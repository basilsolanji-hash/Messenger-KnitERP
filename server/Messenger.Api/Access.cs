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
    public SessionResponse Issue(Account a, Device d)
    {
        var now = clock.GetUtcNow();
        // JWT NumericDate has whole-second precision. Return exactly the signed expiry.
        var expires = DateTimeOffset.FromUnixTimeSeconds(now.AddMinutes(15).ToUnixTimeSeconds());
        var jwt = new JwtSecurityToken(Issuer, Audience,
            [new Claim("sub", a.Id.ToString()), new Claim("device", d.Id.ToString()),
             new Claim("generation", d.Generation.ToString(CultureInfo.InvariantCulture)),
             new Claim("jti", Guid.NewGuid().ToString())],
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
    private readonly ConcurrentDictionary<string, (Guid DeviceId, Action Abort)> connections = new();
    public void Add(string id, Guid deviceId, Action abort) => connections[id] = (deviceId, abort);
    public void Remove(string id) => connections.TryRemove(id, out _);
    public void Revoke(Guid deviceId)
    {
        foreach (var entry in connections.Where(x => x.Value.DeviceId == deviceId))
            if (connections.TryRemove(entry.Key, out var connection)) connection.Abort();
    }
}
