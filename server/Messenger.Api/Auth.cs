using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Api;

public sealed class AuthService(MessengerDb db, TimeProvider clock, AuthSecrets secrets, PhonePolicy phones,
    ISmsSender sms, PinHasher pins, Access access, ConnectionRegistry registry, IConfiguration config)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ResendDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RefreshChallengeLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan RefreshIdle = TimeSpan.FromDays(30);
    public static readonly TimeSpan RefreshAbsolute = TimeSpan.FromDays(90);
    public static readonly TimeSpan LostResponseWindow = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan FreshAuthentication = TimeSpan.FromMinutes(10);
    public const int MaxCodeAttempts = 5;
    public const int PinAttemptsBeforeLock = 5;
    // Any constant works; it only has to be unique among this database's advisory locks.
    private const long SmsReservationLock = 0x6b6e_6974_534d_5301;
    private static readonly ChallengeState[] OpenSms = [ChallengeState.Sending, ChallengeState.Sent, ChallengeState.DeliveryUnknown];

    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow(); // Same microsecond precision as PostgreSQL, so retries return identical values.
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    public async Task<ChallengeResponse> CreateSmsChallenge(SmsChallengeRequest r, IPAddress? ip, CancellationToken ct)
    {
        Transport.Require(r.RequestId != Guid.Empty && r.DeviceId != Guid.Empty && r.RegistrationId is > 0 and <= 0x3fff);
        Transport.Require(r.IdentityKey is { Length: 33 } && r.IdentityKey[0] == 5, "INVALID_IDENTITY_KEY");
        Transport.Require(DeviceProof.IsValidKey(r.AuthKey), "INVALID_AUTH_KEY");
        var phone = phones.Normalize(r.Phone);
        var phoneIndex = secrets.PhoneIndex(phone.E164);
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(r with { Phone = phone.E164 }, Transport.Json));
        if (await Replay(r.RequestId, hash, ct) is { } earlier) return earlier;

        var now = Now();
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var challenge = new AuthChallenge { Id = Guid.NewGuid(), RequestId = r.RequestId, RequestHash = hash,
            Operation = ChallengeOperation.Activate, State = ChallengeState.Sending, PhoneIndex = phoneIndex, Region = phone.Region,
            DeviceId = r.DeviceId, RegistrationId = r.RegistrationId, IdentityKey = r.IdentityKey, AuthKey = r.AuthKey,
            Nonce = RandomNumberGenerator.GetBytes(32), CreatedAt = now, ExpiresAt = now + CodeLifetime };
        challenge.CodeVerifier = secrets.CodeVerifier(challenge.Id, phoneIndex, challenge.ExpiresAt, code);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // One short critical section shared by all API instances. It makes the idempotency check, cooldown,
            // in-flight cap and counters a single atomic reservation. SMS volume is far below its throughput.
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({SmsReservationLock})", ct);
            if (await Replay(r.RequestId, hash, ct) is { } raced) return raced;
            Transport.Require(!await db.Devices.AnyAsync(x => x.Id == r.DeviceId, ct), "DEVICE_ALREADY_EXISTS", 409);
            var last = await db.AuthChallenges.Where(x => x.PhoneIndex == phoneIndex && x.Operation == ChallengeOperation.Activate)
                .MaxAsync(x => (DateTimeOffset?)x.CreatedAt, ct);
            if (last > now - ResendDelay) throw new ApiFault(429, "RESEND_TOO_EARLY") { RetryAfter = last.Value + ResendDelay - now };
            var inFlight = await db.AuthChallenges.CountAsync(x => x.State == ChallengeState.Sending && x.CreatedAt > now.AddSeconds(-30), ct);
            if (inFlight >= config.GetValue("Auth:Sms:MaxInFlight", 10)) throw new ApiFault(503, "SMS_BUSY") { RetryAfter = TimeSpan.FromSeconds(5) };
            var (host, network) = ClientAddress.Keys(ip);
            var phoneKey = Convert.ToHexStringLower(phoneIndex);
            await Take("sms-ip", host, TimeSpan.FromMinutes(10), Limit("IpPer10Minutes", 5), ct);
            await Take("sms-ip-day", host, TimeSpan.FromDays(1), Limit("IpPerDay", 30), ct);
            await Take("sms-net", network, TimeSpan.FromMinutes(10), Limit("NetworkPer10Minutes", 20), ct);
            await Take("sms-phone", phoneKey, TimeSpan.FromMinutes(15), Limit("PhonePer15Minutes", 3), ct);
            await Take("sms-phone-day", phoneKey, TimeSpan.FromDays(1), Limit("PhonePerDay", 10), ct);
            await Take("sms-device", r.DeviceId.ToString(), TimeSpan.FromDays(1), Limit("DevicePerDay", 5), ct);
            await Take("sms-global", "all", TimeSpan.FromHours(1), Limit("GlobalPerHour", 300), ct);
            await Take("sms-budget", "all", TimeSpan.FromDays(1), Limit("DailyBudgetKopecks", 0), ct, Cost(phone.Region));
            await db.AuthChallenges.Where(x => x.PhoneIndex == phoneIndex && OpenSms.Contains(x.State))
                .ExecuteUpdateAsync(x => x.SetProperty(c => c.State, ChallengeState.Superseded), ct);
            db.AuthChallenges.Add(challenge);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        // The reservation is committed. From here a disconnecting client must not cancel the send, and a crash
        // leaves the challenge in Sending: verifiable until expiry, never sent again automatically.
        SmsResult result;
        try { result = await sms.Send(phone.E164, $"knitERP: код {code}. Никому не сообщайте его.", CancellationToken.None); }
        catch (Exception e) when (e is not OutOfMemoryException) { result = SmsResult.Unknown; }
        var state = result switch { SmsResult.Accepted => ChallengeState.Sent, SmsResult.Rejected => ChallengeState.ProviderRejected, _ => ChallengeState.DeliveryUnknown };
        await db.AuthChallenges.Where(x => x.Id == challenge.Id && x.State == ChallengeState.Sending)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.State, state), CancellationToken.None);
        Transport.Require(result != SmsResult.Rejected, "SMS_NOT_SENT", 502);
        return Response(challenge);
    }

    private async Task<ChallengeResponse?> Replay(Guid requestId, byte[] hash, CancellationToken ct)
    {
        var prior = await db.AuthChallenges.AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId, ct);
        if (prior is null) return null;
        Transport.Require(prior.RequestHash is not null && CryptographicOperations.FixedTimeEquals(prior.RequestHash, hash), "IDEMPOTENCY_CONFLICT", 409);
        Transport.Require(prior.State != ChallengeState.ProviderRejected, "SMS_NOT_SENT", 502);
        return Response(prior); // A retry never triggers another paid SMS.
    }

    private static ChallengeResponse Response(AuthChallenge c) => c.Operation == ChallengeOperation.Activate
        ? new(c.Id, c.Nonce, c.ExpiresAt, c.CreatedAt + ResendDelay, 6, MaxCodeAttempts)
        : new(c.Id, c.Nonce, c.ExpiresAt, null, 0, 1);

    private long Limit(string name, long fallback) => config.GetValue("Auth:Sms:Limits:" + name, fallback);
    private long Cost(string region) => config.GetValue<long?>("Auth:Sms:CostKopecks:" + region)
        ?? throw new InvalidOperationException("Missing SMS cost for an allowed region.");

    // Fixed-window counter in PostgreSQL. The conditional upsert is atomic across instances; on rejection the caller's
    // transaction rolls back, so a refused request does not consume the other limits.
    private async Task Take(string scope, string value, TimeSpan window, long limit, CancellationToken ct, long amount = 1)
    {
        var now = Now();
        var seconds = (long)window.TotalSeconds;
        var start = now.ToUnixTimeSeconds() / seconds * seconds;
        var end = DateTimeOffset.FromUnixTimeSeconds(start + seconds);
        var key = secrets.CounterKey(scope, value) + ":" + seconds.ToString(CultureInfo.InvariantCulture) + ":" + start.ToString(CultureInfo.InvariantCulture);
        var taken = amount <= limit && await db.Database.SqlQuery<long>($"""
            INSERT INTO auth_counters ("Key", "WindowEnd", "Count") VALUES ({key}, {end}, {amount})
            ON CONFLICT ("Key") DO UPDATE SET "Count" = auth_counters."Count" + {amount}
            WHERE auth_counters."Count" + {amount} <= {limit}
            RETURNING "Count" AS "Value"
            """).ToListAsync(ct) is { Count: 1 };
        if (!taken) throw new ApiFault(429, scope.StartsWith("sms-global", StringComparison.Ordinal) || scope == "sms-budget" ? "SMS_UNAVAILABLE" : "RATE_LIMITED") { RetryAfter = end - now };
    }

    private async Task<AuthChallenge> LockChallenge(Guid id, ChallengeOperation operation, CancellationToken ct)
    {
        var c = await db.AuthChallenges.FromSqlInterpolated($"SELECT * FROM auth_challenges WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        Transport.Require(c is not null && c.Operation == operation, "CHALLENGE_NOT_FOUND", 404);
        return c!;
    }

    private void RequireState(AuthChallenge c, params ChallengeState[] allowed)
    {
        if (allowed.Contains(c.State))
        {
            Transport.Require(c.ExpiresAt > Now(), "CHALLENGE_EXPIRED", 410);
            return;
        }
        throw c.State switch
        {
            ChallengeState.Superseded => new ApiFault(409, "CHALLENGE_SUPERSEDED"),
            ChallengeState.Failed => new ApiFault(403, "CHALLENGE_LOCKED"),
            ChallengeState.ProviderRejected => new ApiFault(409, "CHALLENGE_NOT_SENT"),
            ChallengeState.CodeVerified when allowed.Contains(ChallengeState.Sent) => new ApiFault(409, "CHALLENGE_USED"),
            ChallengeState.Completed => new ApiFault(409, "CHALLENGE_USED"),
            _ => new ApiFault(409, "CODE_REQUIRED"),
        };
    }

    private async Task Fail(AuthChallenge c, ApiFault fault, CancellationToken ct)
    {
        c.Attempts++;
        if (c.Attempts >= MaxCodeAttempts) { c.State = ChallengeState.Failed; c.CodeVerifier = null; }
        await db.SaveChangesAsync(ct);
        await db.Database.CommitTransactionAsync(ct);
        throw new ApiFault(fault.Status, fault.Code) { AttemptsRemaining = Math.Max(0, MaxCodeAttempts - c.Attempts) };
    }

    private static byte[] ActivationPayload(AuthChallenge c, string e164) => DeviceProof.Payload(ChallengeOperation.Activate,
        c.Id, c.Nonce, c.ExpiresAt, e164, null, null, c.DeviceId, c.RegistrationId, c.IdentityKey, c.AuthKey);

    public async Task<ActivationResponse> VerifySms(SmsVerifyRequest r, CancellationToken ct)
    {
        Transport.Require(r.ChallengeId != Guid.Empty && r.Code is { Length: 6 } && r.Code.All(char.IsAsciiDigit));
        var phone = phones.Normalize(r.Phone);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await LockChallenge(r.ChallengeId, ChallengeOperation.Activate, ct);
        RequireState(c, OpenSms);
        // Every mismatch counts against the same five attempts: wrong number, wrong device proof or wrong code.
        if (!CryptographicOperations.FixedTimeEquals(c.PhoneIndex, secrets.PhoneIndex(phone.E164)))
            await Fail(c, new(400, "CHALLENGE_MISMATCH"), ct);
        if (!DeviceProof.Verify(c.AuthKey, ActivationPayload(c, phone.E164), r.Signature))
            await Fail(c, new(403, "DEVICE_PROOF_INVALID"), ct);
        if (!CryptographicOperations.FixedTimeEquals(c.CodeVerifier, secrets.CodeVerifier(c.Id, c.PhoneIndex!, c.ExpiresAt, r.Code)))
            await Fail(c, new(400, "CODE_INVALID"), ct);
        c.State = ChallengeState.CodeVerified;
        c.CodeVerifier = null; // The code is single-use from this point even if the PIN step follows.
        var account = await LockAccount(c.PhoneIndex!, ct);
        if (account?.PinHash is not null)
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new("PIN_REQUIRED", null, false);
        }
        return await Activate(c, account, ct);
    }

    public async Task<ActivationResponse> VerifyPin(PinVerifyRequest r, CancellationToken ct)
    {
        Transport.Require(r.ChallengeId != Guid.Empty && PinHasher.IsWellFormed(r.Pin));
        var phone = phones.Normalize(r.Phone);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await LockChallenge(r.ChallengeId, ChallengeOperation.Activate, ct);
        RequireState(c, ChallengeState.CodeVerified);
        if (!CryptographicOperations.FixedTimeEquals(c.PhoneIndex, secrets.PhoneIndex(phone.E164)))
            await Fail(c, new(400, "CHALLENGE_MISMATCH"), ct);
        if (!DeviceProof.Verify(c.AuthKey, ActivationPayload(c, phone.E164), r.Signature))
            await Fail(c, new(403, "DEVICE_PROOF_INVALID"), ct);
        var account = await LockAccount(c.PhoneIndex!, ct);
        if (account?.PinHash is not null && await CheckPin(account, r.Pin, ct) is { } fault)
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw fault;
        }
        return await Activate(c, account, ct);
    }

    private Task<Account?> LockAccount(byte[] phoneIndex, CancellationToken ct) =>
        db.Accounts.FromSqlInterpolated($"SELECT * FROM accounts WHERE \"PhoneIndex\" = {phoneIndex} FOR UPDATE").SingleOrDefaultAsync(ct);

    // Records the result on the locked account row. SMS never resets these counters.
    private async Task<ApiFault?> CheckPin(Account account, string pin, CancellationToken ct)
    {
        var now = Now();
        if (account.PinLockedUntil > now) return new ApiFault(429, "PIN_LOCKED") { RetryAfter = account.PinLockedUntil - now };
        if (await pins.Verify(account.PinHash!, pin, ct))
        {
            account.PinFailures = 0;
            account.PinLockedUntil = null;
            return null;
        }
        account.PinFailures++;
        if (account.PinFailures >= PinAttemptsBeforeLock)
        {
            var minutes = Math.Min(24 * 60, 15 * Math.Pow(2, Math.Min(account.PinFailures - PinAttemptsBeforeLock, 10)));
            account.PinLockedUntil = now.AddMinutes(minutes);
        }
        return new ApiFault(403, "PIN_INVALID") { AttemptsRemaining = Math.Max(0, PinAttemptsBeforeLock - account.PinFailures),
            RetryAfter = account.PinLockedUntil > now ? account.PinLockedUntil - now : null };
    }

    // All factors were checked by the caller inside this transaction, under the challenge and account row locks.
    private async Task<ActivationResponse> Activate(AuthChallenge c, Account? account, CancellationToken ct)
    {
        var now = Now();
        Transport.Require(!await db.Devices.AnyAsync(x => x.Id == c.DeviceId, ct), "DEVICE_ALREADY_EXISTS", 409);
        var device = new Device { Id = c.DeviceId, RegistrationId = c.RegistrationId, IdentityKey = c.IdentityKey, AuthKey = c.AuthKey };
        Guid? replaced = null;
        var created = account is null;
        if (account is null)
        {
            account = new Account { Id = Guid.NewGuid(), PhoneIndex = c.PhoneIndex, Generation = 1 };
            device.AccountId = account.Id;
            device.Generation = 1;
            db.Accounts.Add(account);
            db.Devices.Add(device);
        }
        else
        {
            device.AccountId = account.Id;
            replaced = await ReplaceDevice(db, account, device, now, ct);
        }
        c.State = ChallengeState.Completed;
        c.CompletedAt = now;
        var family = new RefreshFamily { Id = Guid.NewGuid(), AccountId = account.Id, DeviceId = device.Id,
            CreatedAt = now, AuthenticatedAt = now, AbsoluteExpiresAt = now + RefreshAbsolute };
        db.RefreshFamilies.Add(family);
        var session = NewSession(account, device, family, created, now);
        try
        {
            await db.SaveChangesAsync(ct);
            await db.Database.CommitTransactionAsync(ct);
        }
        catch (DbUpdateException) { throw new ApiFault(409, "CONCURRENT_ACTIVATION"); }
        if (replaced is { } old) registry.Revoke(old); // Other instances close theirs in QueueWorker's sweep.
        return new("ACTIVATED", session, false);
    }

    // Shared by the public transfer and the synthetic dev replacement. Caller holds the account row lock.
    public static async Task<Guid?> ReplaceDevice(MessengerDb db, Account account, Device device, DateTimeOffset now, CancellationToken ct)
    {
        var old = await db.Devices.FromSqlInterpolated($"SELECT * FROM devices WHERE \"AccountId\" = {account.Id} AND \"Active\" = TRUE ORDER BY \"Id\" FOR UPDATE").SingleOrDefaultAsync(ct);
        if (old is not null) old.Active = false;
        account.Generation++;
        device.Generation = account.Generation;
        await db.SaveChangesAsync(ct); // Satisfy the one-active-device unique index before insert.
        db.Devices.Add(device);
        if (old is not null)
        {
            await db.Envelopes.Where(x => x.RecipientDeviceId == old.Id).ExecuteUpdateAsync(x => x.SetProperty(e => e.Ciphertext, (byte[]?)null), ct);
            await db.Notifications.Where(x => x.DeviceId == old.Id).ExecuteDeleteAsync(ct);
        }
        await db.RefreshFamilies.Where(x => x.AccountId == account.Id && x.RevokedAt == null).ExecuteUpdateAsync(x => x
            .SetProperty(f => f.RevokedAt, now).SetProperty(f => f.RevokedReason, "device-replaced"), ct);
        await db.SaveChangesAsync(ct);
        return old?.Id;
    }

    private AuthSession NewSession(Account account, Device device, RefreshFamily family, bool created, DateTimeOffset now)
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var token = new RefreshToken { Id = Guid.NewGuid(), FamilyId = family.Id, Verifier = SHA256.HashData(secret),
            CreatedAt = now, ExpiresAt = now + RefreshIdle < family.AbsoluteExpiresAt ? now + RefreshIdle : family.AbsoluteExpiresAt };
        db.RefreshTokens.Add(token);
        var issued = access.Issue(account, device, family.Id);
        return new(account.Id, device.Id, device.Generation, issued.AccessToken, issued.ExpiresAt,
            "rt1." + token.Id.ToString("D") + "." + DeviceProof.Base64Url(secret), token.ExpiresAt, created);
    }

    public async Task<ChallengeResponse> CreateRefreshChallenge(RefreshChallengeRequest r, IPAddress? ip, CancellationToken ct)
    {
        Transport.Require(r.DeviceId != Guid.Empty);
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == r.DeviceId, ct);
        Transport.Require(device is { Active: true, AuthKey: not null }, "SESSION_REVOKED", 401);
        var now = Now();
        var challenge = new AuthChallenge { Id = Guid.NewGuid(), Operation = ChallengeOperation.Refresh, State = ChallengeState.Sent,
            DeviceId = device!.Id, RegistrationId = device.RegistrationId, IdentityKey = device.IdentityKey, AuthKey = device.AuthKey!,
            Nonce = RandomNumberGenerator.GetBytes(32), CreatedAt = now, ExpiresAt = now + RefreshChallengeLifetime };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Take("refresh-ip", ClientAddress.Keys(ip).Host, TimeSpan.FromMinutes(10), Limit("RefreshIpPer10Minutes", 120), ct);
        await Take("refresh-device", device.Id.ToString(), TimeSpan.FromMinutes(10), Limit("RefreshDevicePer10Minutes", 20), ct);
        db.AuthChallenges.Add(challenge);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Response(challenge);
    }

    public async Task<AuthSession> Refresh(RefreshRequest r, CancellationToken ct)
    {
        var (tokenId, secret) = ParseRefresh(r.RefreshToken);
        Transport.Require(r.ChallengeId != Guid.Empty);
        var now = Now();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var token = await db.RefreshTokens.FromSqlInterpolated($"SELECT * FROM refresh_tokens WHERE \"Id\" = {tokenId} FOR UPDATE").SingleOrDefaultAsync(ct);
        Transport.Require(token is not null && CryptographicOperations.FixedTimeEquals(token.Verifier, SHA256.HashData(secret)), "REFRESH_INVALID", 401);
        var family = await db.RefreshFamilies.FromSqlInterpolated($"SELECT * FROM refresh_families WHERE \"Id\" = {token!.FamilyId} FOR UPDATE").SingleAsync(ct);
        Transport.Require(family.RevokedAt is null, "SESSION_REVOKED", 401);
        Transport.Require(family.AbsoluteExpiresAt > now, "SESSION_EXPIRED", 401);
        var device = await db.Devices.AsNoTracking().SingleAsync(x => x.Id == family.DeviceId, ct);
        var account = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == family.AccountId, ct);
        if (!device.Active || device.Generation != account.Generation || device.AuthKey is null)
        {
            await Revoke(family, "device-inactive", now, ct);
            throw new ApiFault(401, "SESSION_REVOKED");
        }
        var c = await LockChallenge(r.ChallengeId, ChallengeOperation.Refresh, ct);
        Transport.Require(c.DeviceId == device.Id, "CHALLENGE_MISMATCH");
        var payload = DeviceProof.Payload(ChallengeOperation.Refresh, c.Id, c.Nonce, c.ExpiresAt, null, account.Id, token.Id,
            device.Id, device.RegistrationId, device.IdentityKey, device.AuthKey);
        if (!DeviceProof.Verify(device.AuthKey, payload, r.Signature))
        {
            if (c.State == ChallengeState.Sent) { c.State = ChallengeState.Failed; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
            throw new ApiFault(403, "DEVICE_PROOF_INVALID");
        }
        if (token.ConsumedAt is not null)
        {
            // Lost response: the identical signed request comes back shortly after it was processed and its
            // successor was never used. The unused successor is retired and a new one issued. Anything else is reuse.
            var successor = token.ReplacedBy is { } next ? await db.RefreshTokens.FromSqlInterpolated(
                $"SELECT * FROM refresh_tokens WHERE \"Id\" = {next} FOR UPDATE").SingleOrDefaultAsync(ct) : null;
            if (token.ConsumedByChallenge == c.Id && token.ConsumedAt > now - LostResponseWindow && successor is { ConsumedAt: null })
            {
                successor.ConsumedAt = now;
                return await Rotate(token, family, account, device, now, ct);
            }
            await Revoke(family, "refresh-reuse", now, ct);
            throw new ApiFault(401, "REFRESH_REUSED");
        }
        Transport.Require(token.ExpiresAt > now, "SESSION_EXPIRED", 401);
        RequireState(c, ChallengeState.Sent);
        c.State = ChallengeState.Completed;
        c.CompletedAt = now;
        token.ConsumedAt = now;
        token.ConsumedByChallenge = c.Id;
        return await Rotate(token, family, account, device, now, ct);
    }

    private async Task<AuthSession> Rotate(RefreshToken token, RefreshFamily family, Account account, Device device, DateTimeOffset now, CancellationToken ct)
    {
        var session = NewSession(account, device, family, false, now);
        token.ReplacedBy = db.ChangeTracker.Entries<RefreshToken>().Single(x => x.State == EntityState.Added).Entity.Id;
        await db.SaveChangesAsync(ct);
        await db.Database.CommitTransactionAsync(ct);
        return session;
    }

    private async Task Revoke(RefreshFamily family, string reason, DateTimeOffset now, CancellationToken ct)
    {
        family.RevokedAt = now;
        family.RevokedReason = reason;
        await db.SaveChangesAsync(ct);
        await db.Database.CommitTransactionAsync(ct);
    }

    private static (Guid Id, byte[] Secret) ParseRefresh(string? value)
    {
        var parts = value?.Split('.') ?? [];
        if (parts is ["rt1", var id, { Length: 43 } secret] && Guid.TryParseExact(id, "D", out var tokenId))
        {
            try
            {
                var bytes = Convert.FromBase64String(secret.Replace('-', '+').Replace('_', '/') + "=");
                if (bytes.Length == 32) return (tokenId, bytes);
            }
            catch (FormatException) { }
        }
        throw new ApiFault(401, "REFRESH_INVALID");
    }

    public async Task Logout(ClaimsPrincipal user, CancellationToken ct)
    {
        var actor = Access.Current(user);
        var session = Access.SessionId(user) ?? throw new ApiFault(403, "SESSION_NOT_ELIGIBLE");
        var now = Now();
        await db.RefreshFamilies.Where(x => x.Id == session && x.DeviceId == actor.DeviceId && x.RevokedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(f => f.RevokedAt, now).SetProperty(f => f.RevokedReason, "logout"), ct);
        registry.RevokeSession(session);
    }

    public async Task SetPin(ClaimsPrincipal user, SetPinRequest r, CancellationToken ct)
    {
        Transport.Require(PinHasher.IsWellFormed(r.Pin), "PIN_FORMAT");
        Transport.Require(!IsTrivialPin(r.Pin), "PIN_TOO_SIMPLE");
        Transport.Require(r.CurrentPin is null || PinHasher.IsWellFormed(r.CurrentPin));
        var (account, family, tx) = await LockOwnAccount(user, ct);
        await using (tx)
        {
            if (account.PinHash is not null)
            {
                Transport.Require(r.CurrentPin is not null, "PIN_REQUIRED", 403);
                if (await CheckPin(account, r.CurrentPin!, ct) is { } fault) { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); throw fault; }
            }
            else
            {
                // A stolen unlocked phone must not be able to lock the owner out of their number later.
                Transport.Require(family.AuthenticatedAt > Now() - FreshAuthentication, "REAUTH_REQUIRED", 403);
            }
            account.PinHash = await pins.Hash(r.Pin, ct);
            account.PinFailures = 0;
            account.PinLockedUntil = null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
    }

    public async Task DisablePin(ClaimsPrincipal user, DisablePinRequest r, CancellationToken ct)
    {
        Transport.Require(PinHasher.IsWellFormed(r.CurrentPin));
        var (account, _, tx) = await LockOwnAccount(user, ct);
        await using (tx)
        {
            Transport.Require(account.PinHash is not null, "PIN_NOT_SET", 409);
            if (await CheckPin(account, r.CurrentPin, ct) is { } fault) { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); throw fault; }
            account.PinHash = null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
    }

    private async Task<(Account, RefreshFamily, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction)> LockOwnAccount(ClaimsPrincipal user, CancellationToken ct)
    {
        var actor = Access.Current(user);
        var session = Access.SessionId(user) ?? throw new ApiFault(403, "SESSION_NOT_ELIGIBLE");
        var tx = await db.Database.BeginTransactionAsync(ct);
        var account = await db.Accounts.FromSqlInterpolated($"SELECT * FROM accounts WHERE \"Id\" = {actor.AccountId} FOR UPDATE").SingleAsync(ct);
        var family = await db.RefreshFamilies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == session && x.RevokedAt == null, ct);
        if (family is null || account.Generation != actor.Generation) { await tx.DisposeAsync(); throw new ApiFault(401, "SESSION_REVOKED"); }
        return (account, family, tx);
    }

    public static bool IsTrivialPin(string pin) =>
        pin.Distinct().Count() == 1 || "0123456789012345".Contains(pin, StringComparison.Ordinal) || "9876543210987654".Contains(pin, StringComparison.Ordinal);

    // Retention from docs/AUTH.md. Counters vanish after their window; challenges a day after expiry.
    public static async Task Cleanup(MessengerDb db, DateTimeOffset now, CancellationToken ct)
    {
        await db.AuthChallenges.Where(x => x.CodeVerifier != null && x.ExpiresAt <= now)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.CodeVerifier, (byte[]?)null), ct);
        await db.AuthChallenges.Where(x => x.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
        await db.AuthCounters.Where(x => x.WindowEnd < now).ExecuteDeleteAsync(ct);
        await db.RefreshTokens.Where(x => x.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
        await db.RefreshFamilies.Where(x => x.AbsoluteExpiresAt < now.AddDays(-1) || x.RevokedAt < now.AddDays(-30)).ExecuteDeleteAsync(ct);
    }
}
