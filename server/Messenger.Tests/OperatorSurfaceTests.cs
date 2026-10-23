using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Messenger.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Messenger.Tests;

[Trait("Category", "Unit")]
public sealed class OperatorSurfaceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task ClosedSurfaceReturns404EvenWithOperatorKey(string? enabled)
    {
        await using var host = new SurfaceHost(enabled);
        using var client = host.CreateClient();
        foreach (var path in OperatorPaths)
        {
            using var request = Request(path);
            request.Headers.Add("X-Dev-Provisioning-Token", SurfaceHost.OperatorKey);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var openapi = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.NotFound, openapi.StatusCode);
        Assert.Equal(0, host.Database.Attempts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-operator-key")]
    public async Task EnabledSurfaceStillRequiresOperatorKeyBeforeDatabase(string? key)
    {
        await using var host = new SurfaceHost("true");
        using var client = host.CreateClient();
        foreach (var path in OperatorPaths)
        {
            using var request = Request(path);
            if (key is not null) request.Headers.Add("X-Dev-Provisioning-Token", key);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var openapi = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.Unauthorized, openapi.StatusCode);
        Assert.Equal(0, host.Database.Attempts);
    }

    [Fact]
    public async Task EnabledRealProviderNeverMapsFakeCodeReader()
    {
        await using var host = new SurfaceHost("true", realProvider: true);
        using var client = host.CreateClient();
        using var request = Request("/api/v1/dev/sms/code");
        request.Headers.Add("X-Dev-Provisioning-Token", SurfaceHost.OperatorKey);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Database.Attempts);
    }

    [Fact]
    public async Task DisabledSurfaceDoesNotNeedOperatorSecretOrDisableUserApi()
    {
        await using var host = new SurfaceHost("false", operatorKey: "");
        using var client = host.CreateClient();
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        using var me = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        // Malformed body proves that the public SMS route remains mapped, without sending any SMS.
        using var sms = await client.PostAsync("/api/v1/auth/sms/challenges", new StringContent("{", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, sms.StatusCode);
        Assert.Empty(host.Services.GetRequiredService<FakeSms>().Sent);
        Assert.Equal(0, host.Database.Attempts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short-key")]
    public async Task OptInWithoutValidOperatorSecretFailsAtStartup(string key)
    {
        await using var host = new SurfaceHost("true", operatorKey: key);
        var error = Assert.Throws<InvalidOperationException>(() => host.CreateClient());
        Assert.Contains("Dev:ProvisioningKey", error.Message);
        Assert.Equal(0, host.Database.Attempts);
    }

    [Fact]
    public async Task InvalidFlagFailsAtStartupInsteadOfEnablingSurface()
    {
        await using var host = new SurfaceHost("yes");
        var error = Assert.Throws<InvalidOperationException>(() => host.CreateClient());
        Assert.Contains("Dev:EnableOperatorEndpoints", error.Message);
        Assert.Equal(0, host.Database.Attempts);
    }

    [Fact]
    public async Task OptInCannotBypassDevelopmentOnlyGuard()
    {
        await using var host = new SurfaceHost("true", environment: "Production");
        var error = Assert.Throws<InvalidOperationException>(() => host.CreateClient());
        Assert.Contains("development-only", error.Message);
        Assert.Equal(0, host.Database.Attempts);
    }

    private static readonly string[] OperatorPaths =
    [
        "/api/v1/dev/accounts",
        "/api/v1/dev/accounts/00000000-0000-0000-0000-000000000001/replace-device",
        "/api/v1/dev/accounts/00000000-0000-0000-0000-000000000001/session",
        "/api/v1/dev/sms/code"
    ];

    private static HttpRequestMessage Request(string path) => new(HttpMethod.Post, path)
    {
        Content = path.EndsWith("/sms/code", StringComparison.Ordinal)
            ? JsonContent.Create(new DevSmsCodeRequest(Guid.NewGuid()))
            : JsonContent.Create(new ProvisionRequest("Synthetic", Guid.NewGuid(), 1, new byte[33]))
    };

    private sealed class SurfaceHost(string? enabled, string operatorKey = SurfaceHost.OperatorKey, bool realProvider = false,
        string environment = "Development") : WebApplicationFactory<Program>
    {
        public const string OperatorKey = "SYNTHETIC-OPERATOR-KEY-NOT-A-SECRET-0000000000";
        public ForbiddenDatabase Database { get; } = new();
        private readonly string protection = Path.Combine(Path.GetTempPath(), "os-surface-test-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            foreach (var name in new[] { "SigningKey", "PhoneIndexKey", "SmsCodeKey", "PinPepper" })
                builder.UseSetting("Auth:" + name, Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
            if (enabled is not null) builder.UseSetting("Dev:EnableOperatorEndpoints", enabled);
            builder.UseSetting("Dev:ProvisioningKey", operatorKey);
            builder.UseSetting("Dev:AutoMigrate", "false");
            builder.UseSetting("Dev:DataProtectionPath", protection);
            builder.UseSetting("Auth:Sms:Provider", realProvider ? "SmsRu" : "Fake");
            builder.UseSetting("Auth:Sms:SmsRu:ApiKey", "SYNTHETIC-NEVER-SENT");
            builder.UseSetting("ConnectionStrings:Messenger", "Host=127.0.0.1;Port=1;Database=forbidden;Username=synthetic;Password=synthetic");
            builder.ConfigureTestServices(services =>
            {
                // These are routing/auth tests. Any database access fails, rather than using a database substitute.
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) &&
                             d.ImplementationType == typeof(QueueWorker)).ToArray()) services.Remove(descriptor);
                services.AddDbContext<MessengerDb>(o => o.AddInterceptors(Database));
                services.AddHttpClient(SmsRuSender.HttpClientName).AddHttpMessageHandler(() => new ForbiddenNetwork());
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(protection)) Directory.Delete(protection, recursive: true);
        }
    }

    private sealed class ForbiddenDatabase : DbConnectionInterceptor
    {
        public int Attempts;
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Attempts);
            throw new InvalidOperationException("Database access forbidden in routing test.");
        }
        public override InterceptionResult ConnectionOpening(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result)
        {
            Interlocked.Increment(ref Attempts);
            throw new InvalidOperationException("Database access forbidden in routing test.");
        }
    }
}
