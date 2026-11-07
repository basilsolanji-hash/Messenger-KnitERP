using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Messenger.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Messenger.Tests.Api;

namespace Messenger.Tests;

/// <summary>Service administration panel: docs/ADMIN_PANEL.md, section "Приёмка".</summary>
[Trait("Category", "PostgreSQL")]
public sealed class AdminTests : IAsyncLifetime
{
    private AuthDatabase database = null!;
    private readonly MutableClock clock = new();
    private AuthHost host = null!;
    private HttpClient client = null!;
    private const string Peer = "198.18.7.10";

    public async Task InitializeAsync()
    {
        database = await AuthDatabase.Create();
        host = new AuthHost(database, clock, new Dictionary<string, string> { ["Admin:Enabled"] = "true" });
        client = host.CreateClient();
    }
    public async Task DisposeAsync() { await host.DisposeAsync(); await database.DisposeAsync(); }

    private sealed record Session(string Cookie, string Csrf, string Login);

    private async Task<(AdminApi.Issued Issued, byte[] Key)> NewAdmin(string login, AdminRole role)
    {
        await using var db = database.Open();
        var creds = host.Services.GetRequiredService<AdminCredentials>();
        var issued = await AdminApi.CreateUser(db, creds, login, role, clock.GetUtcNow(), default);
        var stored = await db.AdminUsers.SingleAsync(x => x.Login == login);
        return (issued, creds.TotpKey(stored.TotpSecret));
    }
    private string Code(byte[] key, int shift = 0) => Totp.Code(key, Totp.Step(clock.GetUtcNow()) + shift);

    private async Task<HttpResponseMessage> LoginRaw(string login, string password, string code, string peer = Peer) =>
        await Post(client, "/api/v1/admin/login", new { login, password, code }, peer);

