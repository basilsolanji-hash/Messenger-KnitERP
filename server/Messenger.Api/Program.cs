using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Messenger.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
// This milestone intentionally has no production registration. An environment flag cannot accidentally enable it.
if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Milestone 0 is development-only. Production registration is not implemented.");
var key = Access.Key(builder.Configuration);
if (builder.Configuration["Dev:ProvisioningKey"] is not { Length: >= 40 })
    throw new InvalidOperationException("Configure a random Dev:ProvisioningKey using scripts/init-dev.py.");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<MessengerDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Messenger")
    ?? throw new InvalidOperationException("Missing PostgreSQL connection string.")));
builder.Services.AddScoped<Transport>();
builder.Services.AddSingleton<Access>();
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<AuthSecrets>();
builder.Services.AddSingleton<PhonePolicy>();
builder.Services.AddSingleton<PinHasher>();
builder.Services.AddScoped<AuthService>();
switch (builder.Configuration["Auth:Sms:Provider"])
{
    // The fake keeps codes in memory and must never serve real users.
    case "Fake" when builder.Environment.IsDevelopment():
        builder.Services.AddSingleton<FakeSms>();
        builder.Services.AddSingleton<ISmsSender>(s => s.GetRequiredService<FakeSms>());
        break;
    case "SmsRu":
        if (builder.Configuration["Auth:Sms:SmsRu:ApiKey"] is not { Length: > 0 })
            throw new InvalidOperationException("Set Auth__Sms__SmsRu__ApiKey in the server environment.");
        builder.Services.AddHttpClient<ISmsSender, SmsRuSender>(SmsRuSender.HttpClientName, c =>
        {
            c.BaseAddress = new Uri("https://sms.ru/");
            c.Timeout = TimeSpan.FromSeconds(15);
        });
        break;
    default:
        throw new InvalidOperationException("Set Auth:Sms:Provider to Fake (Development only) or SmsRu.");
}
_ = new AuthSecrets(builder.Configuration);
foreach (var region in new PhonePolicy(builder.Configuration).Regions)
    if (builder.Configuration.GetValue<long?>("Auth:Sms:CostKopecks:" + region) is not > 0)
        throw new InvalidOperationException("Configure Auth:Sms:CostKopecks for every allowed region.");
if (builder.Configuration.GetValue<long?>("Auth:Sms:Limits:DailyBudgetKopecks") is not > 0)
    throw new InvalidOperationException("Configure Auth:Sms:Limits:DailyBudgetKopecks.");
