using Microsoft.EntityFrameworkCore;

namespace Messenger.Api;

/// <summary>
/// Operator panel of the closed test stand (PRODUCT_SPEC section 17). Mapped only together with the other operator
/// routes (Development, Dev:EnableOperatorEndpoints=true); Nginx serves /api/v1/dev only to operator addresses.
/// Every data route needs the operator key in a header, so a page opened from another site cannot use it (no cookies).
/// The panel never returns message content, keys, codes other than the one requested, tokens or phone numbers
/// (the server does not store numbers). Every change is logged as "Operator action" without secrets.
/// </summary>
public static class OperatorPanel
{
    public sealed record DeviceRow(Guid DeviceId, Guid AccountId, int Generation, bool Active, string Kind, int LiveSessions);
    public sealed record Status(bool Ready, int ActiveDevices, int Devices, int LiveSessions, int QueuedEnvelopes, DateTimeOffset ServerTime);
    public sealed record Revoked(int Sessions);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/dev/panel", (HttpContext c) => Static(c, Page, "text/html; charset=utf-8"));
        app.MapGet("/api/v1/dev/panel.js", (HttpContext c) => Static(c, Script, "text/javascript; charset=utf-8"));

        app.MapPost("/api/v1/dev/panel/status", async (HttpContext c, IConfiguration config, MessengerDb db, TimeProvider clock, CancellationToken ct) =>
        {
            Access.ProvisioningKey(c, config);
            var now = clock.GetUtcNow();
            NoStore(c);
            return Results.Ok(new Status(await db.Database.CanConnectAsync(ct),
                await db.Devices.CountAsync(x => x.Active, ct), await db.Devices.CountAsync(ct),
                await db.RefreshFamilies.CountAsync(x => x.RevokedAt == null && x.AbsoluteExpiresAt > now, ct),
                await db.Envelopes.CountAsync(x => !x.Acknowledged && x.Ciphertext != null && x.ExpiresAt > now, ct), now));
        });

        app.MapPost("/api/v1/dev/panel/devices", async (HttpContext c, IConfiguration config, MessengerDb db, TimeProvider clock, CancellationToken ct) =>
        {
            Access.ProvisioningKey(c, config);
            var now = clock.GetUtcNow();
            var rows = await db.Devices.AsNoTracking()
                .OrderByDescending(x => x.Active).ThenBy(x => x.AccountId).Take(500)
                .Select(d => new DeviceRow(d.Id, d.AccountId, d.Generation, d.Active, d.AuthKey == null ? "synthetic" : "sms",
                    db.RefreshFamilies.Count(f => f.DeviceId == d.Id && f.RevokedAt == null && f.AbsoluteExpiresAt > now)))
                .ToListAsync(ct);
            NoStore(c);
            return Results.Ok(rows);
        });

        app.MapPost("/api/v1/dev/panel/devices/{deviceId:guid}/revoke", async (Guid deviceId, HttpContext c, IConfiguration config,
            MessengerDb db, TimeProvider clock, ConnectionRegistry registry, ILoggerFactory logs, CancellationToken ct) =>
        {
            Access.ProvisioningKey(c, config);
            var now = clock.GetUtcNow();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var device = await db.Devices.FromSqlInterpolated($"SELECT * FROM devices WHERE \"Id\" = {deviceId} FOR UPDATE").SingleOrDefaultAsync(ct);
            Transport.Require(device is not null, "NOT_FOUND", 404);
            device!.Active = false;
            var families = await db.RefreshFamilies.Where(x => x.DeviceId == deviceId && x.RevokedAt == null).ToListAsync(ct);
            foreach (var family in families) { family.RevokedAt = now; family.RevokedReason = "operator"; }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            registry.Revoke(deviceId);
            Audit(logs, c, "revoke-device", deviceId.ToString());
            NoStore(c);
            return Results.Ok(new Revoked(families.Count));
        });

        app.MapPost("/api/v1/dev/panel/sessions/revoke-all", async (HttpContext c, IConfiguration config, MessengerDb db,
            TimeProvider clock, ConnectionRegistry registry, ILoggerFactory logs, CancellationToken ct) =>
        {
            Access.ProvisioningKey(c, config);
            var now = clock.GetUtcNow();
            var families = await db.RefreshFamilies.Where(x => x.RevokedAt == null).ToListAsync(ct);
            foreach (var family in families) { family.RevokedAt = now; family.RevokedReason = "operator"; }
            await db.SaveChangesAsync(ct);
            foreach (var family in families) registry.RevokeSession(family.Id);
            Audit(logs, c, "revoke-all-sessions", families.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            NoStore(c);
            return Results.Ok(new Revoked(families.Count));
        });
    }

