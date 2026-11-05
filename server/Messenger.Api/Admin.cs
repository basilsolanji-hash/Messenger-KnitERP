using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Api;

/// <summary>RFC 6238 TOTP (HMAC-SHA1, 6 digits, 30 s) and RFC 4648 Base32 for authenticator apps.</summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public static long Step(DateTimeOffset now) => now.ToUnixTimeSeconds() / StepSeconds;
    public static string Code(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }
    /// <summary>The matching step within ±1 of now, or null. Constant-time comparison per candidate.</summary>
    public static long? Match(byte[] key, string? code, DateTimeOffset now)
    {
        if (code is null) return null;
        code = code.Replace(" ", "", StringComparison.Ordinal);
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return null;
        var step = Step(now);
        long? found = null;
        foreach (var candidate in new[] { step - 1, step, step + 1 })
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(key, candidate)), Encoding.ASCII.GetBytes(code)))
                found = candidate;
        return found;
    }
    public static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
    public static string Uri(string login, byte[] key) =>
        $"otpauth://totp/{System.Uri.EscapeDataString("Ось коммуникации:" + login)}?secret={Base32(key)}&issuer={System.Uri.EscapeDataString("Ось коммуникации")}&algorithm=SHA1&digits=6&period=30";
}

/// <summary>Credentials of service administrators: Argon2id passwords with a server pepper and Data-Protected TOTP keys.</summary>
public sealed class AdminCredentials(AuthSecrets secrets, IConfiguration config, IDataProtectionProvider protection)
{
    private readonly long ops = config.GetValue("Auth:Pin:Argon2Ops", 3L);
    private readonly int memory = config.GetValue("Auth:Pin:Argon2MemoryKiB", 65536) * 1024;
    private readonly IDataProtector totp = protection.CreateProtector("admin-totp-v1");
    private readonly SemaphoreSlim slots = new(2);
    // Same cost for unknown logins, so response time does not reveal which logins exist.
    private readonly Lazy<string> dummy = new(() => Sodium.PasswordHash.ArgonHashString("dummy-" + Guid.NewGuid(),
        config.GetValue("Auth:Pin:Argon2Ops", 3L), config.GetValue("Auth:Pin:Argon2MemoryKiB", 65536) * 1024));

    private string Input(string password) => Convert.ToBase64String(AuthSecrets.Mac(secrets.PinPepper, "admin-password-v1", password));
    public static bool IsStrong(string? password) => password is { Length: >= 12 and <= 128 } && !password.Any(char.IsControl);
    public async Task<string> Hash(string password, CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        try { return Sodium.PasswordHash.ArgonHashString(Input(password), ops, memory); } finally { slots.Release(); }
    }
    public async Task<bool> Verify(string? hash, string password, CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        try { return Sodium.PasswordHash.ArgonHashStringVerify(hash ?? dummy.Value, Input(password)) && hash is not null; }
        finally { slots.Release(); }
    }
    public string ProtectTotp(byte[] key) => totp.Protect(Convert.ToBase64String(key));
    public byte[] TotpKey(string protectedKey) => Convert.FromBase64String(totp.Unprotect(protectedKey));
    /// <summary>A fresh password (16 characters from a 56-symbol set, about 93 bits) and TOTP key; shown once.</summary>
    public static (string Password, byte[] Totp) NewSecrets()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var password = new string(Enumerable.Range(0, 16).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
        return (password, RandomNumberGenerator.GetBytes(20));
    }
}

