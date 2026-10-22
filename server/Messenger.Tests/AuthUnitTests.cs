using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Messenger.Api;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Messenger.Tests;

public sealed class AuthUnitTests(ITestOutputHelper output)
{
    private static IConfiguration Config(params (string Key, string Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.ToDictionary(x => x.Key, x => (string?)x.Value)).Build();
    private static readonly IConfiguration Secrets = Config(
        ("Auth:PhoneIndexKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
        ("Auth:SmsCodeKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
        ("Auth:PinPepper", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

    private static string Code(Action action) => Assert.Throws<ApiFault>(action).Code;

    [Fact, Trait("Category", "Unit")]
    public void PlusSevenIsSplitByNumberPlanNotPrefix()
    {
        var policy = new PhonePolicy(Config(("Auth:Sms:AllowedRegions", "RU,BY,KZ,UZ")));
        Assert.Equal(new NormalizedPhone("+79001234567", "RU"), policy.Normalize("+7 (900) 123-45-67"));
        Assert.Equal("KZ", policy.Normalize("+7 701 123 4567").Region);
        Assert.Equal("BY", policy.Normalize("+375 29 123 45 67").Region);
        Assert.Equal("UZ", policy.Normalize("+998 90 123 45 67").Region);
        var ruOnly = new PhonePolicy(Config(("Auth:Sms:AllowedRegions", "RU")));
        Assert.Equal("PHONE_REGION_NOT_ALLOWED", Code(() => ruOnly.Normalize("+77011234567")));
        Assert.Equal("PHONE_REGION_NOT_ALLOWED", Code(() => policy.Normalize("+380501234567")));
        Assert.Equal("PHONE_INVALID", Code(() => policy.Normalize("89001234567")));
        Assert.Equal("PHONE_INVALID", Code(() => policy.Normalize("+7900123456")));
        Assert.Equal("PHONE_INVALID", Code(() => policy.Normalize("+7 900 ABC 45 67 89")));
        Assert.Equal("PHONE_NOT_MOBILE", Code(() => policy.Normalize("+74951234567")));
        Assert.Throws<InvalidOperationException>(() => new PhonePolicy(Config(("Auth:Sms:AllowedRegions", ""))));
        Assert.Throws<InvalidOperationException>(() => new PhonePolicy(Config(("Auth:Sms:AllowedRegions", "RU,XX"))));
    }

    [Fact, Trait("Category", "Unit")]
    public void CodeVerifierNeedsServerSecretAndIsBoundToChallenge()
    {
        var secrets = new AuthSecrets(Secrets);
        var other = new AuthSecrets(Config(
            ("Auth:PhoneIndexKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("Auth:SmsCodeKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("Auth:PinPepper", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))));
        var id = Guid.NewGuid();
        var phone = secrets.PhoneIndex("+79001234567");
        var expires = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var verifier = secrets.CodeVerifier(id, phone, expires, "123456");
        Assert.Equal(verifier, secrets.CodeVerifier(id, phone, expires, "123456"));
        Assert.NotEqual(verifier, other.CodeVerifier(id, phone, expires, "123456"));
        Assert.NotEqual(verifier, secrets.CodeVerifier(Guid.NewGuid(), phone, expires, "123456"));
        Assert.NotEqual(verifier, secrets.CodeVerifier(id, secrets.PhoneIndex("+79001234568"), expires, "123456"));
        Assert.NotEqual(verifier, secrets.CodeVerifier(id, phone, expires.AddSeconds(1), "123456"));
        Assert.NotEqual(verifier, SHA256.HashData(Encoding.ASCII.GetBytes("123456")));
        Assert.DoesNotContain("79001234567", secrets.CounterKey("sms-phone", "+79001234567"));
        Assert.Throws<InvalidOperationException>(() => new AuthSecrets(Config(("Auth:PhoneIndexKey", "c2hvcnQ="))));
    }

    private static JsonElement Fixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "docs", "fixtures", "device-proof-v1.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    [Fact, Trait("Category", "Unit")]
    public void DeviceProofMatchesPublishedAndroidFixture()
    {
        var f = Fixture();
        var input = f.GetProperty("input");
        var authKey = Convert.FromBase64String(input.GetProperty("authKeySpkiBase64").GetString()!);
        var payload = DeviceProof.Payload(ChallengeOperation.Activate, input.GetProperty("challengeId").GetGuid(),
            Convert.FromBase64String(input.GetProperty("nonceBase64").GetString()!), input.GetProperty("expiresAt").GetDateTimeOffset(),
            input.GetProperty("phone").GetString(), null, null, input.GetProperty("deviceId").GetGuid(),
            input.GetProperty("registrationId").GetInt32(), Convert.FromBase64String(input.GetProperty("identityKeyBase64").GetString()!), authKey);
        Assert.Equal(f.GetProperty("payload").GetString(), Encoding.ASCII.GetString(payload));
        Assert.Equal(f.GetProperty("payloadSha256Hex").GetString(), Convert.ToHexStringLower(SHA256.HashData(payload)));
        // Signature produced by OpenSSL, not by this server: an independent implementation of the same contract.
        var signature = Convert.FromBase64String(f.GetProperty("signatureDerBase64").GetString()!);
        Assert.True(DeviceProof.IsValidKey(authKey));
        Assert.True(DeviceProof.Verify(authKey, payload, signature));
        var altered = (byte[])payload.Clone();
        altered[^2] ^= 1;
        Assert.False(DeviceProof.Verify(authKey, altered, signature));
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(f.GetProperty("testOnlyPrivateKeyPkcs8Base64").GetString()!), out _);
        Assert.True(DeviceProof.Verify(authKey, payload, key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
        // Raw r||s (IEEE P1363) is not the contract; Android Signature produces DER.
        Assert.False(DeviceProof.Verify(authKey, payload, key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }

    [Fact, Trait("Category", "Unit")]
    public void DeviceProofRejectsNonP256AndMalformedKeys()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);
        Assert.False(DeviceProof.IsValidKey(p384.ExportSubjectPublicKeyInfo()));
        Assert.False(DeviceProof.IsValidKey(rsa.ExportSubjectPublicKeyInfo()));
        Assert.False(DeviceProof.IsValidKey([5, .. new byte[32]])); // A Signal identity key is not a proof key.
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(DeviceProof.IsValidKey([.. p256.ExportSubjectPublicKeyInfo(), 0]));
        Assert.False(DeviceProof.Verify(p256.ExportSubjectPublicKeyInfo(), [1], null));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task PinUsesArgon2idWithPepper()
    {
        var secrets = new AuthSecrets(Secrets);
        var hasher = new PinHasher(secrets, Config());
        var stopwatch = Stopwatch.StartNew();
        var hash = await hasher.Hash("482913", default);
        var hashTime = stopwatch.Elapsed;
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", hash);
        Assert.NotEqual(hash, await hasher.Hash("482913", default)); // Random salt.
        stopwatch.Restart();
        Assert.True(await hasher.Verify(hash, "482913", default));
        output.WriteLine($"Argon2id m=64MiB t=3: hash {hashTime.TotalMilliseconds:F0} ms, verify {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        Assert.False(await hasher.Verify(hash, "482914", default));
        var otherPepper = new PinHasher(new AuthSecrets(Config(
            ("Auth:PhoneIndexKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("Auth:SmsCodeKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("Auth:PinPepper", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))))), Config());
        Assert.False(await otherPepper.Verify(hash, "482913", default)); // A database copy without the pepper is not enough.
        Assert.False(PinHasher.IsWellFormed("12345"));
        Assert.False(PinHasher.IsWellFormed("12345a"));
        Assert.False(PinHasher.IsWellFormed("１２３４５６")); // Full-width digits are not ASCII.
        Assert.True(AuthService.IsTrivialPin("000000"));
        Assert.True(AuthService.IsTrivialPin("456789"));
        Assert.True(AuthService.IsTrivialPin("543210"));
        Assert.False(AuthService.IsTrivialPin("482913"));
    }

    [Fact, Trait("Category", "Unit")]
    public void ClientAddressGroupsIpv6SubscriberPrefix()
    {
        Assert.Equal(("203.0.113.7", "203.0.113.0/24"), ClientAddress.Keys(IPAddress.Parse("203.0.113.7")));
        Assert.Equal(("203.0.113.7", "203.0.113.0/24"), ClientAddress.Keys(IPAddress.Parse("::ffff:203.0.113.7")));
        var one = ClientAddress.Keys(IPAddress.Parse("2001:db8:1:2:aaaa::1"));
        Assert.Equal(one, ClientAddress.Keys(IPAddress.Parse("2001:db8:1:2:bbbb::9")));
        Assert.NotEqual(one.Host, ClientAddress.Keys(IPAddress.Parse("2001:db8:1:3::1")).Host);
        Assert.Equal(one.Network, ClientAddress.Keys(IPAddress.Parse("2001:db8:1:3::1")).Network);
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("""{"status":"OK","status_code":100,"sms":{"79001234567":{"status":"OK","status_code":100,"sms_id":"000000-000001"}},"balance":1}""", SmsResult.Accepted)]
    [InlineData("""{"status":"OK","status_code":100,"sms":{"79001234567":{"status":"ERROR","status_code":207,"status_text":"x"}}}""", SmsResult.Rejected)]
    [InlineData("""{"status":"ERROR","status_code":200,"status_text":"x"}""", SmsResult.Rejected)]
    [InlineData("""{"status":"OK","status_code":100}""", SmsResult.Unknown)]
    [InlineData("""{"status":"OK","sms":{"a":{"status":"OK"},"b":{"status":"OK"}}}""", SmsResult.Unknown)]
    [InlineData("""[]""", SmsResult.Unknown)]
    public void SmsRuAnswersMapConservatively(string json, SmsResult expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, SmsRuSender.Interpret(document.RootElement));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task SmsRuKeepsKeyOutOfUrlAndTreatsFailuresAsUnknown()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"status":"OK","sms":{"79001234567":{"status":"OK","sms_id":"1"}}}""") });
        var config = Config(("Auth:Sms:SmsRu:ApiKey", "SYNTHETIC-KEY-0000"));
        var sender = new SmsRuSender(new HttpClient(stub) { BaseAddress = new Uri("https://sms.ru/") }, config);
        Assert.Equal(SmsResult.Accepted, await sender.Send("+79001234567", "knitERP: код 000000.", default));
        Assert.Equal(HttpMethod.Post, stub.Method);
        Assert.Equal("https://sms.ru/sms/send", stub.Uri!.ToString());
        Assert.DoesNotContain("SYNTHETIC", stub.Uri.ToString());
        Assert.Contains("api_id=SYNTHETIC-KEY-0000", stub.Body);
        Assert.Contains("to=79001234567", stub.Body);
        Assert.Contains("json=1", stub.Body);
        Assert.DoesNotContain("test=", stub.Body);

        foreach (var failure in new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") },
            _ => throw new HttpRequestException("reset"),
            _ => throw new TaskCanceledException("timeout"),
        })
        {
            var broken = new SmsRuSender(new HttpClient(new StubHandler(failure)) { BaseAddress = new Uri("https://sms.ru/") }, config);
            Assert.Equal(SmsResult.Unknown, await broken.Send("+79001234567", "x", default));
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpMethod? Method;
        public Uri? Uri;
        public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }
}