builder.Services.AddMessengerForwardedHeaders(builder.Configuration);
builder.Services.AddHostedService<QueueWorker>();
builder.Services.AddSignalR(o => { o.MaximumReceiveMessageSize = 4096; o.EnableDetailedErrors = false; });
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddDataProtection().SetApplicationName("kniterp-messenger-dev")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["Dev:DataProtectionPath"] ?? ".secrets/dataprotection"));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new()
    {
        ValidateIssuer = true, ValidIssuer = Access.Issuer, ValidateAudience = true, ValidAudience = Access.Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateLifetime = true, ClockSkew = TimeSpan.Zero, ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
    };
    o.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<MessengerDb>();
            var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            if (!await Access.IsSessionActive(db, context.Principal!, now, context.HttpContext.RequestAborted))
                context.Fail("Device revoked.");
        }
    };
    // Only Authorization header. No access_token query string and no token logging.
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c =>
        RateLimitPartition.GetFixedWindowLimiter(c.User.FindFirst("device")?.Value ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("send", c => RateLimitPartition.GetTokenBucketLimiter(c.User.FindFirst("device")?.Value ?? "anonymous",
        _ => new TokenBucketRateLimiterOptions { TokenLimit = 20, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromSeconds(1), QueueLimit = 0 }));
});
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024 * 1024);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler(handler => handler.Run(async ctx =>
{
    var exception = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = exception is ApiFault f ? f.Status : exception is BadHttpRequestException ? 400 : 500;
    var code = exception is ApiFault af ? af.Code : status == 400 ? "INVALID_REQUEST" : "INTERNAL_ERROR";
    ctx.Response.StatusCode = status;
    var extensions = new Dictionary<string, object?> { ["code"] = code, ["traceId"] = ctx.TraceIdentifier };
    if (exception is ApiFault { RetryAfter: { } retry })
    {
        var seconds = (long)Math.Ceiling(Math.Max(1, retry.TotalSeconds));
        ctx.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        extensions["retryAfterSeconds"] = seconds;
    }
    if (exception is ApiFault { AttemptsRemaining: { } attempts }) extensions["attemptsRemaining"] = attempts;
    await Results.Problem(statusCode: status, title: code, extensions: extensions).ExecuteAsync(ctx);
}));
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapGet("/health/live", () => Results.Ok(new { status = "up", milestone = "0", productionReady = false }));
app.MapGet("/health/ready", async (MessengerDb db, CancellationToken ct) => await db.Database.CanConnectAsync(ct)
    ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
app.MapOpenApi().RequireAuthorization();

app.MapPost("/api/v1/dev/accounts", async (HttpContext context, ProvisionRequest request, MessengerDb db, Access access, IConfiguration config, CancellationToken ct) =>
{
    Access.ProvisioningKey(context, config);
    ValidateProvision(request);
    var account = new Account { Id = Guid.NewGuid(), DisplayName = request.DisplayName.Trim() };
    var device = new Device { Id = request.DeviceId, AccountId = account.Id, Generation = 1, IdentityKey = request.IdentityKey, RegistrationId = request.RegistrationId };
    Transport.Require(!await db.Devices.AnyAsync(x => x.Id == device.Id, ct), "DEVICE_ALREADY_EXISTS", 409);
    db.Accounts.Add(account); db.Devices.Add(device);
    await db.SaveChangesAsync(ct);
    return Results.Ok(access.Issue(account, device));
});
app.MapPost("/api/v1/dev/accounts/{accountId:guid}/replace-device", async (Guid accountId, HttpContext ctx, ProvisionRequest request,
    MessengerDb db, Access access, IConfiguration config, ConnectionRegistry registry, CancellationToken ct) =>
{
    Access.ProvisioningKey(ctx, config);
    ValidateProvision(request);
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    var rows = await db.Accounts.FromSqlInterpolated($"SELECT * FROM accounts WHERE \"Id\" = {accountId} FOR UPDATE").ToListAsync(ct);
    Transport.Require(rows.Count == 1, "NOT_FOUND", 404);
    var account = rows[0];
    Transport.Require(!await db.Devices.AnyAsync(x => x.Id == request.DeviceId, ct), "DEVICE_ALREADY_EXISTS", 409);
    var device = new Device { Id = request.DeviceId, AccountId = accountId, RegistrationId = request.RegistrationId, IdentityKey = request.IdentityKey };
    var old = await AuthService.ReplaceDevice(db, account, device, TimeProvider.System.GetUtcNow(), ct);
    Transport.Require(old is not null, "NOT_FOUND", 404);
    await tx.CommitAsync(ct);
    registry.Revoke(old!.Value);
    return Results.Ok(access.Issue(account, device));
});

// Operator-only renewal for the SAME synthetic phone and keys. No admin credential reaches Android.
app.MapPost("/api/v1/dev/accounts/{accountId:guid}/session", async (Guid accountId, HttpContext context,
    ProvisionRequest request, MessengerDb db, Access access, IConfiguration config, CancellationToken ct) =>
{
    Access.ProvisioningKey(context, config);
    ValidateProvision(request);
    var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId, ct);
    var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.DeviceId && x.AccountId == accountId, ct);
    Transport.Require(account is not null && device is not null && device.Active && device.Generation == account.Generation &&
        device.RegistrationId == request.RegistrationId && device.IdentityKey.SequenceEqual(request.IdentityKey), "DEVICE_MISMATCH", 409);
    return Results.Ok(access.Issue(account!, device!));
});

// Synthetic test stand only: reads a code the in-memory fake provider "sent". Not mapped with a real provider,
// requires the operator key, and takes the challenge id in the body so no number or code appears in a URL.
if (app.Services.GetService<FakeSms>() is { } fakeSms)
    app.MapPost("/api/v1/dev/sms/code", async (HttpContext context, DevSmsCodeRequest request, MessengerDb db,
        AuthSecrets secrets, IConfiguration config, CancellationToken ct) =>
    {
        Access.ProvisioningKey(context, config);
        var challenge = await db.AuthChallenges.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ChallengeId &&
            x.Operation == ChallengeOperation.Activate, ct);
        var sent = challenge?.PhoneIndex is { } index ? fakeSms.Sent.LastOrDefault(x =>
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(secrets.PhoneIndex(x.Phone), index)) : default;
        var code = sent.Text is null ? null : System.Text.RegularExpressions.Regex.Match(sent.Text, @"\b\d{6}\b").Value;
        Transport.Require(code is { Length: 6 }, "NOT_FOUND", 404);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { code });
    });

// Public auth API. Separate from /api/v1/dev; its presence does not lift the Development-only guard above.
var auth = app.MapGroup("/api/v1/auth").AddEndpointFilter(async (context, next) =>
{
    context.HttpContext.Response.Headers.CacheControl = "no-store";
    return await next(context);
});
auth.MapPost("/sms/challenges", async (HttpContext c, SmsChallengeRequest r, AuthService s, CancellationToken ct) =>
    Results.Ok(await s.CreateSmsChallenge(r, c.Connection.RemoteIpAddress, ct)));
