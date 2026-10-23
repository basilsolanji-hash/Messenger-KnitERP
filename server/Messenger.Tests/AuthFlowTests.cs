using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Messenger.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Messenger.Tests.Api;

namespace Messenger.Tests;

// Real API instances over HTTP on one PostgreSQL database. Requires TEST_DATABASE, like the transport tests.
[Collection("PostgreSQL")]
public sealed class AuthFlowTests : IAsyncLifetime
{
    private AuthDatabase database = null!;
    private readonly MutableClock clock = new();
    private AuthHost first = null!, second = null!;
    private HttpClient a = null!, b = null!;
    private static readonly Dictionary<string, string> Roomy = new()
    {
        ["Auth:Sms:Limits:GlobalPerHour"] = "100000", ["Auth:Sms:Limits:DailyBudgetKopecks"] = "100000000",
    };

    public async Task InitializeAsync()
    {
        database = await AuthDatabase.Create();
        first = new AuthHost(database, clock, Roomy);
        second = new AuthHost(database, clock, Roomy);
        a = first.CreateClient();
        b = second.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await first.DisposeAsync();
        await second.DisposeAsync();
        await database.DisposeAsync();
    }

    private static string RuPhone() => "+7912" + RandomNumberGenerator.GetInt32(0, 10_000_000).ToString("D7");
    private static string Peer() => $"198.18.{RandomNumberGenerator.GetInt32(0, 256)}.{RandomNumberGenerator.GetInt32(1, 255)}";
    private IEnumerable<(string Phone, string Text)> AllSms => first.Sms.Sent.Concat(second.Sms.Sent);
    private string CodeFor(string phone) => Regex(AllSms.Last(x => x.Phone == phone).Text);
    private static string Regex(string text) => System.Text.RegularExpressions.Regex.Match(text, @"\d{6}").Value;

    private async Task<ChallengeResponse> Start(TestDevice device, string phone, HttpClient? client = null, string? peer = null) =>
        await Ok<ChallengeResponse>(await Post(client ?? a, "/api/v1/auth/sms/challenges", Challenge(device, phone), peer ?? Peer()));

    private async Task<AuthSession> Register(TestDevice device, string phone)
    {
        var c = await Start(device, phone);
        var result = await Ok<ActivationResponse>(await Post(a, "/api/v1/auth/sms/verify", Verify(device, c, phone, CodeFor(phone))));
        Assert.Equal("ACTIVATED", result.Status);
        return result.Session!;
    }

    // Lets the next SMS for this number pass the 60 s resend gate without moving the JWT clock.
    private async Task AgeChallenges(string phone)
    {
        await using var db = database.Open();
        await db.Database.ExecuteSqlRawAsync("UPDATE auth_challenges SET \"CreatedAt\" = \"CreatedAt\" - interval '61 seconds'");
    }