/// <summary>
/// Service administration panel (PRODUCT_SPEC section 17, docs/ADMIN_PANEL.md, docs/ADMIN_API.md). Mapped only with
/// Admin:Enabled=true; Nginx serves it to administrator addresses only. Never returns message content, keys, tokens,
/// PIN or SMS codes; phone numbers are not stored by the server at all.
/// </summary>
public static class AdminApi
{
    public const string Cookie = "__Host-os-admin";
    public const string CsrfHeader = "X-Admin-Csrf";
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan Absolute = TimeSpan.FromHours(8);
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    public sealed record LoginRequest(string Login, string Password, string Code);
    public sealed record Me(string Login, string Role, string Csrf, DateTimeOffset ExpiresAt);
    public sealed record ReasonRequest(string Reason);
    public sealed record PhoneSearch(string Phone);
    public sealed record PasswordChange(string Current, string Next);
    public sealed record NewUser(string Login, string Role);
    public sealed record RoleChange(string Role);
    public sealed record Issued(Guid Id, string Login, string Password, string OtpAuthUri);
    public sealed record AccountRow(Guid AccountId, DateTimeOffset? CreatedAt, int Devices, int? ActiveGeneration, bool PinEnabled,
        bool Suspended, string? SuspendReason, string Kind);
    public sealed record DeviceRow(Guid DeviceId, int Generation, bool Active, string Kind, int LiveSessions);
    public sealed record AccountDetail(AccountRow Account, IReadOnlyList<DeviceRow> Devices, int QueuedForAccount, int BlockedByOthers, int BlocksMade);
    public sealed record Metrics(int Accounts, int Suspended, int ActiveDevices, int LiveSessions, int QueuedEnvelopes,
        long SmsSpentKopecksToday, long SmsBudgetKopecks, int AdminSessions, DateTimeOffset ServerTime);
    public sealed record AuditRow(long Id, DateTimeOffset At, string Login, string Remote, string Action, string Target, string Detail, bool Success);
    public sealed record UserRow(Guid Id, string Login, string Role, bool Disabled, bool Locked, DateTimeOffset CreatedAt);

    private sealed record Actor(AdminUser User, AdminSession Session, byte[] Token);

