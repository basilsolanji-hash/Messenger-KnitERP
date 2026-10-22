using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Messenger.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
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
            if (!await Access.IsActive(db, Access.Current(context.Principal!), context.HttpContext.RequestAborted))
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
app.UseExceptionHandler(handler => handler.Run(async ctx =>
{
    var exception = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = exception is ApiFault f ? f.Status : exception is BadHttpRequestException ? 400 : 500;
    var code = exception is ApiFault af ? af.Code : status == 400 ? "INVALID_REQUEST" : "INTERNAL_ERROR";
    ctx.Response.StatusCode = status;
    await Results.Problem(statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = ctx.TraceIdentifier }).ExecuteAsync(ctx);
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
    var old = await db.Devices.FromSqlInterpolated($"SELECT * FROM devices WHERE \"AccountId\" = {accountId} AND \"Active\" = TRUE ORDER BY \"Id\" FOR UPDATE").SingleAsync(ct);
    Transport.Require(!await db.Devices.AnyAsync(x => x.Id == request.DeviceId, ct), "DEVICE_ALREADY_EXISTS", 409);
    old.Active = false; account.Generation++;
    await db.SaveChangesAsync(ct); // Satisfy the one-active-device unique index before insert.
    var device = new Device { Id = request.DeviceId, AccountId = accountId, Generation = account.Generation, RegistrationId = request.RegistrationId, IdentityKey = request.IdentityKey };
    db.Devices.Add(device);
    await db.Envelopes.Where(x => x.RecipientDeviceId == old.Id).ExecuteUpdateAsync(x => x.SetProperty(e => e.Ciphertext, (byte[]?)null), ct);
    await db.Notifications.Where(x => x.DeviceId == old.Id).ExecuteDeleteAsync(ct);
    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    registry.Revoke(old.Id);
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

var api = app.MapGroup("/api/v1").RequireAuthorization();
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
