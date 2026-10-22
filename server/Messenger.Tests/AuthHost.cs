using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Messenger.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Messenger.Tests;

// One isolated PostgreSQL schema shared by several real API instances, like two processes behind a balancer.
public sealed class AuthDatabase : IAsyncDisposable
{
    public string Schema { get; } = "test_" + Guid.NewGuid().ToString("N");
    public string Connection { get; private set; } = "";
    private string admin = "";

    public static async Task<AuthDatabase> Create()
    {
        var result = new AuthDatabase();
        result.admin = Environment.GetEnvironmentVariable("TEST_DATABASE")
            ?? throw new InvalidOperationException("Set TEST_DATABASE to an isolated PostgreSQL test database.");
        await result.Command("CREATE SCHEMA ");
        result.Connection = new Npgsql.NpgsqlConnectionStringBuilder(result.admin) { SearchPath = result.Schema }.ConnectionString;
        await using var db = result.Open();
        await db.Database.MigrateAsync();
        return result;
    }

    public MessengerDb Open() => new(new DbContextOptionsBuilder<MessengerDb>()
        .UseNpgsql(Connection, n => n.MigrationsHistoryTable("__EFMigrationsHistory", Schema)).Options);

    private async Task Command(string prefix)
    {
        await using var connection = new Npgsql.NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = prefix + new Npgsql.NpgsqlCommandBuilder().QuoteIdentifier(Schema) + (prefix.StartsWith("DROP") ? " CASCADE" : "");
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Npgsql.NpgsqlConnection.ClearAllPools();
        await Command("DROP SCHEMA IF EXISTS ");
    }
}

public sealed class MutableClock : TimeProvider
{
    private TimeSpan offset;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + offset;
    public void Advance(TimeSpan by) => offset += by;
    public void Reset() => offset = TimeSpan.Zero;
}

public sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string category) => new Logger(this, category);
    public void Dispose() { }
    private sealed class Logger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e, Func<TState, Exception?, string> format) =>
            owner.Lines.Enqueue(category + " " + format(state, e) + " " + e);
    }
}

// Counts any attempt to reach the real SMS.ru client. Tests must keep it at zero.
public sealed class ForbiddenNetwork : DelegatingHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        throw new InvalidOperationException("Real SMS provider must not be called in tests.");
    }
}

public sealed class AuthHost : WebApplicationFactory<Program>
{
    public static readonly Dictionary<string, string> Secrets = new()
    {
        ["Auth:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
        ["Auth:PhoneIndexKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["Auth:SmsCodeKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["Auth:PinPepper"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["Dev:ProvisioningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
    };
    private readonly AuthDatabase database;
    private readonly IReadOnlyDictionary<string, string> extra;
    public MutableClock Clock { get; }
    public LogCapture Logs { get; } = new();
    public ForbiddenNetwork Network { get; } = new();

    public AuthHost(AuthDatabase database, MutableClock clock, IReadOnlyDictionary<string, string>? extra = null)
    {
        this.database = database;
        Clock = clock;
        this.extra = extra ?? new Dictionary<string, string>();
    }

    public FakeSms Sms => Services.GetRequiredService<FakeSms>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        foreach (var (k, v) in Secrets) builder.UseSetting(k, v);
        builder.UseSetting("ConnectionStrings:Messenger", database.Connection);
        builder.UseSetting("Dev:DataProtectionPath", Path.Combine(Path.GetTempPath(), "kniterp-test-dp-" + database.Schema));
        builder.UseSetting("Auth:Sms:Provider", "Fake");
        builder.UseSetting("Auth:Pin:Argon2MemoryKiB", "19456"); // Faster tests; production parameters are measured separately.
        builder.UseSetting("Auth:Pin:Argon2Ops", "3"); // libsodium's Argon2id minimum.
        builder.UseSetting("Logging:LogLevel:Default", "Trace");
        foreach (var (k, v) in extra) builder.UseSetting(k, v);
        builder.ConfigureLogging(l => l.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(Clock);
            s.AddSingleton<IStartupFilter>(new TestAddress());
            s.AddHttpClient(SmsRuSender.HttpClientName).AddHttpMessageHandler(() => Network);
        });
    }

    // TestServer has no socket peer. The test sets the peer address the way a TCP connection would.
    private sealed class TestAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Peer", out var peer)) context.Connection.RemoteIpAddress = IPAddress.Parse(peer!);
                context.Request.Headers.Remove("X-Test-Peer");
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

public sealed class TestDevice : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public int RegistrationId { get; } = RandomNumberGenerator.GetInt32(1, 0x3fff);
    public byte[] IdentityKey { get; } = [5, .. RandomNumberGenerator.GetBytes(32)];
    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public byte[] AuthKey => Key.ExportSubjectPublicKeyInfo();
    public byte[] Sign(byte[] payload) => Key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    public void Dispose() => Key.Dispose();
}

public sealed record Problem(int Status, string Code, int? AttemptsRemaining, long? RetryAfterSeconds);

public static class Api
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<HttpResponseMessage> Post(HttpClient client, string path, object body, string? peer = null, string? bearer = null, string? forwarded = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
        if (peer is not null) request.Headers.Add("X-Test-Peer", peer);
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        if (forwarded is not null) request.Headers.Add("X-Forwarded-For", forwarded);
        return await client.SendAsync(request);
    }

    public static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, Json)!;
    }

    public static async Task<Problem> Fails(HttpResponseMessage response, string code)
    {
        var problem = await ProblemOf(response);
        Assert.Equal(code, problem.Code);
        return problem;
    }

    public static async Task<Problem> ProblemOf(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.False(response.IsSuccessStatusCode, text);
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        return new((int)response.StatusCode, root.GetProperty("code").GetString()!,
            root.TryGetProperty("attemptsRemaining", out var a) ? a.GetInt32() : null,
            root.TryGetProperty("retryAfterSeconds", out var r) ? r.GetInt64() : null);
    }

    public static string LastCode(FakeSms sms, string phone) =>
        Regex.Match(sms.Sent.Last(x => x.Phone == phone).Text, @"\d{6}").Value;

    public static SmsChallengeRequest Challenge(TestDevice device, string phone, Guid? requestId = null) =>
        new(requestId ?? Guid.NewGuid(), phone, device.Id, device.RegistrationId, device.IdentityKey, device.AuthKey);

    public static byte[] ActivationProof(TestDevice device, ChallengeResponse c, string phone) => device.Sign(DeviceProof.Payload(
        ChallengeOperation.Activate, c.ChallengeId, c.Nonce, c.ExpiresAt, phone, null, null, device.Id, device.RegistrationId, device.IdentityKey, device.AuthKey));

    public static SmsVerifyRequest Verify(TestDevice device, ChallengeResponse c, string phone, string code) =>
        new(c.ChallengeId, phone, code, ActivationProof(device, c, phone));

    public static Guid TokenId(string refresh) => Guid.Parse(refresh.Split('.')[1]);
}