    public static void Map(WebApplication app)
    {
        app.MapGet("/admin", (HttpContext c) => Static(c, AdminPage.Html, "text/html; charset=utf-8"));
        app.MapGet("/admin/app.js", (HttpContext c) => Static(c, AdminPage.Script, "text/javascript; charset=utf-8"));

        var api = app.MapGroup("/api/v1/admin");
        api.MapPost("/login", Login);
        api.MapPost("/logout", async (HttpContext c, MessengerDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var a = await Authenticate(c, db, clock, ct, mutate: true);
            a.Session.RevokedAt = clock.GetUtcNow();
            await Audit(db, c, a.User, "logout", a.User.Login, "", true, ct);
            Expire(c);
            return Results.NoContent();
        });
        api.MapGet("/me", async (HttpContext c, MessengerDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var a = await Authenticate(c, db, clock, ct);
            return Results.Ok(new Me(a.User.Login, Name(a.User.Role), Csrf(a.Token), Min(a.Session.ExpiresAt, a.Session.LastSeenAt + Idle)));
        });
        api.MapPost("/me/password", async (HttpContext c, PasswordChange r, MessengerDb db, TimeProvider clock, AdminCredentials creds, CancellationToken ct) =>
        {
            var a = await Authenticate(c, db, clock, ct, mutate: true);
            Transport.Require(AdminCredentials.IsStrong(r.Next), "PASSWORD_WEAK");
            if (!await creds.Verify(a.User.PasswordHash, r.Current ?? "", ct))
            {
                await Audit(db, c, a.User, "password-change", a.User.Login, "wrong current password", false, ct);
                throw new ApiFault(403, "PASSWORD_INVALID");
            }
            a.User.PasswordHash = await creds.Hash(r.Next, ct);
            await RevokeOtherSessions(db, a.User.Id, a.Session.TokenHash, clock.GetUtcNow(), ct);
            await Audit(db, c, a.User, "password-change", a.User.Login, "other sessions ended", true, ct);
            return Results.NoContent();
        });

        api.MapGet("/metrics", async (HttpContext c, MessengerDb db, TimeProvider clock, AuthSecrets secrets, IConfiguration config, CancellationToken ct) =>
        {
            await Authenticate(c, db, clock, ct, roles: [AdminRole.Admin, AdminRole.Support, AdminRole.Auditor]);
            var now = clock.GetUtcNow();
            var day = now.ToUnixTimeSeconds() / 86400 * 86400;
            var budgetKey = secrets.CounterKey("sms-budget", "all") + ":86400:" + day.ToString(CultureInfo.InvariantCulture);
            var spent = await db.AuthCounters.AsNoTracking().Where(x => x.Key == budgetKey).Select(x => (long?)x.Count).SingleOrDefaultAsync(ct) ?? 0;
            return Results.Ok(new Metrics(await db.Accounts.CountAsync(ct), await db.Accounts.CountAsync(x => x.SuspendedAt != null, ct),
                await db.Devices.CountAsync(x => x.Active, ct),
                await db.RefreshFamilies.CountAsync(x => x.RevokedAt == null && x.AbsoluteExpiresAt > now, ct),
                await db.Envelopes.CountAsync(x => !x.Acknowledged && x.Ciphertext != null && x.ExpiresAt > now, ct),
                spent, config.GetValue("Auth:Sms:Limits:DailyBudgetKopecks", 0L),
                await db.AdminSessions.CountAsync(x => x.RevokedAt == null && x.ExpiresAt > now && x.LastSeenAt > now - Idle, ct), now));
        });

        api.MapGet("/accounts", async (HttpContext c, string? q, Guid? after, MessengerDb db, TimeProvider clock, CancellationToken ct) =>
        {
            await Authenticate(c, db, clock, ct, roles: [AdminRole.Admin, AdminRole.Support]);
            var query = db.Accounts.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
            {
                Transport.Require(Guid.TryParse(q.Trim(), out var id), "QUERY_INVALID");
                query = query.Where(x => x.Id == id);
            }
            if (after is { } cursor) query = query.Where(x => x.Id.CompareTo(cursor) > 0);
            var rows = await Rows(db, query.OrderBy(x => x.Id).Take(50), ct);
            return Results.Ok(new { items = rows, next = rows.Count == 50 ? rows[^1].AccountId : (Guid?)null });
        });
        api.MapPost("/accounts/search-phone", async (HttpContext c, PhoneSearch r, MessengerDb db, TimeProvider clock, PhonePolicy phones,
            AuthSecrets secrets, CancellationToken ct) =>
        {
            var a = await Authenticate(c, db, clock, ct, mutate: true, roles: [AdminRole.Admin, AdminRole.Support]);
            await Take(db, secrets, "admin-phone-search", a.User.Id.ToString(), TimeSpan.FromHours(1), 20, clock.GetUtcNow(), ct);
            NormalizedPhone phone;
            try { phone = phones.Normalize(r.Phone); }
            catch (ApiFault) { throw new ApiFault(400, "PHONE_INVALID"); }
            var index = secrets.PhoneIndex(phone.E164);
            var rows = await Rows(db, db.Accounts.AsNoTracking().Where(x => x.PhoneIndex == index), ct);
            // The number itself is never logged: only the fact of a search and its result count.
            await Audit(db, c, a.User, "search-phone", "", $"found {rows.Count}", true, ct);
            return Results.Ok(new { items = rows });
        });
        api.MapGet("/accounts/{id:guid}", async (Guid id, HttpContext c, MessengerDb db, TimeProvider clock, CancellationToken ct) =>
        {
            await Authenticate(c, db, clock, ct, roles: [AdminRole.Admin, AdminRole.Support]);
            var now = clock.GetUtcNow();
            var row = (await Rows(db, db.Accounts.AsNoTracking().Where(x => x.Id == id), ct)).SingleOrDefault();
            Transport.Require(row is not null, "NOT_FOUND", 404);
            var devices = await db.Devices.AsNoTracking().Where(d => d.AccountId == id).OrderByDescending(d => d.Active).ThenByDescending(d => d.Generation)
                .Select(d => new DeviceRow(d.Id, d.Generation, d.Active, d.AuthKey == null ? "synthetic" : "sms",
                    db.RefreshFamilies.Count(f => f.DeviceId == d.Id && f.RevokedAt == null && f.AbsoluteExpiresAt > now))).ToListAsync(ct);
            return Results.Ok(new AccountDetail(row!, devices,
                await db.Envelopes.CountAsync(x => x.RecipientAccountId == id && !x.Acknowledged && x.Ciphertext != null && x.ExpiresAt > now, ct),
                await db.Blocks.CountAsync(x => x.TargetId == id, ct), await db.Blocks.CountAsync(x => x.OwnerId == id, ct)));
        });
        api.MapPost("/accounts/{id:guid}/suspend", (Guid id, HttpContext c, ReasonRequest r, MessengerDb db, TimeProvider clock,
            ConnectionRegistry registry, CancellationToken ct) => AccountAction(id, c, r, db, clock, registry, "suspend", ct));
        api.MapPost("/accounts/{id:guid}/resume", (Guid id, HttpContext c, ReasonRequest r, MessengerDb db, TimeProvider clock,
            ConnectionRegistry registry, CancellationToken ct) => AccountAction(id, c, r, db, clock, registry, "resume", ct));
        api.MapPost("/accounts/{id:guid}/revoke-sessions", (Guid id, HttpContext c, ReasonRequest r, MessengerDb db, TimeProvider clock,
            ConnectionRegistry registry, CancellationToken ct) => AccountAction(id, c, r, db, clock, registry, "revoke-sessions", ct));

        api.MapGet("/audit", async (HttpContext c, long? before, string? action, int? limit, MessengerDb db, TimeProvider clock, CancellationToken ct) =>
        {
            await Authenticate(c, db, clock, ct, roles: [AdminRole.Admin, AdminRole.Auditor]);
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var rows = await AuditQuery(db, before, action).Take(take).ToListAsync(ct);
            return Results.Ok(new { items = rows, next = rows.Count == take ? rows[^1].Id : (long?)null });
        });
        api.MapGet("/audit.csv", async (HttpContext c, string? action, MessengerDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var a = await Authenticate(c, db, clock, ct, roles: [AdminRole.Admin, AdminRole.Auditor]);
            var rows = await AuditQuery(db, null, action).Take(10_000).ToListAsync(ct);
            await Audit(db, c, a.User, "export-audit", action ?? "", $"rows {rows.Count}", true, ct);
            var csv = new StringBuilder("id;at;login;remote;action;target;detail;success\n");
            foreach (var x in rows)
                csv.AppendJoin(';', new[] { x.Id.ToString(CultureInfo.InvariantCulture), x.At.ToString("O"), x.Login, x.Remote, x.Action, x.Target, x.Detail,
                    x.Success ? "1" : "0" }.Select(CsvCell)).Append('\n');
            c.Response.Headers.ContentDisposition = "attachment; filename=\"admin-audit.csv\"";
            return Results.Text(csv.ToString(), "text/csv; charset=utf-8");
        });

        api.MapGet("/users", async (HttpContext c, MessengerDb db, TimeProvider clock, CancellationToken ct) =>
        {
            await Authenticate(c, db, clock, ct, roles: [AdminRole.Admin]);
            var now = clock.GetUtcNow();
            var users = await db.AdminUsers.AsNoTracking().OrderBy(x => x.Login).ToListAsync(ct);
            return Results.Ok(users.Select(u => new UserRow(u.Id, u.Login, Name(u.Role), u.Disabled, u.LockedUntil > now, u.CreatedAt)));
        });
        api.MapPost("/users", async (HttpContext c, NewUser r, MessengerDb db, TimeProvider clock, AdminCredentials creds, CancellationToken ct) =>
        {
            var a = await Authenticate(c, db, clock, ct, mutate: true, roles: [AdminRole.Admin]);
            var issued = await CreateUser(db, creds, r.Login, ParseRole(r.Role), clock.GetUtcNow(), ct);
            await Audit(db, c, a.User, "create-user", issued.Login, r.Role, true, ct);
            NoStore(c);
            return Results.Ok(issued);
        });
        api.MapPost("/users/{id:guid}/role", async (Guid id, HttpContext c, RoleChange r, MessengerDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var a = await Authenticate(c, db, clock, ct, mutate: true, roles: [AdminRole.Admin]);
            var role = ParseRole(r.Role);
            var user = await LockUser(db, id, ct);
            Transport.Require(user.Id != a.User.Id, "SELF_CHANGE_FORBIDDEN", 409);
            if (user.Role == AdminRole.Admin && role != AdminRole.Admin) await RequireAnotherAdmin(db, user.Id, ct);
            user.Role = role;
            await RevokeOtherSessions(db, user.Id, null, clock.GetUtcNow(), ct);
            await Audit(db, c, a.User, "change-role", user.Login, r.Role, true, ct);
            return Results.NoContent();
        });
        api.MapPost("/users/{id:guid}/disable", (Guid id, HttpContext c, MessengerDb db, TimeProvider clock, CancellationToken ct) =>
            UserSwitch(id, c, db, clock, disable: true, ct));
        api.MapPost("/users/{id:guid}/enable", (Guid id, HttpContext c, MessengerDb db, TimeProvider clock, CancellationToken ct) =>
            UserSwitch(id, c, db, clock, disable: false, ct));
        api.MapPost("/users/{id:guid}/reset-credentials", async (Guid id, HttpContext c, MessengerDb db, TimeProvider clock,
            AdminCredentials creds, CancellationToken ct) =>
        {
            var a = await Authenticate(c, db, clock, ct, mutate: true, roles: [AdminRole.Admin]);
            var user = await LockUser(db, id, ct);
            Transport.Require(user.Id != a.User.Id, "SELF_CHANGE_FORBIDDEN", 409);
            var (password, totp) = AdminCredentials.NewSecrets();
            user.PasswordHash = await creds.Hash(password, ct);
            user.TotpSecret = creds.ProtectTotp(totp);
            user.TotpLastStep = 0; user.FailedLogins = 0; user.LockedUntil = null;
            await RevokeOtherSessions(db, user.Id, null, clock.GetUtcNow(), ct);
            await Audit(db, c, a.User, "reset-credentials", user.Login, "", true, ct);
            NoStore(c);
            return Results.Ok(new Issued(user.Id, user.Login, password, Totp.Uri(user.Login, totp)));
        });
    }