    private static void Audit(ILoggerFactory logs, HttpContext c, string action, string target) =>
        logs.CreateLogger("Messenger.Api.OperatorPanel").LogWarning("Operator action {Action} {Target} from {Remote}",
            action, target, c.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    private static void NoStore(HttpContext c) => c.Response.Headers.CacheControl = "no-store";

    private static IResult Static(HttpContext c, string body, string type)
    {
        var h = c.Response.Headers;
        NoStore(c);
        h.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'unsafe-inline'; " +
            "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h.XContentTypeOptions = "nosniff";
        return Results.Text(body, type);
    }

    private const string Page = """
        <!doctype html>
        <html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Ось коммуникации · операторская панель стенда</title>
        <style>
        body{font:15px/1.4 system-ui,sans-serif;margin:0;padding:16px;max-width:960px;color:#14262b;background:#f6f8f8}
        h1{font-size:20px}h2{font-size:16px;margin-top:28px}section{background:#fff;border:1px solid #d5dfe1;border-radius:8px;padding:12px 16px}
        input{font:inherit;padding:6px 8px;width:100%;max-width:420px;box-sizing:border-box}button{font:inherit;padding:6px 12px;margin:4px 4px 4px 0;cursor:pointer}
        .wide{overflow-x:auto}table{border-collapse:collapse;width:100%;font-size:13px}td,th{border-bottom:1px solid #e3eaeb;padding:4px 6px;text-align:left;white-space:nowrap}td.id{font:12px ui-monospace,monospace}
        .muted{color:#5b6f74}.err{color:#a1260d}.ok{color:#11663c}code{font-size:22px;letter-spacing:4px}
        </style></head><body>
        <h1>Операторская панель закрытого стенда</h1>
        <p class="muted">Только для оператора стенда. Текст переписки, ключи и номера телефонов панель не видит. Каждое изменение записывается в журнал сервера.</p>
        <section><label for="key">Операторский ключ (только в памяти этой страницы; после перезагрузки ввести снова)</label><br>
        <input id="key" type="password" autocomplete="off"> <button id="load">Открыть</button> <span id="state" class="muted"></span></section>
        <h2>Состояние</h2><section id="status" class="muted">Введите ключ.</section>
        <h2>Код из SMS для телефона</h2>
        <section><label for="challenge">Номер запроса с экрана телефона</label><br><input id="challenge" autocomplete="off">
        <button id="code">Показать код</button><p id="codeOut"></p>
        <p class="muted">Код показывается 60 секунд. Передавайте его голосом или другим каналом, не фотографируйте.</p></section>
        <h2>Устройства</h2>
        <section><button id="refresh">Обновить</button> <button id="revokeAll">Отозвать все сессии</button>
        <div class="wide"><table><thead><tr><th>Устройство</th><th>Аккаунт</th><th>Поколение</th><th>Тип</th><th>Сессии</th><th>Состояние</th><th></th></tr></thead>
        <tbody id="devices"></tbody></table></div></section>
        <script src="panel.js"></script></body></html>
        """;

    private const string Script = """
        'use strict';
        const $ = id => document.getElementById(id);
        let key = '';
        function say(el, text, cls) { el.textContent = text; el.className = cls || ''; }
        async function call(path, body) {
          const r = await fetch(path, { method: 'POST', cache: 'no-store', credentials: 'omit',
            headers: { 'X-Dev-Provisioning-Token': key, 'Content-Type': 'application/json' }, body: JSON.stringify(body || {}) });
          if (r.status === 401) throw new Error('Неверный операторский ключ.');
          if (r.status === 404) throw new Error('Не найдено.');
          if (!r.ok) throw new Error('Сервер ответил ' + r.status + '.');
          return r.json();
        }
        async function load(message) {
          try {
            const s = await call('/api/v1/dev/panel/status');
            say($('status'), 'API: ' + (s.ready ? 'готов' : 'нет связи с БД') + ' · активных устройств ' + s.activeDevices + ' из ' + s.devices +
              ' · живых сессий ' + s.liveSessions + ' · сообщений в очереди ' + s.queuedEnvelopes + ' · время сервера ' + new Date(s.serverTime).toLocaleString('ru-RU'));
            const rows = await call('/api/v1/dev/panel/devices');
            const body = $('devices'); body.replaceChildren();
            for (const d of rows) {
              const tr = document.createElement('tr');
              [d.deviceId, d.accountId, d.generation, d.kind === 'sms' ? 'SMS' : 'синтетическое', d.liveSessions, d.active ? 'активно' : 'отключено']
                .forEach((v, i) => { const td = document.createElement('td'); td.textContent = String(v); if (i < 2) td.className = 'id'; tr.appendChild(td); });
              const td = document.createElement('td');
              if (d.active) {
                const b = document.createElement('button'); b.textContent = 'Отозвать';
                b.addEventListener('click', () => revoke(d.deviceId)); td.appendChild(b);
              }
              tr.appendChild(td); body.appendChild(tr);
            }
            say($('state'), message || ('Обновлено ' + new Date().toLocaleTimeString('ru-RU')), 'ok');
          } catch (e) { say($('state'), e.message, 'err'); }
        }
        async function revoke(id) {
          if (!confirm('Отключить устройство ' + id + '? Телефону придётся войти заново.')) return;
          try { const r = await call('/api/v1/dev/panel/devices/' + encodeURIComponent(id) + '/revoke'); await load('Устройство отключено, отозвано сессий: ' + r.sessions); }
          catch (e) { say($('state'), e.message, 'err'); }
        }
        let hide;
        $('load').addEventListener('click', () => { key = $('key').value; $('key').value = ''; load(); });
        $('refresh').addEventListener('click', () => load());
        $('revokeAll').addEventListener('click', async () => {
          if (!confirm('Отозвать все SMS-сессии? Всем телефонам придётся войти заново.')) return;
          try { const r = await call('/api/v1/dev/panel/sessions/revoke-all'); await load('Отозвано сессий: ' + r.sessions); }
          catch (e) { say($('state'), e.message, 'err'); }
        });
        $('code').addEventListener('click', async () => {
          const id = $('challenge').value.trim();
          clearTimeout(hide);
          if (!/^[0-9a-fA-F-]{36}$/.test(id)) { say($('codeOut'), 'Номер запроса — 36 символов с экрана телефона.', 'err'); return; }
          try {
            const r = await call('/api/v1/dev/sms/code', { challengeId: id });
            const c = document.createElement('code'); c.textContent = r.code; $('codeOut').replaceChildren(c); $('codeOut').className = '';
            hide = setTimeout(() => $('codeOut').replaceChildren(), 60000);
          } catch (e) { say($('codeOut'), e.message, 'err'); }
        });
        """;
}
