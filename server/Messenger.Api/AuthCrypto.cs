using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PhoneNumbers;

namespace Messenger.Api;

// Server secrets used by auth. All are base64 random values from the environment, never stored in PostgreSQL.
public sealed class AuthSecrets
{
    public byte[] PhoneIndexKey { get; }
    public byte[] SmsCodeKey { get; }
    public byte[] PinPepper { get; }
    public AuthSecrets(IConfiguration c)
    {
        PhoneIndexKey = Read(c, "Auth:PhoneIndexKey");
        SmsCodeKey = Read(c, "Auth:SmsCodeKey");
        PinPepper = Read(c, "Auth:PinPepper");
        if (PhoneIndexKey.SequenceEqual(SmsCodeKey) || PhoneIndexKey.SequenceEqual(PinPepper) || SmsCodeKey.SequenceEqual(PinPepper))
            throw new InvalidOperationException("Auth secrets must be independent random values.");
    }
    private static byte[] Read(IConfiguration c, string name)
    {
        byte[] key;
        try { key = Convert.FromBase64String(c[name] ?? ""); }
        catch (FormatException) { throw new InvalidOperationException(name + " must be base64."); }
        if (key.Length < 32) throw new InvalidOperationException(name + " requires at least 32 random bytes.");
        return key;
    }
    public byte[] PhoneIndex(string e164) => Mac(PhoneIndexKey, "phone-index-v1", e164);
    // Rate-limit keys never contain a raw number or IP address.
    public string CounterKey(string scope, string value) => scope + ":" + Convert.ToHexStringLower(Mac(PhoneIndexKey, "counter-v1:" + scope, value)[..20]);
    public byte[] CodeVerifier(Guid challengeId, byte[] phoneIndex, DateTimeOffset expiresAt, string code) =>
        Mac(SmsCodeKey, "sms-code-v1", challengeId.ToString("D") + "\n" + Convert.ToHexStringLower(phoneIndex) + "\n" +
            expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + "\n" + code);
    public static byte[] Mac(byte[] key, string label, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(label + "\0" + value));
}

public sealed record NormalizedPhone(string E164, string Region);

public sealed class PhonePolicy
{
    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();
    public IReadOnlySet<string> Regions { get; }
    public PhonePolicy(IConfiguration c)
    {
        Regions = (c["Auth:Sms:AllowedRegions"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToUpperInvariant()).ToHashSet();
        if (Regions.Count == 0 || Regions.Any(r => !Util.GetSupportedRegions().Contains(r)))
            throw new InvalidOperationException("Auth:Sms:AllowedRegions must list supported ISO regions explicitly.");
    }
    // Only full international input is accepted: "+7..." alone does not tell Russia from Kazakhstan, the
    // number plan does. libphonenumber resolves the region from the complete number.
    public NormalizedPhone Normalize(string? input)
    {
        Transport.Require(input is { Length: > 0 and <= 32 } && input.TrimStart().StartsWith('+'), "PHONE_INVALID");
        PhoneNumber number;
        try { number = Util.Parse(input, null); }
        catch (NumberParseException) { throw new ApiFault(400, "PHONE_INVALID"); }
        Transport.Require(Util.IsValidNumber(number), "PHONE_INVALID");
        var region = Util.GetRegionCodeForNumber(number);
        Transport.Require(region is not null && Regions.Contains(region), "PHONE_REGION_NOT_ALLOWED");
        Transport.Require(Util.GetNumberType(number) is PhoneNumberType.MOBILE or PhoneNumberType.FIXED_LINE_OR_MOBILE, "PHONE_NOT_MOBILE");
        return new(Util.Format(number, PhoneNumberFormat.E164), region!);
    }
}

// Proof of possession: ECDSA P-256 / SHA-256 over a canonical ASCII payload, made with a non-exportable
// Android Keystore key. Signal identity keys are X25519/XEdDSA and are deliberately not reused here.
public static class DeviceProof
{
    public const string Version = "kniterp-device-proof-v1";
    private const string P256 = "1.2.840.10045.3.1.7";

    public static bool IsValidKey(byte[]? spki)
    {
        if (spki is not { Length: > 0 and <= 256 }) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            return read == spki.Length && key.ExportParameters(false).Curve.Oid.Value == P256;
        }
        catch (CryptographicException) { return false; }
    }

    public static bool Verify(byte[] spki, byte[] payload, byte[]? signature)
    {
        if (signature is not { Length: > 0 and <= 80 } || !IsValidKey(spki)) return false;
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out _);
        return key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    // One field per line, fixed order, "-" when a field does not apply, trailing newline. See docs/ANDROID_AUTH.md.
    public static byte[] Payload(ChallengeOperation operation, Guid challengeId, byte[] nonce, DateTimeOffset expiresAt,
        string? phone, Guid? accountId, Guid? refreshTokenId, Guid deviceId, int registrationId, byte[] identityKey, byte[] authKey)
    {
        var lines = new[]
        {
            Version,
            "op:" + (operation == ChallengeOperation.Activate ? "activate" : "refresh"),
            "challenge:" + challengeId.ToString("D"),
            "nonce:" + Base64Url(nonce),
            "expires:" + expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "phone:" + (phone ?? "-"),
            "account:" + (accountId?.ToString("D") ?? "-"),
            "token:" + (refreshTokenId?.ToString("D") ?? "-"),
            "device:" + deviceId.ToString("D"),
            "registration:" + registrationId.ToString(CultureInfo.InvariantCulture),
            "identity:" + Base64Url(identityKey),
            "authkey:" + Base64Url(SHA256.HashData(authKey)),
        };
        return Encoding.ASCII.GetBytes(string.Join('\n', lines) + "\n");
    }

    public static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

// Argon2id from libsodium (crypto_pwhash_str). The PIN is first keyed with a server pepper, so a database copy
// alone is not enough for an offline search over the 10^6 PIN space.
public sealed class PinHasher(AuthSecrets secrets, IConfiguration config)
{
    private readonly long ops = config.GetValue("Auth:Pin:Argon2Ops", 3L);
    private readonly int memory = config.GetValue("Auth:Pin:Argon2MemoryKiB", 65536) * 1024;
    // Bounds Argon2 memory per process: 4 * 64 MiB.
    private readonly SemaphoreSlim slots = new(config.GetValue("Auth:Pin:MaxParallel", 4));

    public static bool IsWellFormed(string? pin) => pin is { Length: 6 } && pin.All(char.IsAsciiDigit);

    private string Input(string pin) => Convert.ToBase64String(AuthSecrets.Mac(secrets.PinPepper, "pin-v1", pin));

    public async Task<string> Hash(string pin, CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        try { return Sodium.PasswordHash.ArgonHashString(Input(pin), ops, memory); }
        finally { slots.Release(); }
    }

    public async Task<bool> Verify(string hash, string pin, CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        try { return Sodium.PasswordHash.ArgonHashStringVerify(hash, Input(pin)); }
        finally { slots.Release(); }
    }
}

public static class ClientAddress
{
    // Exact IPv4 address or the IPv6 /64 a single subscriber usually controls, and a wider network for subnet limits.
    public static (string Host, string Network) Keys(IPAddress? address)
    {
        if (address is null) return ("unknown", "unknown");
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4) return (address.ToString(), new IPAddress([bytes[0], bytes[1], bytes[2], 0]) + "/24");
        var host = bytes[..8].Concat(new byte[8]).ToArray();
        var network = bytes[..6].Concat(new byte[10]).ToArray();
        return (new IPAddress(host) + "/64", new IPAddress(network) + "/48");
    }
}