    // ---- authentication ----

    private static async Task<IResult> Login(HttpContext c, LoginRequest r, MessengerDb db, TimeProvider clock, AdminCredentials creds,
        AuthSecrets secrets, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (host, network) = ClientAddress.Keys(c.Connection.RemoteIpAddress);
        await Take(db, secrets, "admin-login-ip", host, TimeSpan.FromMinutes(15), 30, now, ct);
        var login = (r.Login ?? "").Trim().ToLowerInvariant();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await db.AdminUsers.FromSqlInterpolated($"SELECT * FROM admin_users WHERE \"Login\" = {login} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (user is { LockedUntil: { } until } && until > now)
        {
            await Audit(db, c, user, "login", login, "locked", false, ct);
            await tx.CommitAsync(ct);
            throw new ApiFault(429, "ADMIN_LOCKED") { RetryAfter = until - now };
        }
        var passwordOk = await creds.Verify(user?.PasswordHash, r.Password ?? "", ct);
        long? step = user is null ? null : Totp.Match(creds.TotpKey(user.TotpSecret), r.Code, now);
        var ok = user is { Disabled: false } && passwordOk && step is { } s && s > user.TotpLastStep;
        if (!ok)
        {
            if (user is not null)
            {
                user.FailedLogins++;
                if (user.FailedLogins >= MaxFailedLogins) { user.LockedUntil = now + Lockout; user.FailedLogins = 0; }
            }
            var why = user is null ? "unknown login" : user.Disabled ? "disabled" : !passwordOk ? "password" : step is null ? "code" : "code reused";
            await Audit(db, c, user, "login", login, why, false, ct);
            await tx.CommitAsync(ct);
            throw new ApiFault(401, "ADMIN_LOGIN_FAILED"); // One answer for every reason.
        }
        user!.FailedLogins = 0; user.LockedUntil = null; user.TotpLastStep = step!.Value;
        var token = RandomNumberGenerator.GetBytes(32);
        db.AdminSessions.Add(new AdminSession { TokenHash = SHA256.HashData(token), AdminId = user.Id, CreatedAt = now, LastSeenAt = now,
            ExpiresAt = now + Absolute, Network = network });
        await Audit(db, c, user, "login", login, "", true, ct);
        await tx.CommitAsync(ct);
        c.Response.Headers.Append("Set-Cookie", $"{Cookie}={Base64Url(token)}; Path=/; Secure; HttpOnly; SameSite=Strict");
        NoStore(c);
        return Results.Ok(new Me(user.Login, Name(user.Role), Csrf(token), now + Idle));
    }

    private static async Task<Actor> Authenticate(HttpContext c, MessengerDb db, TimeProvider clock, CancellationToken ct,
        bool mutate = false, AdminRole[]? roles = null)
    {
        NoStore(c);
        var raw = c.Request.Cookies[Cookie];
        byte[] token;
        try { token = raw is { Length: 43 } ? FromBase64Url(raw) : throw new FormatException(); }
        catch (FormatException) { throw new ApiFault(401, "ADMIN_SESSION_REQUIRED"); }
        var now = clock.GetUtcNow();
        var hash = SHA256.HashData(token);
        var session = await db.AdminSessions.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        var user = session is null ? null : await db.AdminUsers.SingleOrDefaultAsync(x => x.Id == session.AdminId, ct);
        if (session is null || user is null || session.RevokedAt is not null || session.ExpiresAt <= now || session.LastSeenAt + Idle <= now || user.Disabled)
        {
            Expire(c);
            throw new ApiFault(401, "ADMIN_SESSION_REQUIRED");
        }
        if (session.Network != ClientAddress.Keys(c.Connection.RemoteIpAddress).Network)
        {
            // A cookie used from another network ends the session (possible theft).
            session.RevokedAt = now;
            await Audit(db, c, user, "session-network-changed", user.Login, "", false, ct);
            Expire(c);
            throw new ApiFault(401, "ADMIN_SESSION_REQUIRED");
        }
        if (mutate)
        {
            var origin = c.Request.Headers.Origin.ToString();
            var sameOrigin = origin.Length == 0 || (System.Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.Authority == c.Request.Host.Value);
            var csrf = c.Request.Headers[CsrfHeader].ToString();
            if (!sameOrigin || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(csrf), Encoding.ASCII.GetBytes(Csrf(token))))
                throw new ApiFault(403, "ADMIN_CSRF");
        }
        if (roles is not null && !roles.Contains(user.Role)) throw new ApiFault(403, "ADMIN_FORBIDDEN");
        if (now - session.LastSeenAt > TimeSpan.FromSeconds(30)) { session.LastSeenAt = now; await db.SaveChangesAsync(ct); }
        return new Actor(user, session, token);
    }