    private async Task<HttpStatusCode> Me(string access)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new("Bearer", access);
        return (await a.SendAsync(request)).StatusCode;
    }

    private async Task<HttpResponseMessage> Refresh(TestDevice device, AuthSession session, string refreshToken, HttpClient? client = null)
    {
        var issued = await Post(client ?? a, "/api/v1/auth/refresh/challenges", new RefreshChallengeRequest(device.Id), Peer());
        if (!issued.IsSuccessStatusCode) return issued; // A revoked device is refused before any challenge exists.
        var challenge = await Ok<ChallengeResponse>(issued);
        return await Post(client ?? a, "/api/v1/auth/refresh", RefreshBody(device, session, refreshToken, challenge));
    }

    private static RefreshRequest RefreshBody(TestDevice device, AuthSession session, string refreshToken, ChallengeResponse challenge) =>
        new(refreshToken, challenge.ChallengeId, device.Sign(DeviceProof.Payload(ChallengeOperation.Refresh, challenge.ChallengeId,
            challenge.Nonce, challenge.ExpiresAt, null, session.AccountId, TokenId(refreshToken), device.Id, device.RegistrationId,
            device.IdentityKey, device.AuthKey)));

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task CodeIsSingleUseAndWrongCodesConsumeAttempts()
    {
        using var device = new TestDevice();
        var phone = RuPhone();
        var c = await Start(device, phone);
        Assert.Equal(c.ExpiresAt, c.ResendAfter!.Value.AddMinutes(4)); // 5 min TTL, resend after 60 s.
        var code = CodeFor(phone);
        var wrong = code == "000000" ? "000001" : "000000";
        var fault = await Fails(await Post(a, "/api/v1/auth/sms/verify", Verify(device, c, phone, wrong)), "CODE_INVALID");
        Assert.Equal(4, fault.AttemptsRemaining);
        var ok = await Ok<ActivationResponse>(await Post(b, "/api/v1/auth/sms/verify", Verify(device, c, phone, code)));
        Assert.Equal("ACTIVATED", ok.Status);
        Assert.True(ok.Session!.NewAccount);
        Assert.Equal(HttpStatusCode.OK, await Me(ok.Session.AccessToken));
        await Fails(await Post(a, "/api/v1/auth/sms/verify", Verify(device, c, phone, code)), "CHALLENGE_USED");
        await using var db = database.Open();
        var stored = await db.AuthChallenges.SingleAsync(x => x.Id == c.ChallengeId);
        Assert.Null(stored.CodeVerifier); // Nothing verifiable remains after success.
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task FifthWrongCodeLocksChallengeEvenForCorrectCode()
    {
        using var device = new TestDevice();
        var phone = RuPhone();
        var c = await Start(device, phone);
        var code = CodeFor(phone);
        var wrong = code == "111111" ? "222222" : "111111";
        for (var i = 4; i >= 0; i--)
            Assert.Equal(i, (await Fails(await Post(i % 2 == 0 ? a : b, "/api/v1/auth/sms/verify", Verify(device, c, phone, wrong)), "CODE_INVALID")).AttemptsRemaining);
        await Fails(await Post(a, "/api/v1/auth/sms/verify", Verify(device, c, phone, code)), "CHALLENGE_LOCKED");
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task CodeExpiresAfterFiveMinutes()
    {
        using var device = new TestDevice();
        var phone = RuPhone();
        var c = await Start(device, phone);
        clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));
        try { await Fails(await Post(a, "/api/v1/auth/sms/verify", Verify(device, c, phone, CodeFor(phone))), "CHALLENGE_EXPIRED"); }
        finally { clock.Reset(); }
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task ResendWaitsSixtySecondsAndInvalidatesPreviousCode()
    {
        using var device = new TestDevice();
        var phone = RuPhone();
        var old = await Start(device, phone);
        var oldCode = CodeFor(phone);
        var early = await Fails(await Post(b, "/api/v1/auth/sms/challenges", Challenge(device, phone), Peer()), "RESEND_TOO_EARLY");
        Assert.Equal(429, early.Status);
        Assert.InRange(early.RetryAfterSeconds!.Value, 1, 60);
        await AgeChallenges(phone);
        var fresh = await Start(device, phone, b);
        var newCode = CodeFor(phone);
        await Fails(await Post(a, "/api/v1/auth/sms/verify", Verify(device, old, phone, oldCode)), "CHALLENGE_SUPERSEDED");
        if (oldCode != newCode)
            await Fails(await Post(a, "/api/v1/auth/sms/verify", Verify(device, fresh, phone, oldCode)), "CODE_INVALID");
        Assert.Equal("ACTIVATED", (await Ok<ActivationResponse>(await Post(a, "/api/v1/auth/sms/verify", Verify(device, fresh, phone, newCode)))).Status);
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task IdempotentRetryNeverSendsSecondSmsAndChangedBodyConflicts()
    {
        using var device = new TestDevice();
        var phone = RuPhone();
        var request = Challenge(device, phone);
        var peer = Peer();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
            await Ok<ChallengeResponse>(await Post(i % 2 == 0 ? a : b, "/api/v1/auth/sms/challenges", request, peer))));
        Assert.Single(results.Select(x => x.ChallengeId).Distinct());
        Assert.All(results, x => Assert.Equal(results[0].Nonce, x.Nonce));
        Assert.Single(AllSms, x => x.Phone == phone);
        await Fails(await Post(a, "/api/v1/auth/sms/challenges", request with { RegistrationId = request.RegistrationId % 16000 + 1 }, peer), "IDEMPOTENCY_CONFLICT");
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task UnknownProviderResultIsNotResentAndCodeStillWorks()
    {
        using var device = new TestDevice();
        var phone = RuPhone();
        first.Sms.Result = SmsResult.Unknown;
        try
        {
            var request = Challenge(device, phone);
            var c = await Ok<ChallengeResponse>(await Post(a, "/api/v1/auth/sms/challenges", request, Peer()));
            var again = await Ok<ChallengeResponse>(await Post(b, "/api/v1/auth/sms/challenges", request, Peer()));
            Assert.Equal(c with { Nonce = [] }, again with { Nonce = [] });
            Assert.Equal(c.Nonce, again.Nonce);
            Assert.Single(AllSms, x => x.Phone == phone);
            await using (var db = database.Open())
                Assert.Equal(ChallengeState.DeliveryUnknown, (await db.AuthChallenges.SingleAsync(x => x.Id == c.ChallengeId)).State);
            Assert.Equal("ACTIVATED", (await Ok<ActivationResponse>(await Post(a, "/api/v1/auth/sms/verify", Verify(device, c, phone, CodeFor(phone))))).Status);
        }
        finally { first.Sms.Result = SmsResult.Accepted; }
        first.Sms.Result = SmsResult.Rejected;
        try
        {
            using var other = new TestDevice();
            var request = Challenge(other, RuPhone());
            await Fails(await Post(a, "/api/v1/auth/sms/challenges", request, Peer()), "SMS_NOT_SENT");
            await Fails(await Post(b, "/api/v1/auth/sms/challenges", request, Peer()), "SMS_NOT_SENT");
        }
        finally { first.Sms.Result = SmsResult.Accepted; }
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task ParallelRequestsForOneNumberOnTwoInstancesSendOneSms()
    {
        var phone = RuPhone();
        first.Sms.Delay = second.Sms.Delay = TimeSpan.FromMilliseconds(200);
        try
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
            {
                using var device = new TestDevice();
                return await Post(i % 2 == 0 ? a : b, "/api/v1/auth/sms/challenges", Challenge(device, phone), Peer());
            }));
            Assert.Single(responses, x => x.IsSuccessStatusCode);
            foreach (var r in responses.Where(x => !x.IsSuccessStatusCode)) await Fails(r, "RESEND_TOO_EARLY");
            Assert.Single(AllSms, x => x.Phone == phone);
        }
        finally { first.Sms.Delay = second.Sms.Delay = TimeSpan.Zero; }
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task IpLimitIsSharedBetweenInstances()
    {
        var peer = Peer();
        var responses = await Task.WhenAll(Enumerable.Range(0, 9).Select(async i =>
        {
            using var device = new TestDevice();
            return await Post(i % 2 == 0 ? a : b, "/api/v1/auth/sms/challenges", Challenge(device, RuPhone()), peer);
        }));
        Assert.Equal(5, responses.Count(x => x.IsSuccessStatusCode));
        foreach (var r in responses.Where(x => !x.IsSuccessStatusCode)) Assert.Equal(429, (await Fails(r, "RATE_LIMITED")).Status);
        Assert.Equal(5, AllSms.Count());
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task ParallelCorrectVerifyOnTwoInstancesActivatesOnce()
    {
        using var device = new TestDevice();
        var phone = RuPhone();
        var c = await Start(device, phone);
        var body = Verify(device, c, phone, CodeFor(phone));
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Post(i % 2 == 0 ? a : b, "/api/v1/auth/sms/verify", body)));
        Assert.Single(responses, x => x.IsSuccessStatusCode);
        foreach (var r in responses.Where(x => !x.IsSuccessStatusCode)) await Fails(r, "CHALLENGE_USED");
        await using var db = database.Open();
        Assert.Equal(1, await db.Devices.CountAsync(x => x.Id == device.Id));
        Assert.Equal(1, await db.RefreshFamilies.CountAsync(x => x.DeviceId == device.Id));
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task ProofIsBoundToDeviceIdentityOperationAndNumber()
    {
        using var device = new TestDevice();
        using var attacker = new TestDevice();
        var phone = RuPhone();
        var c = await Start(device, phone);
        var code = CodeFor(phone);
        byte[] Bind(Guid deviceId, int registration, byte[] identity, byte[] authKey, ChallengeOperation op = ChallengeOperation.Activate) =>
            DeviceProof.Payload(op, c.ChallengeId, c.Nonce, c.ExpiresAt, phone, null, null, deviceId, registration, identity, authKey);
        // Each variant is signed with the attacker key over the substituted fields, or with the real key over altered fields.
        var attempts = new[]
        {
            attacker.Sign(Bind(attacker.Id, attacker.RegistrationId, attacker.IdentityKey, attacker.AuthKey)),
            device.Sign(Bind(attacker.Id, device.RegistrationId, device.IdentityKey, device.AuthKey)),
            device.Sign(Bind(device.Id, device.RegistrationId, attacker.IdentityKey, device.AuthKey)),
            device.Sign(Bind(device.Id, device.RegistrationId, device.IdentityKey, device.AuthKey, ChallengeOperation.Refresh)),
        };
        foreach (var signature in attempts)
            await Fails(await Post(a, "/api/v1/auth/sms/verify", new SmsVerifyRequest(c.ChallengeId, phone, code, signature)), "DEVICE_PROOF_INVALID");
        await Fails(await Post(a, "/api/v1/auth/sms/verify", Verify(device, c, RuPhone(), code)), "CHALLENGE_MISMATCH");
        // Five failures exhausted the challenge: the correct proof and code no longer work.
        await Fails(await Post(a, "/api/v1/auth/sms/verify", Verify(device, c, phone, code)), "CHALLENGE_LOCKED");
        await using var db = database.Open();
        Assert.False(await db.Devices.AnyAsync(x => x.Id == device.Id || x.Id == attacker.Id));
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task ChallengesCannotCrossOperations()
    {
        using var device = new TestDevice();
        var phone = RuPhone();
        var session = await Register(device, phone);
        var refreshChallenge = await Ok<ChallengeResponse>(await Post(a, "/api/v1/auth/refresh/challenges", new RefreshChallengeRequest(device.Id), Peer()));
        await Fails(await Post(a, "/api/v1/auth/sms/verify", new SmsVerifyRequest(refreshChallenge.ChallengeId, phone, "123456",
            device.Sign([1]))), "CHALLENGE_NOT_FOUND");
        await AgeChallenges(phone);
        using var next = new TestDevice();
        var sms = await Start(next, phone);
        await Fails(await Post(a, "/api/v1/auth/refresh", new RefreshRequest(session.RefreshToken, sms.ChallengeId, device.Sign([1]))), "CHALLENGE_NOT_FOUND");
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task PinBlocksTransferUntilCorrectAndHasNoBypass()
    {
        using var phone1 = new TestDevice();
        using var phone2 = new TestDevice();
        var phone = RuPhone();
        var session = await Register(phone1, phone);
        await Fails(await Put(session.AccessToken, new SetPinRequest("123456", null)), "PIN_TOO_SIMPLE");
        await Fails(await Put(session.AccessToken, new SetPinRequest("111111", null)), "PIN_TOO_SIMPLE");
        Assert.Equal(HttpStatusCode.NoContent, (await Put(session.AccessToken, new SetPinRequest("482913", null))).StatusCode);

        await AgeChallenges(phone);
        var c = await Start(phone2, phone);
        var step = await Ok<ActivationResponse>(await Post(b, "/api/v1/auth/sms/verify", Verify(phone2, c, phone, CodeFor(phone))));
        Assert.Equal("PIN_REQUIRED", step.Status);
        Assert.Null(step.Session);
        Assert.False(step.RecoveryAvailable);
        await Fails(await Post(a, "/api/v1/auth/recovery", new { }), "RECOVERY_NOT_AVAILABLE");
        // SMS alone changed nothing.
        Assert.Equal(HttpStatusCode.OK, await Me(session.AccessToken));
        var proof = ActivationProof(phone2, c, phone);
        var wrong = await Fails(await Post(a, "/api/v1/auth/pin/verify", new PinVerifyRequest(c.ChallengeId, phone, "000001", proof)), "PIN_INVALID");
        Assert.Equal(4, wrong.AttemptsRemaining);
        Assert.Equal(HttpStatusCode.OK, await Me(session.AccessToken));
        await using (var db = database.Open())
        {
            var account = await db.Accounts.SingleAsync(x => x.Id == session.AccountId);
            Assert.Equal(1, account.Generation);
            Assert.True((await db.Devices.SingleAsync(x => x.Id == phone1.Id)).Active);
            Assert.False(await db.Devices.AnyAsync(x => x.Id == phone2.Id));
            Assert.StartsWith("$argon2id$", account.PinHash);
        }
        // The SMS code cannot be replayed to restart the PIN step.
        await Fails(await Post(a, "/api/v1/auth/sms/verify", Verify(phone2, c, phone, CodeFor(phone))), "CHALLENGE_USED");
        var done = await Ok<ActivationResponse>(await Post(b, "/api/v1/auth/pin/verify", new PinVerifyRequest(c.ChallengeId, phone, "482913", proof)));
        Assert.Equal("ACTIVATED", done.Status);
        Assert.False(done.Session!.NewAccount);
        Assert.Equal(2, done.Session.Generation);
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(session.AccessToken));
        await Fails(await Refresh(phone1, session, session.RefreshToken), "SESSION_REVOKED");
    }

    private Task<HttpResponseMessage> Put(string bearer, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/account/pin") { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Authorization = new("Bearer", bearer);
        return a.SendAsync(request);
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task PinLockoutSurvivesNewSmsAndBlocksCorrectPin()
    {
        using var owner = new TestDevice();
        var phone = RuPhone();
        var session = await Register(owner, phone);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(session.AccessToken, new SetPinRequest("482913", null))).StatusCode);
        using var thief = new TestDevice();
        await AgeChallenges(phone);
        var c = await Start(thief, phone);
        Assert.Equal("PIN_REQUIRED", (await Ok<ActivationResponse>(await Post(a, "/api/v1/auth/sms/verify", Verify(thief, c, phone, CodeFor(phone))))).Status);
        var proof = ActivationProof(thief, c, phone);
        for (var i = 0; i < 5; i++)
            await Fails(await Post(i % 2 == 0 ? a : b, "/api/v1/auth/pin/verify", new PinVerifyRequest(c.ChallengeId, phone, $"00000{i}", proof)), "PIN_INVALID");
        var locked = await Fails(await Post(a, "/api/v1/auth/pin/verify", new PinVerifyRequest(c.ChallengeId, phone, "482913", proof)), "PIN_LOCKED");
        Assert.InRange(locked.RetryAfterSeconds!.Value, 14 * 60, 15 * 60);
        // A fresh SMS does not reset the account-level PIN counter.
        await AgeChallenges(phone);
        var again = await Start(thief, phone);
        Assert.Equal("PIN_REQUIRED", (await Ok<ActivationResponse>(await Post(a, "/api/v1/auth/sms/verify", Verify(thief, again, phone, CodeFor(phone))))).Status);
        await Fails(await Post(b, "/api/v1/auth/pin/verify", new PinVerifyRequest(again.ChallengeId, phone, "482913", ActivationProof(thief, again, phone))), "PIN_LOCKED");
        Assert.Equal(HttpStatusCode.OK, await Me(session.AccessToken));
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task PinChangesRequireCurrentPinOrFreshAuthentication()
    {
        using var device = new TestDevice();
        var session = await Register(device, RuPhone());
        await using (var db = database.Open())
            await db.RefreshFamilies.Where(x => x.DeviceId == device.Id).ExecuteUpdateAsync(x => x.SetProperty(f => f.AuthenticatedAt, f => f.AuthenticatedAt.AddMinutes(-11)));
        await Fails(await Put(session.AccessToken, new SetPinRequest("482913", null)), "REAUTH_REQUIRED");
        await using (var db = database.Open())
            await db.RefreshFamilies.Where(x => x.DeviceId == device.Id).ExecuteUpdateAsync(x => x.SetProperty(f => f.AuthenticatedAt, f => f.AuthenticatedAt.AddMinutes(11)));
        Assert.Equal(HttpStatusCode.NoContent, (await Put(session.AccessToken, new SetPinRequest("482913", null))).StatusCode);
        await Fails(await Put(session.AccessToken, new SetPinRequest("592614", null)), "PIN_REQUIRED");
        await Fails(await Put(session.AccessToken, new SetPinRequest("592614", "000000")), "PIN_INVALID");
        Assert.Equal(HttpStatusCode.NoContent, (await Put(session.AccessToken, new SetPinRequest("592614", "482913"))).StatusCode);
        await Fails(await Post(a, "/api/v1/account/pin/disable", new DisablePinRequest("482913"), bearer: session.AccessToken), "PIN_INVALID");
        Assert.Equal(HttpStatusCode.NoContent, (await Post(a, "/api/v1/account/pin/disable", new DisablePinRequest("592614"), bearer: session.AccessToken)).StatusCode);
        await using var check = database.Open();
        Assert.Null((await check.Accounts.SingleAsync(x => x.Id == session.AccountId)).PinHash);
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task AccessTokenCarriesExactExpiryAndSession()
    {
        using var device = new TestDevice();
        var session = await Register(device, RuPhone());
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(session.AccessToken);
        Assert.Equal("HS256", jwt.Header.Alg);
        Assert.Equal(session.ExpiresAt, DateTimeOffset.FromUnixTimeSeconds(long.Parse(jwt.Claims.Single(x => x.Type == "exp").Value)));
        Assert.Equal(TimeSpan.FromMinutes(15), jwt.ValidTo - jwt.ValidFrom);
        Assert.Equal(device.Id.ToString(), jwt.Claims.Single(x => x.Type == "device").Value);
        Assert.Equal("1", jwt.Claims.Single(x => x.Type == "generation").Value);
        Assert.Contains(jwt.Claims, x => x.Type == "sid");
        Assert.DoesNotContain(session.RefreshToken, session.AccessToken);
        Assert.InRange(session.RefreshExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromDays(29.9), TimeSpan.FromDays(30));
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task RefreshRotatesAndReuseRevokesFamily()
    {
        using var device = new TestDevice();
        var session = await Register(device, RuPhone());
        var next = await Ok<AuthSession>(await Refresh(device, session, session.RefreshToken, b));
        Assert.NotEqual(session.RefreshToken, next.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, await Me(next.AccessToken));
        await Fails(await Refresh(device, session, session.RefreshToken), "REFRESH_REUSED");
        // The whole family is gone, including the token the legitimate client holds and its access token.
        await Fails(await Refresh(device, next, next.RefreshToken), "SESSION_REVOKED");
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(next.AccessToken));
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task RefreshNeedsDeviceProofAndFreshChallenge()
    {
        using var device = new TestDevice();
        using var other = new TestDevice();
        var session = await Register(device, RuPhone());
        var challenge = await Ok<ChallengeResponse>(await Post(a, "/api/v1/auth/refresh/challenges", new RefreshChallengeRequest(device.Id), Peer()));
        await Fails(await Post(a, "/api/v1/auth/refresh", RefreshBody(other, session, session.RefreshToken, challenge)), "DEVICE_PROOF_INVALID");
        // A failed proof burns the challenge; a stolen refresh token alone is useless.
        await Fails(await Post(a, "/api/v1/auth/refresh", RefreshBody(device, session, session.RefreshToken, challenge)), "CHALLENGE_LOCKED");
        await Fails(await Post(a, "/api/v1/auth/refresh", RefreshBody(device, session, "rt1." + Guid.NewGuid() + "." + new string('A', 43), challenge)), "REFRESH_INVALID");
        var expiring = await Ok<ChallengeResponse>(await Post(a, "/api/v1/auth/refresh/challenges", new RefreshChallengeRequest(device.Id), Peer()));
        clock.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));
        try { await Fails(await Post(a, "/api/v1/auth/refresh", RefreshBody(device, session, session.RefreshToken, expiring)), "CHALLENGE_EXPIRED"); }
        finally { clock.Reset(); }
        Assert.Equal(HttpStatusCode.OK, (await Refresh(device, session, session.RefreshToken)).StatusCode);
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task LostRefreshResponseCanBeRetriedOnce()
    {
        using var device = new TestDevice();
        var session = await Register(device, RuPhone());
        var challenge = await Ok<ChallengeResponse>(await Post(a, "/api/v1/auth/refresh/challenges", new RefreshChallengeRequest(device.Id), Peer()));
        var body = RefreshBody(device, session, session.RefreshToken, challenge);
        var lost = await Ok<AuthSession>(await Post(a, "/api/v1/auth/refresh", body));
        var retried = await Ok<AuthSession>(await Post(b, "/api/v1/auth/refresh", body));
        Assert.NotEqual(lost.RefreshToken, retried.RefreshToken);
        // The response that was "lost" must not become usable later.
        await Fails(await Refresh(device, lost, lost.RefreshToken), "REFRESH_REUSED");
        await Fails(await Refresh(device, retried, retried.RefreshToken), "SESSION_REVOKED");
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task LostResponseRetryExpiresAfterWindowOrAfterSuccessorUse()
    {
        using var device = new TestDevice();
        var session = await Register(device, RuPhone());
        var challenge = await Ok<ChallengeResponse>(await Post(a, "/api/v1/auth/refresh/challenges", new RefreshChallengeRequest(device.Id), Peer()));
        var body = RefreshBody(device, session, session.RefreshToken, challenge);
        var next = await Ok<AuthSession>(await Post(a, "/api/v1/auth/refresh", body));
        var after = await Ok<AuthSession>(await Refresh(device, next, next.RefreshToken));
        await Fails(await Post(b, "/api/v1/auth/refresh", body), "REFRESH_REUSED");
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(after.AccessToken));
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task ConcurrentRefreshWithOneTokenYieldsAtMostOneSurvivor()
    {
        using var device = new TestDevice();
        var session = await Register(device, RuPhone());
        var challenges = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            await Ok<ChallengeResponse>(await Post(a, "/api/v1/auth/refresh/challenges", new RefreshChallengeRequest(device.Id), Peer()))));
        var responses = await Task.WhenAll(challenges.Select((c, i) => Post(i == 0 ? a : b, "/api/v1/auth/refresh", RefreshBody(device, session, session.RefreshToken, c))));
        Assert.Single(responses, x => x.IsSuccessStatusCode);
        await Fails(responses.Single(x => !x.IsSuccessStatusCode), "REFRESH_REUSED");
        var winner = await Ok<AuthSession>(responses.Single(x => x.IsSuccessStatusCode));
        await Fails(await Refresh(device, winner, winner.RefreshToken), "SESSION_REVOKED");
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task LogoutEndsAccessAndRefresh()
    {
        using var device = new TestDevice();
        var session = await Register(device, RuPhone());
        Assert.Equal(HttpStatusCode.NoContent, (await Post(b, "/api/v1/auth/logout", new { }, bearer: session.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(session.AccessToken));
        await Fails(await Refresh(device, session, session.RefreshToken), "SESSION_REVOKED");
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task TransferRacingDevReplacementLeavesOneActivePhoneAndNoOldDelivery()
    {
        using var alice1 = new TestDevice();
        using var bob = new TestDevice();
        var phone = RuPhone();
        var alice = await Register(alice1, phone);
        var bobSession = await Register(bob, RuPhone());
        var accepted = await Ok<AcceptedMessage>(await Post(a, "/api/v1/messages", new SendRequest(Guid.NewGuid(), alice.AccountId, alice1.Id, 1, 3,
            RandomNumberGenerator.GetBytes(64)), bearer: bobSession.AccessToken));

        await AgeChallenges(phone);
        using var alice2 = new TestDevice();
        using var alice3 = new TestDevice();
        var c = await Start(alice2, phone);
        var verify = Verify(alice2, c, phone, CodeFor(phone));
        using var replace = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/dev/accounts/{alice.AccountId}/replace-device")
        { Content = JsonContent.Create(new ProvisionRequest("Synthetic", alice3.Id, alice3.RegistrationId, alice3.IdentityKey), options: Json) };
        replace.Headers.Add("X-Dev-Provisioning-Token", AuthHost.Secrets["Dev:ProvisioningKey"]);
        var results = await Task.WhenAll(Post(a, "/api/v1/auth/sms/verify", verify), b.SendAsync(replace));
        Assert.All(results, r => Assert.True(r.IsSuccessStatusCode));

        await using var db = database.Open();
        var devices = await db.Devices.Where(x => x.AccountId == alice.AccountId).ToListAsync();
        Assert.Single(devices, x => x.Active);
        Assert.Equal(3, (await db.Accounts.SingleAsync(x => x.Id == alice.AccountId)).Generation);
        Assert.Equal(3, devices.Single(x => x.Active).Generation);
        Assert.Null((await db.Envelopes.SingleAsync(x => x.Sequence == accepted.Sequence)).Ciphertext);
        var active = devices.Single(d => d.Active).Id;
        Assert.False(await db.RefreshFamilies.AnyAsync(x => x.AccountId == alice.AccountId && x.RevokedAt == null && x.DeviceId != active));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(alice.AccessToken));
        using var inbox = new HttpRequestMessage(HttpMethod.Get, "/api/v1/inbox");
        inbox.Headers.Authorization = new("Bearer", alice.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.SendAsync(inbox)).StatusCode);
        await Fails(await Refresh(alice1, alice, alice.RefreshToken), "SESSION_REVOKED");
        await Fails(await Post(a, "/api/v1/auth/refresh/challenges", new RefreshChallengeRequest(alice1.Id), Peer()), "SESSION_REVOKED");
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task TransportWorksWithAuthSessionsAndIdentityChangeIsNotSilent()
    {
        using var aliceDevice = new TestDevice();
        using var bobDevice = new TestDevice();
        var alicePhone = RuPhone();
        var alice = await Register(aliceDevice, alicePhone);
        var bob = await Register(bobDevice, RuPhone());
        var upload = new PublishBundleRequest(1, [5, .. new byte[32]], new byte[64],
            Enumerable.Range(1, 4).Select(i => new PublicPreKey(i, [5, .. new byte[32]])).ToArray(),
            Enumerable.Range(1, 4).Select(i => new PublicPqPreKey(i, new byte[1569], new byte[64])).ToArray());
        Assert.Equal(HttpStatusCode.NoContent, (await Post(a, "/api/v1/keys/bundles", upload, bearer: alice.AccessToken)).StatusCode);
        var claim = new ClaimRequest(alice.AccountId, Guid.NewGuid());
        var bundle = await Ok<ClaimedBundle>(await Post(b, "/api/v1/keys/claim", claim, bearer: bob.AccessToken));
        Assert.Equal(aliceDevice.IdentityKey, bundle.IdentityKey);
        var message = new SendRequest(Guid.NewGuid(), alice.AccountId, bundle.DeviceId, bundle.Generation, 3, RandomNumberGenerator.GetBytes(128));
        var first = await Ok<AcceptedMessage>(await Post(a, "/api/v1/messages", message, bearer: bob.AccessToken));
        Assert.Equal(first, await Ok<AcceptedMessage>(await Post(b, "/api/v1/messages", message, bearer: bob.AccessToken)));
        using (var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/inbox"))
        {
            get.Headers.Authorization = new("Bearer", alice.AccessToken);
            var page = await Ok<InboxPage>(await a.SendAsync(get));
            Assert.Equal(message.Ciphertext, Assert.Single(page.Items).Ciphertext);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await Post(a, "/api/v1/inbox/ack", new AckRequest([first.Sequence]), bearer: alice.AccessToken)).StatusCode);

        // Alice moves to a new phone. Bob's old addressing is refused instead of silently re-keyed.
        await AgeChallenges(alicePhone);
        using var aliceNew = new TestDevice();
        var c = await Start(aliceNew, alicePhone);
        var moved = (await Ok<ActivationResponse>(await Post(a, "/api/v1/auth/sms/verify", Verify(aliceNew, c, alicePhone, CodeFor(alicePhone))))).Session!;
        await Fails(await Post(a, "/api/v1/messages", message with { ClientMessageId = Guid.NewGuid() }, bearer: bob.AccessToken), "RECIPIENT_DEVICE_CHANGED");
        Assert.Equal(HttpStatusCode.NoContent, (await Post(a, "/api/v1/keys/bundles", upload, bearer: moved.AccessToken)).StatusCode);
        var rebundle = await Ok<ClaimedBundle>(await Post(b, "/api/v1/keys/claim", claim with { ClaimId = Guid.NewGuid() }, bearer: bob.AccessToken));
        Assert.Equal(2, rebundle.Generation);
        Assert.Equal(aliceNew.IdentityKey, rebundle.IdentityKey); // Client sees a new identity and must not trust it automatically.
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task ForwardedForIsHonouredOnlyFromTrustedProxy()
    {
        await using var proxied = new AuthHost(database, clock, new Dictionary<string, string>(Roomy) { ["Auth:TrustedProxies:0"] = "10.9.9.9" });
        var client = proxied.CreateClient();
        // Through the trusted proxy every client is counted separately.
        for (var i = 0; i < 6; i++)
        {
            using var device = new TestDevice();
            await Ok<ChallengeResponse>(await Post(client, "/api/v1/auth/sms/challenges", Challenge(device, RuPhone()), "10.9.9.9", forwarded: Peer()));
        }
        // A direct client cannot spread itself over fake addresses.
        var direct = Peer();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var device = new TestDevice();
            statuses.Add((await Post(client, "/api/v1/auth/sms/challenges", Challenge(device, RuPhone()), direct, forwarded: Peer())).StatusCode);
        }
        Assert.Equal(5, statuses.Count(x => x == HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task EmptyTrustedProxyListDoesNotAllowSpoofingSmsIpLimit()
    {
        // appsettings.json defaults to no trusted proxies. A different X-Forwarded-For
        // on each request must not spread one TCP peer across multiple limit buckets.
        var direct = Peer();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var device = new TestDevice();
            var response = await Post(i % 2 == 0 ? a : b, "/api/v1/auth/sms/challenges",
                Challenge(device, RuPhone()), direct, forwarded: Peer());
            statuses.Add(response.StatusCode);
            if (i == 5) await Fails(response, "RATE_LIMITED");
        }
        Assert.Equal(5, statuses.Count(x => x == HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(5, AllSms.Count());
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task DailyBudgetStopsPaidSends()
    {
        await using var tight = new AuthHost(database, clock, new Dictionary<string, string> { ["Auth:Sms:Limits:DailyBudgetKopecks"] = "1200" });
        var client = tight.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            using var device = new TestDevice();
            await Ok<ChallengeResponse>(await Post(client, "/api/v1/auth/sms/challenges", Challenge(device, RuPhone()), Peer()));
        }
        using var over = new TestDevice();
        var phone = RuPhone();
        await Fails(await Post(client, "/api/v1/auth/sms/challenges", Challenge(over, phone), Peer()), "SMS_UNAVAILABLE");
        Assert.DoesNotContain(tight.Sms.Sent, x => x.Phone == phone);
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task FakeProviderIsWiredAndRealProviderIsNeverCalled()
    {
        using var device = new TestDevice();
        await Register(device, RuPhone());
        Assert.IsType<FakeSms>(first.Services.GetRequiredService<ISmsSender>());
        Assert.IsType<FakeSms>(second.Services.GetRequiredService<ISmsSender>());
        Assert.Equal(0, first.Network.Calls + second.Network.Calls);
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task LogsAndResponsesCarryNoSecrets()
    {
        using var device = new TestDevice();
        using var next = new TestDevice();
        var phone = RuPhone();
        var c = await Start(device, phone);
        var code = CodeFor(phone);
        var verifyResponse = await Post(a, "/api/v1/auth/sms/verify", Verify(device, c, phone, code));
        var session = (await Ok<ActivationResponse>(verifyResponse)).Session!;
        Assert.Equal("no-store", verifyResponse.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await Put(session.AccessToken, new SetPinRequest("482913", null))).StatusCode);
        var refreshed = await Ok<AuthSession>(await Refresh(device, session, session.RefreshToken));
        await AgeChallenges(phone);
        var transfer = await Start(next, phone);
        var bodies = new List<string>
        {
            await (await Post(a, "/api/v1/auth/sms/verify", Verify(next, transfer, phone, CodeFor(phone)))).Content.ReadAsStringAsync(),
            await (await Post(a, "/api/v1/auth/pin/verify", new PinVerifyRequest(transfer.ChallengeId, phone, "000000", ActivationProof(next, transfer, phone)))).Content.ReadAsStringAsync(),
            await (await Post(a, "/api/v1/auth/sms/challenges", Challenge(next, phone), Peer())).Content.ReadAsStringAsync(),
        };
        // Give the background worker a tick so its logs are included too.
        await Task.Delay(1500);
        var logs = string.Join("\n", first.Logs.Lines.Concat(second.Logs.Lines));
        Assert.NotEmpty(logs);
        string[] secrets = [code, CodeFor(phone), "482913", phone, phone.TrimStart('+'), session.AccessToken, session.RefreshToken,
            refreshed.AccessToken, refreshed.RefreshToken, refreshed.RefreshToken.Split('.')[2], AuthHost.Secrets["Auth:SmsCodeKey"],
            AuthHost.Secrets["Auth:PinPepper"], AuthHost.Secrets["Auth:PhoneIndexKey"], AuthHost.Secrets["Auth:SigningKey"]];
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, logs);
            foreach (var body in bodies) Assert.DoesNotContain(secret, body);
        }
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task DevCodeEndpointNeedsOperatorKeyAndExistsOnlyWithFakeProvider()
    {
        using var device = new TestDevice();
        var phone = RuPhone();
        var c = await Start(device, phone);
        await Fails(await Post(a, "/api/v1/dev/sms/code", new DevSmsCodeRequest(c.ChallengeId)), "UNAUTHORIZED");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dev/sms/code") { Content = JsonContent.Create(new DevSmsCodeRequest(c.ChallengeId), options: Json) };
        request.Headers.Add("X-Dev-Provisioning-Token", AuthHost.Secrets["Dev:ProvisioningKey"]);
        var response = await a.SendAsync(request);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(CodeFor(phone), (await Ok<System.Text.Json.JsonElement>(response)).GetProperty("code").GetString());

        await using var real = new AuthHost(database, clock, new Dictionary<string, string>
            { ["Auth:Sms:Provider"] = "SmsRu", ["Auth:Sms:SmsRu:ApiKey"] = "synthetic-not-a-key" });
        using var probe = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dev/sms/code") { Content = JsonContent.Create(new DevSmsCodeRequest(c.ChallengeId), options: Json) };
        probe.Headers.Add("X-Dev-Provisioning-Token", AuthHost.Secrets["Dev:ProvisioningKey"]);
        Assert.Equal(HttpStatusCode.NotFound, (await real.CreateClient().SendAsync(probe)).StatusCode);
        Assert.Equal(0, real.Network.Calls);
    }
}
