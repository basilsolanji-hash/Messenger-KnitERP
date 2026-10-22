using System.Collections.Concurrent;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Messenger.Api;

public sealed class Access(IConfiguration config, TimeProvider clock)
{
    public const string Issuer = "kniterp-messenger-dev";
    public const string Audience = "kniterp-messenger-android";
    public static byte[] Key(IConfiguration c)
    {
        byte[] key;
        try { key = Convert.FromBase64String(c["Auth:SigningKey"] ?? ""); }
        catch (FormatException) { throw new InvalidOperationException("Auth signing key must be base64."); }
        if (key.Length < 32) throw new InvalidOperationException("Auth signing key requires at least 32 random bytes.");
        return key;
    }
    public SessionResponse Issue(Account a, Device d, Guid? session = null)
    {
        var now = clock.GetUtcNow();
        // JWT NumericDate has whole-second precision. Return exactly the signed expiry.
        var expires = DateTimeOffset.FromUnixTimeSeconds(now.AddMinutes(15).ToUnixTimeSeconds());
        List<Claim> claims = [new Claim("sub", a.Id.ToString()), new Claim("device", d.Id.ToString()),
             new Claim("generation", d.Generation.ToString(CultureInfo.InvariantCulture)),
             new Claim("jti", Guid.NewGuid().ToString())];
        // sid names the refresh family. Logout and device replacement revoke it before the JWT expires.
        if (session is { } sid) claims.Add(new Claim("sid", sid.ToString()));
        var jwt = new JwtSecurityToken(Issuer, Audience, claims,
            now.UtcDateTime, expires.UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(Key(config)), SecurityAlgorithms.HmacSha256));
        return new(a.Id, d.Id, d.Generation, new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
    public static Actor Current(ClaimsPrincipal p)
    {
        if (!Guid.TryParse(p.FindFirstValue("sub"), out var account) ||
            !Guid.TryParse(p.FindFirstValue("device"), out var device) ||
            !int.TryParse(p.FindFirstValue("generation"), out var generation))
            throw new ApiFault(401, "INVALID_SESSION");
        return new(account, device, generation);
    }
    public static Guid? SessionId(ClaimsPrincipal p) => p.FindFirstValue("sid") is { } value
        ? Guid.TryParse(value, out var id) ? id : throw new ApiFault(401, "INVALID_SESSION") : null;
    // Synthetic dev sessions carry no sid. Public sessions also require their refresh family to be live.
    public static async Task<bool> IsSessionActive(MessengerDb db, ClaimsPrincipal p, DateTimeOffset now, CancellationToken ct = default)
    {
        var actor = Current(p);
        if (!await IsActive(db, actor, ct)) return false;
        return SessionId(p) is not { } sid || await db.RefreshFamilies.AsNoTracking().AnyAsync(f => f.Id == sid &&
            f.DeviceId == actor.DeviceId && f.RevokedAt == null && f.AbsoluteExpiresAt > now, ct);
    }
    public static Task<bool> IsActive(MessengerDb db, Actor a, CancellationToken ct = default) =>
        db.Devices.AsNoTracking().AnyAsync(d => d.Id == a.DeviceId && d.AccountId == a.AccountId &&
            d.Generation == a.Generation && d.Active && db.Accounts.Any(x => x.Id == a.AccountId && x.Generation == a.Generation), ct);

    public static void ProvisioningKey(HttpContext ctx, IConfiguration config)
    {
        var expected = config["Dev:ProvisioningKey"];
        if (expected is null || expected.Length < 40) throw new InvalidOperationException("Missing random development provisioning key.");
        var actual = ctx.Request.Headers["X-Dev-Provisioning-Token"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(actual)),
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)))) throw new ApiFault(401, "UNAUTHORIZED");
    }
}

public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, (Guid DeviceId, Guid? Session, Action Abort)> connections = new();
    public void Add(string id, Guid deviceId, Guid? session, Action abort) => connections[id] = (deviceId, session, abort);
    public void Remove(string id) => connections.TryRemove(id, out _);
    public (Guid[] Devices, Guid[] Sessions) Snapshot() =>
        (connections.Values.Select(x => x.DeviceId).Distinct().ToArray(),
         connections.Values.Where(x => x.Session.HasValue).Select(x => x.Session!.Value).Distinct().ToArray());
    public void Revoke(Guid deviceId)
    {
        foreach (var entry in connections.Where(x => x.Value.DeviceId == deviceId))
            if (connections.TryRemove(entry.Key, out var connection)) connection.Abort();
    }
    public void RevokeSession(Guid session)
    {
        foreach (var entry in connections.Where(x => x.Value.Session == session))
            if (connections.TryRemove(entry.Key, out var connection)) connection.Abort();
    }
}