auth.MapPost("/sms/verify", async (SmsVerifyRequest r, AuthService s, CancellationToken ct) => Results.Ok(await s.VerifySms(r, ct)));
auth.MapPost("/pin/verify", async (PinVerifyRequest r, AuthService s, CancellationToken ct) => Results.Ok(await s.VerifyPin(r, ct)));
auth.MapPost("/refresh/challenges", async (HttpContext c, RefreshChallengeRequest r, AuthService s, CancellationToken ct) =>
    Results.Ok(await s.CreateRefreshChallenge(r, c.Connection.RemoteIpAddress, ct)));
auth.MapPost("/refresh", async (RefreshRequest r, AuthService s, CancellationToken ct) => Results.Ok(await s.Refresh(r, ct)));
// Recovery codes are not implemented. Losing the PIN blocks transfer; there is no support or SMS bypass.
auth.MapPost("/recovery", IResult () => throw new ApiFault(501, "RECOVERY_NOT_AVAILABLE"));

var api = app.MapGroup("/api/v1").RequireAuthorization();
api.MapPost("/auth/logout", async (HttpContext c, AuthService s, CancellationToken ct) =>
{ await s.Logout(c.User, ct); return Results.NoContent(); });
api.MapPut("/account/pin", async (HttpContext c, SetPinRequest r, AuthService s, CancellationToken ct) =>
{ await s.SetPin(c.User, r, ct); return Results.NoContent(); });
api.MapPost("/account/pin/disable", async (HttpContext c, DisablePinRequest r, AuthService s, CancellationToken ct) =>
{ await s.DisablePin(c.User, r, ct); return Results.NoContent(); });
api.MapGet("/me", async (HttpContext c, MessengerDb db, CancellationToken ct) =>
{
    var actor = Access.Current(c.User);
    var account = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == actor.AccountId, ct);
    var device = await db.Devices.AsNoTracking().SingleAsync(x => x.Id == actor.DeviceId, ct);
    var expiration = long.Parse(c.User.FindFirst("exp")!.Value, CultureInfo.InvariantCulture);
    return new { actor.AccountId, actor.DeviceId, actor.Generation, account.DisplayName,
        device.RegistrationId, device.IdentityKey, ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expiration) };
});
api.MapPost("/keys/bundles", async (HttpContext c, PublishBundleRequest r, Transport t, CancellationToken ct) =>
{ await t.Publish(Access.Current(c.User), r, ct); return Results.NoContent(); });
api.MapPost("/keys/claim", async (HttpContext c, ClaimRequest r, Transport t, CancellationToken ct) =>
    Results.Ok(await t.Claim(Access.Current(c.User), r, ct)));
api.MapPost("/messages", async (HttpContext c, SendRequest r, Transport t, CancellationToken ct) =>
    Results.Ok(await t.Send(Access.Current(c.User), r, ct))).RequireRateLimiting("send");
api.MapGet("/inbox", async (string? cursor, HttpContext c, Transport t, IDataProtectionProvider protection, CancellationToken ct) =>
{
    var actor = Access.Current(c.User);
    var protector = protection.CreateProtector("inbox-v1", actor.DeviceId.ToString(), actor.Generation.ToString(CultureInfo.InvariantCulture));
    long after = 0;
    if (cursor is not null)
    {
        try { after = long.Parse(protector.Unprotect(cursor), CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException or OverflowException)
        { throw new ApiFault(400, "INVALID_CURSOR"); }
        Transport.Require(after >= 0, "INVALID_CURSOR");
    }
    var items = await t.Inbox(actor, after, ct);
    return new InboxPage(items, items.Length == 0 ? null : protector.Protect(items[^1].Sequence.ToString(CultureInfo.InvariantCulture)));
});
api.MapPost("/inbox/ack", async (HttpContext c, AckRequest r, Transport t, CancellationToken ct) =>
{ await t.Ack(Access.Current(c.User), r, ct); return Results.NoContent(); });
api.MapPost("/blocks", async (HttpContext c, BlockRequest r, MessengerDb db, CancellationToken ct) =>
{
    var actor = Access.Current(c.User);
    Transport.Require(r.AccountId != actor.AccountId && await db.Accounts.AnyAsync(x => x.Id == r.AccountId, ct));
    // Upsert is idempotent and safe under concurrent retries.
    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO blocks (\"OwnerId\", \"TargetId\") VALUES ({actor.AccountId}, {r.AccountId}) ON CONFLICT DO NOTHING", ct);
    return Results.NoContent();
});
app.MapHub<EventsHub>("/hubs/events", o => o.CloseOnAuthenticationExpiration = true);

if (app.Configuration.GetValue<bool>("Dev:AutoMigrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<MessengerDb>().Database.MigrateAsync();
}
app.Run();

static void ValidateProvision(ProvisionRequest r)
{
    Transport.Require(!string.IsNullOrWhiteSpace(r.DisplayName) && r.DisplayName.EnumerateRunes().Count() <= 64 && !r.DisplayName.Any(char.IsControl));
    Transport.Require(r.DeviceId != Guid.Empty && r.RegistrationId is > 0 and <= 0x3fff && r.IdentityKey is { Length: 33 });
}
public partial class Program { }