    private static string Csrf(byte[] token) => Base64Url(HMACSHA256.HashData(token, "admin-csrf-v1"u8));

    // ---- accounts ----

    private static async Task<List<AccountRow>> Rows(MessengerDb db, IQueryable<Account> accounts, CancellationToken ct) =>
        await accounts.Select(x => new AccountRow(x.Id, x.CreatedAt,
            db.Devices.Count(d => d.AccountId == x.Id),
            db.Devices.Where(d => d.AccountId == x.Id && d.Active).Select(d => (int?)d.Generation).FirstOrDefault(),
            x.PinHash != null, x.SuspendedAt != null, x.SuspendReason, x.PhoneIndex == null ? "synthetic" : "sms")).ToListAsync(ct);

    private static async Task<IResult> AccountAction(Guid id, HttpContext c, ReasonRequest r, MessengerDb db, TimeProvider clock,
        ConnectionRegistry registry, string action, CancellationToken ct)
    {
        var a = await Authenticate(c, db, clock, ct, mutate: true, roles: [AdminRole.Admin]);
        var reason = (r.Reason ?? "").Trim();
        Transport.Require(reason.Length is >= 3 and <= 200 && !reason.Any(char.IsControl), "REASON_REQUIRED");
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var account = await db.Accounts.FromSqlInterpolated($"SELECT * FROM accounts WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        Transport.Require(account is not null, "NOT_FOUND", 404);
        if (action == "suspend") { Transport.Require(account!.SuspendedAt is null, "ALREADY_SUSPENDED", 409); account.SuspendedAt = now; account.SuspendReason = reason; }
        if (action == "resume") { Transport.Require(account!.SuspendedAt is not null, "NOT_SUSPENDED", 409); account.SuspendedAt = null; account.SuspendReason = null; }
        var revoked = 0;
        if (action != "resume")
        {
            var families = await db.RefreshFamilies.Where(x => x.AccountId == id && x.RevokedAt == null).ToListAsync(ct);
            foreach (var f in families) { f.RevokedAt = now; f.RevokedReason = "admin"; }
            revoked = families.Count;
        }
        await Audit(db, c, a.User, action + "-account", id.ToString(), reason + (action == "resume" ? "" : $"; sessions {revoked}"), true, ct);
        await tx.CommitAsync(ct);
        if (action != "resume")
            foreach (var device in await db.Devices.AsNoTracking().Where(x => x.AccountId == id).Select(x => x.Id).ToListAsync(ct))
                registry.Revoke(device);
        return Results.Ok(new { sessions = revoked });
    }

    // ---- panel users ----

    public static async Task<Issued> CreateUser(MessengerDb db, AdminCredentials creds, string? login, AdminRole role, DateTimeOffset now, CancellationToken ct)
    {
        login = (login ?? "").Trim().ToLowerInvariant();
        Transport.Require(login.Length is >= 3 and <= 64 && login.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '.' or '-' or '_'), "LOGIN_INVALID");
        Transport.Require(!await db.AdminUsers.AnyAsync(x => x.Login == login, ct), "LOGIN_TAKEN", 409);
        var (password, totp) = AdminCredentials.NewSecrets();
        var user = new AdminUser { Id = Guid.NewGuid(), Login = login, Role = role, PasswordHash = await creds.Hash(password, ct),
            TotpSecret = creds.ProtectTotp(totp), CreatedAt = now };
        db.AdminUsers.Add(user);
        await db.SaveChangesAsync(ct);
        return new Issued(user.Id, login, password, Totp.Uri(login, totp));
    }