    private async Task<Session> Login(string login, string password, byte[] key)
    {
        var response = await LoginRaw(login, password, Code(key));
        var me = await Ok<AdminApi.Me>(response);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(AdminApi.Cookie + "=", StringComparison.Ordinal));
        Assert.Contains("HttpOnly", cookie); Assert.Contains("Secure", cookie); Assert.Contains("SameSite=Strict", cookie);
        return new(cookie.Split(';')[0], me.Csrf, me.Login);
    }

    private async Task<HttpResponseMessage> Call(Session? s, HttpMethod method, string path, object? body = null, bool csrf = true,
        string peer = Peer, string? origin = null, HttpClient? transport = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (s is not null) request.Headers.Add("Cookie", s.Cookie);
        if (s is not null && csrf) request.Headers.Add(AdminApi.CsrfHeader, s.Csrf);
        if (origin is not null) request.Headers.Add("Origin", origin);
        request.Headers.Add("X-Test-Peer", peer);
        return await (transport ?? client).SendAsync(request);
    }

    [Fact]
    public async Task DisabledPanelHasNoRoutes()
    {
        await using var off = new AuthHost(database, clock);
        var c = off.CreateClient();
        foreach (var path in new[] { "/admin", "/admin/app.js", "/api/v1/admin/me", "/api/v1/admin/metrics" })
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(c, "/api/v1/admin/login", new { login = "x", password = "y", code = "1" })).StatusCode);
    }

    [Fact]
    public async Task LoginNeedsPasswordAndFreshTotpAndLocksAfterFiveFailures()
    {
        var (admin, key) = await NewAdmin("root.admin", AdminRole.Admin);
        await Fails(await LoginRaw("root.admin", admin.Password, "000000"), "ADMIN_LOGIN_FAILED");
        await Fails(await LoginRaw("root.admin", "wrong-password-123", Code(key)), "ADMIN_LOGIN_FAILED");
        await Fails(await LoginRaw("nobody", admin.Password, Code(key)), "ADMIN_LOGIN_FAILED");
        var session = await Login("root.admin", admin.Password, key);
        // The same code cannot be used twice.
        await Fails(await LoginRaw("root.admin", admin.Password, Code(key)), "ADMIN_LOGIN_FAILED");
        Assert.Equal(HttpStatusCode.OK, (await Call(session, HttpMethod.Get, "/api/v1/admin/me")).StatusCode);

        var (other, otherKey) = await NewAdmin("victim", AdminRole.Support);
        for (var i = 0; i < AdminApi.MaxFailedLogins; i++) await Fails(await LoginRaw("victim", "bad-password-0000", Code(otherKey)), "ADMIN_LOGIN_FAILED");
        var locked = await Fails(await LoginRaw("victim", other.Password, Code(otherKey)), "ADMIN_LOCKED");
        Assert.NotNull(locked.RetryAfterSeconds);
        clock.Advance(AdminApi.Lockout + TimeSpan.FromSeconds(31));
        await Login("victim", other.Password, otherKey);

        await using var db = database.Open();
        var log = await db.AdminAudit.Where(x => x.Action == "login").ToListAsync();
        Assert.Contains(log, x => !x.Success && x.Detail == "code");
        Assert.Contains(log, x => !x.Success && x.Detail == "password");
        Assert.Contains(log, x => !x.Success && x.Detail == "unknown login");
        Assert.Contains(log, x => !x.Success && x.Detail == "code reused");
        Assert.Contains(log, x => !x.Success && x.Detail == "locked");
        Assert.DoesNotContain(log, x => x.Detail.Contains(admin.Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SessionNeedsCookieCsrfAndSameNetworkAndExpiresWhenIdle()
    {
        var (admin, key) = await NewAdmin("ops", AdminRole.Admin);
        var s = await Login("ops", admin.Password, key);
        await Fails(await Call(null, HttpMethod.Get, "/api/v1/admin/me"), "ADMIN_SESSION_REQUIRED");
        await Fails(await Call(s, HttpMethod.Post, "/api/v1/admin/users", new { login = "new.one", role = "support" }, csrf: false), "ADMIN_CSRF");
        await Fails(await Call(s, HttpMethod.Post, "/api/v1/admin/users", new { login = "new.one", role = "support" }, origin: "https://evil.example"), "ADMIN_CSRF");
        Assert.Equal(HttpStatusCode.OK, (await Call(s, HttpMethod.Post, "/api/v1/admin/users", new { login = "new.one", role = "support" })).StatusCode);

        clock.Advance(AdminApi.Idle + TimeSpan.FromSeconds(1));
        await Fails(await Call(s, HttpMethod.Get, "/api/v1/admin/me"), "ADMIN_SESSION_REQUIRED");

        clock.Advance(TimeSpan.FromSeconds(31));
        var again = await Login("ops", admin.Password, key);
        // The same cookie from another network ends the session for good.
        await Fails(await Call(again, HttpMethod.Get, "/api/v1/admin/me", peer: "203.0.113.9"), "ADMIN_SESSION_REQUIRED");
        await Fails(await Call(again, HttpMethod.Get, "/api/v1/admin/me"), "ADMIN_SESSION_REQUIRED");

        clock.Advance(TimeSpan.FromSeconds(31));
        var third = await Login("ops", admin.Password, key);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(third, HttpMethod.Post, "/api/v1/admin/logout")).StatusCode);
        await Fails(await Call(third, HttpMethod.Get, "/api/v1/admin/me"), "ADMIN_SESSION_REQUIRED");
    }

    [Fact]
    public async Task RolesLimitWhatEachUserCanDo()
    {
        var (admin, adminKey) = await NewAdmin("chief", AdminRole.Admin);
        var (support, supportKey) = await NewAdmin("helpdesk", AdminRole.Support);
        var (auditor, auditorKey) = await NewAdmin("watcher", AdminRole.Auditor);
        var account = await SyntheticAccount();
        var sup = await Login("helpdesk", support.Password, supportKey);
        var aud = await Login("watcher", auditor.Password, auditorKey);
        var adm = await Login("chief", admin.Password, adminKey);

        Assert.Equal(HttpStatusCode.OK, (await Call(sup, HttpMethod.Get, $"/api/v1/admin/accounts/{account}")).StatusCode);
        await Fails(await Call(sup, HttpMethod.Post, $"/api/v1/admin/accounts/{account}/suspend", new { reason = "test reason" }), "ADMIN_FORBIDDEN");
        await Fails(await Call(sup, HttpMethod.Get, "/api/v1/admin/audit"), "ADMIN_FORBIDDEN");
        await Fails(await Call(sup, HttpMethod.Get, "/api/v1/admin/users"), "ADMIN_FORBIDDEN");
        await Fails(await Call(aud, HttpMethod.Get, "/api/v1/admin/accounts"), "ADMIN_FORBIDDEN");
        Assert.Equal(HttpStatusCode.OK, (await Call(aud, HttpMethod.Get, "/api/v1/admin/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(aud, HttpMethod.Get, "/api/v1/admin/metrics")).StatusCode);

        // The last active administrator cannot be removed, and nobody changes their own account here.
        var users = await Ok<List<AdminApi.UserRow>>(await Call(adm, HttpMethod.Get, "/api/v1/admin/users"));
        var chief = users.Single(x => x.Login == "chief");
        await Fails(await Call(adm, HttpMethod.Post, $"/api/v1/admin/users/{chief.Id}/disable"), "SELF_CHANGE_FORBIDDEN");
        var helpdesk = users.Single(x => x.Login == "helpdesk");
        Assert.Equal(HttpStatusCode.NoContent, (await Call(adm, HttpMethod.Post, $"/api/v1/admin/users/{helpdesk.Id}/role", new { role = "admin" })).StatusCode);
        // The promoted user's sessions were ended; the new role applies from the next sign-in.
        await Fails(await Call(sup, HttpMethod.Get, "/api/v1/admin/me"), "ADMIN_SESSION_REQUIRED");
        Assert.Equal(HttpStatusCode.NoContent, (await Call(adm, HttpMethod.Post, $"/api/v1/admin/users/{helpdesk.Id}/disable")).StatusCode);
        clock.Advance(TimeSpan.FromSeconds(31));
        await Fails(await LoginRaw("helpdesk", support.Password, Code(supportKey)), "ADMIN_LOGIN_FAILED");

        var reset = await Ok<AdminApi.Issued>(await Call(adm, HttpMethod.Post, $"/api/v1/admin/users/{users.Single(x => x.Login == "watcher").Id}/reset-credentials"));
        await Fails(await Call(aud, HttpMethod.Get, "/api/v1/admin/me"), "ADMIN_SESSION_REQUIRED");
        await Fails(await LoginRaw("watcher", auditor.Password, Code(auditorKey)), "ADMIN_LOGIN_FAILED");
        Assert.NotEqual(auditor.Password, reset.Password);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("role")]
    public async Task ConcurrentAdministratorsCannotRemoveEachOthersAccess(string action)
    {
        var (first, firstKey) = await NewAdmin("first", AdminRole.Admin);
        var (second, secondKey) = await NewAdmin("second", AdminRole.Admin);
        var one = await Login(first.Login, first.Password, firstKey);
        var two = await Login(second.Login, second.Password, secondKey);
        await using var otherHost = new AuthHost(database, clock, new Dictionary<string, string> { ["Admin:Enabled"] = "true" });
        using var otherClient = otherHost.CreateClient();
        // Keep both updates in flight long enough to expose a check-then-write race.
        // This trigger exists only in this test's isolated PostgreSQL schema.
        await using var db = database.Open();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION slow_admin_update() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_sleep(0.3); RETURN NEW; END $$;
            CREATE TRIGGER slow_admin_update BEFORE UPDATE ON admin_users
            FOR EACH ROW EXECUTE FUNCTION slow_admin_update();
            """);
        object? body = action == "role" ? new { role = "support" } : null;
        var replies = await Task.WhenAll(
            Call(one, HttpMethod.Post, $"/api/v1/admin/users/{second.Id}/{action}", body),
            Call(two, HttpMethod.Post, $"/api/v1/admin/users/{first.Id}/{action}", body, transport: otherClient));
        Assert.Single(replies, x => x.IsSuccessStatusCode);
        await Fails(replies.Single(x => !x.IsSuccessStatusCode), "ADMIN_SESSION_REQUIRED");
        Assert.Equal(1, await db.AdminUsers.CountAsync(x => x.Role == AdminRole.Admin && !x.Disabled));
    }

    [Fact]
    public async Task FailedAuditRollsBackRoleChangeAndSessionRevocation()
    {
        var (admin, adminKey) = await NewAdmin("chief", AdminRole.Admin);
        var (support, supportKey) = await NewAdmin("helper", AdminRole.Support);
        var adm = await Login(admin.Login, admin.Password, adminKey);
        var sup = await Login(support.Login, support.Password, supportKey);
        await using var db = database.Open();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION refuse_role_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."Action" = 'change-role' THEN RAISE EXCEPTION 'test audit failure'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER refuse_role_audit BEFORE INSERT ON admin_audit
            FOR EACH ROW EXECUTE FUNCTION refuse_role_audit();
            """);
        var response = await Call(adm, HttpMethod.Post, $"/api/v1/admin/users/{support.Id}/role", new { role = "admin" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(AdminRole.Support, (await db.AdminUsers.AsNoTracking().SingleAsync(x => x.Id == support.Id)).Role);
        Assert.Equal(HttpStatusCode.OK, (await Call(sup, HttpMethod.Get, "/api/v1/admin/me")).StatusCode);
        Assert.False(await db.AdminAudit.AnyAsync(x => x.Action == "change-role"));
    }

    [Fact]
    public async Task PasswordChangeKeepsCurrentSessionRevokesOthersAndAuditsWrongPassword()
    {
        var (admin, key) = await NewAdmin("chief", AdminRole.Admin);
        var first = await Login(admin.Login, admin.Password, key);
        clock.Advance(TimeSpan.FromSeconds(31));
        var current = await Login(admin.Login, admin.Password, key);
        const string next = "new-password-for-admin-2026";
        await Fails(await Call(current, HttpMethod.Post, "/api/v1/admin/me/password",
            new { current = "wrong-password", next }), "PASSWORD_INVALID");
        await using var db = database.Open();
        Assert.True(await db.AdminAudit.AnyAsync(x => x.Action == "password-change" && !x.Success));
        Assert.Equal(HttpStatusCode.NoContent, (await Call(current, HttpMethod.Post, "/api/v1/admin/me/password",
            new { current = admin.Password, next })).StatusCode);
        await Fails(await Call(first, HttpMethod.Get, "/api/v1/admin/me"), "ADMIN_SESSION_REQUIRED");
        Assert.Equal(HttpStatusCode.OK, (await Call(current, HttpMethod.Get, "/api/v1/admin/me")).StatusCode);
        clock.Advance(TimeSpan.FromSeconds(31));
        await Fails(await LoginRaw(admin.Login, admin.Password, Code(key)), "ADMIN_LOGIN_FAILED");
        await Login(admin.Login, next, key);
        Assert.True(await db.AdminAudit.AnyAsync(x => x.Action == "password-change" && x.Success));
    }

    [Fact]
    public async Task SuspensionEndsSessionsAndRefusesDeliveryUntilResumedAndEverythingIsAudited()
    {
        var (admin, key) = await NewAdmin("chief", AdminRole.Admin);
        var s = await Login("chief", admin.Password, key);
        var (alice, aliceToken, aliceDevice) = await SyntheticSession("Alice");
        var (bob, bobToken, _) = await SyntheticSession("Bob");
        Assert.Equal(HttpStatusCode.OK, await Me(aliceToken));

        await Fails(await Call(s, HttpMethod.Post, $"/api/v1/admin/accounts/{alice}/suspend", new { reason = "" }), "REASON_REQUIRED");
        Assert.Equal(HttpStatusCode.OK, (await Call(s, HttpMethod.Post, $"/api/v1/admin/accounts/{alice}/suspend", new { reason = "Жалоба #12: рассылка" })).StatusCode);
        await Fails(await Call(s, HttpMethod.Post, $"/api/v1/admin/accounts/{alice}/suspend", new { reason = "again please" }), "ALREADY_SUSPENDED");
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(aliceToken));
        // Nobody can deliver to or claim keys of a suspended account.
        var send = await Post(client, "/api/v1/messages", new SendRequest(Guid.NewGuid(), alice, aliceDevice, 1, 3, new byte[64]), bearer: bobToken);
        await Fails(send, "DELIVERY_UNAVAILABLE");
        await Fails(await Post(client, "/api/v1/keys/claim", new ClaimRequest(alice, Guid.NewGuid()), bearer: bobToken), "NOT_FOUND");

        var detail = await Ok<AdminApi.AccountDetail>(await Call(s, HttpMethod.Get, $"/api/v1/admin/accounts/{alice}"));
        Assert.True(detail.Account.Suspended);
        Assert.Equal("Жалоба #12: рассылка", detail.Account.SuspendReason);
        var metrics = await Ok<AdminApi.Metrics>(await Call(s, HttpMethod.Get, "/api/v1/admin/metrics"));
        Assert.Equal(1, metrics.Suspended);

        Assert.Equal(HttpStatusCode.OK, (await Call(s, HttpMethod.Post, $"/api/v1/admin/accounts/{alice}/resume", new { reason = "Проверено" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await Me(aliceToken)); // Synthetic dev token has no refresh family; the device is active again.

        var audit = await Ok<JsonElement>(await Call(s, HttpMethod.Get, "/api/v1/admin/audit"));
        var actions = audit.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("action").GetString()).ToList();
        Assert.Contains("suspend-account", actions);
        Assert.Contains("resume-account", actions);
        Assert.Contains("login", actions);

        // Append-only: the database refuses to change or delete audit rows.
        await using var db = database.Open();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE admin_audit SET \"Detail\" = 'x'"));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM admin_audit"));

        var csv = await (await Call(s, HttpMethod.Get, "/api/v1/admin/audit.csv")).Content.ReadAsStringAsync();
        Assert.StartsWith("id;at;login;remote;action;target;detail;success\n", csv);
        Assert.Contains("suspend-account", csv);
        Assert.Equal(bob, (await Ok<JsonElement>(await Call(s, HttpMethod.Get, $"/api/v1/admin/accounts?q={bob}"))).GetProperty("items")[0].GetProperty("accountId").GetGuid());
    }

    [Fact]
    public async Task PhoneSearchFindsBySmsNumberWithoutStoringItAndIsLimited()
    {
        var (admin, key) = await NewAdmin("chief", AdminRole.Admin);
        var s = await Login("chief", admin.Password, key);
        var found = await Ok<JsonElement>(await Call(s, HttpMethod.Post, "/api/v1/admin/accounts/search-phone", new { phone = "+79001112233" }));
        Assert.Equal(0, found.GetProperty("items").GetArrayLength());
        await Fails(await Call(s, HttpMethod.Post, "/api/v1/admin/accounts/search-phone", new { phone = "abc" }), "PHONE_INVALID");
        for (var i = 0; i < 18; i++) await Call(s, HttpMethod.Post, "/api/v1/admin/accounts/search-phone", new { phone = "+79001112233" });
        await Fails(await Call(s, HttpMethod.Post, "/api/v1/admin/accounts/search-phone", new { phone = "+79001112233" }), "RATE_LIMITED");
        await using var db = database.Open();
        Assert.False(await db.AdminAudit.AnyAsync(x => x.Detail.Contains("9001112233") || x.Target.Contains("9001112233")));
        Assert.True(await db.AdminAudit.CountAsync(x => x.Action == "search-phone") >= 19);
    }

    [Fact]
    public async Task ResponsesNeverCarryTokensCiphertextOrSecrets()
    {
        var (admin, key) = await NewAdmin("chief", AdminRole.Admin);
        var s = await Login("chief", admin.Password, key);
        var (alice, aliceToken, aliceDevice) = await SyntheticSession("Alice");
        var (_, bobToken, _) = await SyntheticSession("Bob");
        var cipher = Enumerable.Repeat((byte)0x5a, 64).ToArray();
        await Ok<JsonElement>(await Post(client, "/api/v1/messages", new SendRequest(Guid.NewGuid(), alice, aliceDevice, 1, 3, cipher), bearer: bobToken));
        var texts = new List<string>();
        foreach (var path in new[] { "/api/v1/admin/me", "/api/v1/admin/metrics", "/api/v1/admin/accounts", $"/api/v1/admin/accounts/{alice}",
                     "/api/v1/admin/audit", "/api/v1/admin/audit.csv", "/api/v1/admin/users" })
        {
            var response = await Call(s, HttpMethod.Get, path);
            Assert.True(response.IsSuccessStatusCode, path);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            texts.Add(await response.Content.ReadAsStringAsync());
        }
        var all = string.Join("\n", texts);
        foreach (var secret in new[] { aliceToken, bobToken, admin.Password, Convert.ToBase64String(cipher), AuthHost.Secrets["Dev:ProvisioningKey"] })
            Assert.DoesNotContain(secret, all);
        Assert.DoesNotContain("TotpSecret", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PasswordHash", all, StringComparison.OrdinalIgnoreCase);
        var page = await client.GetAsync("/admin");
        Assert.Contains("frame-ancestors 'none'", string.Join(";", page.Headers.GetValues("Content-Security-Policy")));
        Assert.DoesNotContain("innerHTML", await (await client.GetAsync("/admin/app.js")).Content.ReadAsStringAsync());
    }

    // ---- helpers ----

    private async Task<Guid> SyntheticAccount() => (await SyntheticSession("Synthetic")).Account;

    private async Task<(Guid Account, string Token, Guid Device)> SyntheticSession(string name)
    {
        var device = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dev/accounts")
        { Content = JsonContent.Create(new ProvisionRequest(name, device, 77, [5, .. new byte[32]]), options: Json) };
        request.Headers.Add("X-Dev-Provisioning-Token", AuthHost.Secrets["Dev:ProvisioningKey"]);
        var issued = await Ok<JsonElement>(await client.SendAsync(request));
        var account = issued.GetProperty("accountId").GetGuid();
        var token = issued.GetProperty("accessToken").GetString()!;
        // Publish keys so that delivery to this device is possible.
        var published = await Post(client, "/api/v1/keys/bundles", new PublishBundleRequest(1, new byte[33], new byte[64],
            Enumerable.Range(1, 4).Select(i => new PublicPreKey(i, new byte[33])).ToArray(),
            Enumerable.Range(1, 4).Select(i => new PublicPqPreKey(i, new byte[1569], new byte[64])).ToArray()), bearer: token);
        Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);
        return (account, token, device);
    }

    private async Task<HttpStatusCode> Me(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new("Bearer", token);
        return (await client.SendAsync(request)).StatusCode;
    }
}