    private static async Task<IResult> UserSwitch(Guid id, HttpContext c, MessengerDb db, TimeProvider clock, bool disable, CancellationToken ct)
    {
        var a = await Authenticate(c, db, clock, ct, mutate: true, roles: [AdminRole.Admin]);
        var user = await LockUser(db, id, ct);
        Transport.Require(user.Id != a.User.Id, "SELF_CHANGE_FORBIDDEN", 409);
        if (disable && user.Role == AdminRole.Admin) await RequireAnotherAdmin(db, user.Id, ct);
        user.Disabled = disable;
        if (!disable) { user.FailedLogins = 0; user.LockedUntil = null; }
        if (disable) await RevokeOtherSessions(db, user.Id, null, clock.GetUtcNow(), ct);
        await Audit(db, c, a.User, disable ? "disable-user" : "enable-user", user.Login, "", true, ct);
        return Results.NoContent();
    }

    private static async Task<AdminUser> LockUser(MessengerDb db, Guid id, CancellationToken ct)
    {
        var user = await db.AdminUsers.FromSqlInterpolated($"SELECT * FROM admin_users WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        return user ?? throw new ApiFault(404, "NOT_FOUND");
    }

    private static async Task RequireAnotherAdmin(MessengerDb db, Guid except, CancellationToken ct) =>
        Transport.Require(await db.AdminUsers.AnyAsync(x => x.Id != except && x.Role == AdminRole.Admin && !x.Disabled, ct), "LAST_ADMIN", 409);

    private static async Task RevokeOtherSessions(MessengerDb db, Guid adminId, byte[]? keep, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var s in await db.AdminSessions.Where(x => x.AdminId == adminId && x.RevokedAt == null).ToListAsync(ct))
            if (keep is null || !s.TokenHash.SequenceEqual(keep)) s.RevokedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public static AdminRole ParseRole(string? role) => role switch
    {
        "admin" => AdminRole.Admin, "support" => AdminRole.Support, "auditor" => AdminRole.Auditor,
        _ => throw new ApiFault(400, "ROLE_INVALID"),
    };
    private static string Name(AdminRole role) => role switch { AdminRole.Admin => "admin", AdminRole.Support => "support", _ => "auditor" };

    // ---- audit ----

    private static IQueryable<AuditRow> AuditQuery(MessengerDb db, long? before, string? action)
    {
        var q = db.AdminAudit.AsNoTracking();
        if (before is { } b) q = q.Where(x => x.Id < b);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(x => x.Action == action);
        return q.OrderByDescending(x => x.Id).Select(x => new AuditRow(x.Id, x.At, x.Login, x.Remote, x.Action, x.Target, x.Detail, x.Success));
    }

    private static async Task Audit(MessengerDb db, HttpContext c, AdminUser? user, string action, string target, string detail, bool success, CancellationToken ct)
    {
        db.AdminAudit.Add(new AdminAudit { At = c.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow(), AdminId = user?.Id,
            Login = Trim(user?.Login ?? "", 64), Remote = Trim(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", 64),
            Action = Trim(action, 48), Target = Trim(target, 128), Detail = Trim(detail, 400), Success = success });
        await db.SaveChangesAsync(ct);
    }
    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
    /// <summary>Spreadsheet formula injection: a cell starting with = + - @ is prefixed with an apostrophe.</summary>
    public static string CsvCell(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.Contains(';') || value.Contains('"') || value.Contains('\n') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    // ---- shared ----

    private static async Task Take(MessengerDb db, AuthSecrets secrets, string scope, string value, TimeSpan window, long limit, DateTimeOffset now, CancellationToken ct)
    {
        var seconds = (long)window.TotalSeconds;
        var start = now.ToUnixTimeSeconds() / seconds * seconds;
        var end = DateTimeOffset.FromUnixTimeSeconds(start + seconds);
        var key = secrets.CounterKey(scope, value) + ":" + seconds.ToString(CultureInfo.InvariantCulture) + ":" + start.ToString(CultureInfo.InvariantCulture);
        var taken = await db.Database.SqlQuery<long>($"""
            INSERT INTO auth_counters ("Key", "WindowEnd", "Count") VALUES ({key}, {end}, 1)
            ON CONFLICT ("Key") DO UPDATE SET "Count" = auth_counters."Count" + 1
            WHERE auth_counters."Count" + 1 <= {limit}
            RETURNING "Count" AS "Value"
            """).ToListAsync(ct) is { Count: 1 };
        if (!taken) throw new ApiFault(429, "RATE_LIMITED") { RetryAfter = end - now };
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static void NoStore(HttpContext c) => c.Response.Headers.CacheControl = "no-store";
    private static void Expire(HttpContext c) =>
        c.Response.Headers.Append("Set-Cookie", $"{Cookie}=; Path=/; Secure; HttpOnly; SameSite=Strict; Max-Age=0");
    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4));

    private static IResult Static(HttpContext c, string body, string type)
    {
        var h = c.Response.Headers;
        NoStore(c);
        h.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'unsafe-inline'; img-src 'self' data:; " +
            "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h.XContentTypeOptions = "nosniff";
        return Results.Text(body, type);
    }
}

/// <summary>Server console commands: `dotnet Messenger.Api.dll admin create --login NAME --role admin|support|auditor`.</summary>
public static class AdminCli
{
    public static async Task<int> Run(WebApplication app, string[] args)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MessengerDb>();
        var creds = scope.ServiceProvider.GetRequiredService<AdminCredentials>();
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        switch (args.ElementAtOrDefault(1))
        {
            case "create":
                var issued = await AdminApi.CreateUser(db, creds, Arg("--login"), AdminApi.ParseRole(Arg("--role") ?? "admin"),
                    TimeProvider.System.GetUtcNow(), CancellationToken.None);
                db.AdminAudit.Add(new AdminAudit { At = TimeProvider.System.GetUtcNow(), Login = "console", Remote = "console",
                    Action = "create-user", Target = issued.Login, Detail = Arg("--role") ?? "admin", Success = true });
                await db.SaveChangesAsync();
                // Shown once on the server console; never logged.
                Console.WriteLine($"ADMIN_CREATED login={issued.Login}");
                Console.WriteLine($"password: {issued.Password}");
                Console.WriteLine($"totp: {issued.OtpAuthUri}");
                Console.WriteLine("Add the TOTP URI to an authenticator app, store the password offline, then clear this terminal.");
                return 0;
            case "list":
                foreach (var u in await db.AdminUsers.AsNoTracking().OrderBy(x => x.Login).ToListAsync())
                    Console.WriteLine($"{u.Login}\t{u.Role}\t{(u.Disabled ? "disabled" : "enabled")}");
                return 0;
            default:
                Console.Error.WriteLine("Usage: admin create --login NAME --role admin|support|auditor | admin list");
                return 2;
        }
    }
}
