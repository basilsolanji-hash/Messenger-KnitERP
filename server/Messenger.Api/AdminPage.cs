namespace Messenger.Api;

/// <summary>Static page of the administration panel (approved mockup docs/design/admin-panel.html). DOM via textContent only.</summary>
public static class AdminPage
{
    public const string Html = """
        <!doctype html>
        <html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>«Ось коммуникации» · администрирование</title>
        <style>
        :root{--gutter:max(16px,calc((100% - 1200px) / 2 + 16px));--ink:#14262b;--muted:#5b6f74;--line:#d5dfe1;--bg:#f4f7f7;--card:#fff;--teal:#0f6b6b;--warn:#a1260d;--ok:#11663c}
        *{box-sizing:border-box}body{margin:0;font:15px/1.45 system-ui,sans-serif;color:var(--ink);background:var(--bg)}
        header{display:flex;align-items:center;gap:12px;padding:12px var(--gutter);background:var(--card);border-bottom:1px solid var(--line);flex-wrap:wrap}
        header b{font-size:17px}header .who{margin-left:auto;display:flex;align-items:center;gap:8px;color:var(--muted);font-size:13px}
        nav{display:flex;gap:4px;padding:8px var(--gutter);overflow-x:auto;background:var(--card);border-bottom:1px solid var(--line)}
        nav button{border:0;background:none;padding:6px 12px;border-radius:6px;white-space:nowrap}nav button.on{background:var(--teal);color:#fff}
        main{padding:16px;max-width:1200px;margin:0 auto;display:grid;gap:16px}main>*{min-width:0}
        .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px}.card>*{margin:0}.card>*+*{margin-top:12px}.card>.wide+*{margin-top:16px}p{margin:0}
        h2{font-size:16px;margin:0}.muted{color:var(--muted);font-size:13px}.err{color:var(--warn)}.okt{color:var(--ok)}
        .kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px}.kpi>*+*{margin-top:4px}.kpi .v{font-size:22px;font-weight:600;white-space:nowrap}.kpi .l{color:var(--muted);font-size:13px}
        .row{display:flex;gap:8px;flex-wrap:wrap;align-items:center}.row input{flex:1;min-width:180px}.row select{flex:0 1 260px;min-width:160px}
        input,select{padding:8px 10px;font:inherit;border:1px solid var(--line);border-radius:6px;background:#fff}
        button{font:inherit;padding:7px 14px;border-radius:6px;border:1px solid var(--line);background:#fff;cursor:pointer}
        button.primary{background:var(--teal);color:#fff;border-color:var(--teal)}button.danger{color:var(--warn);border-color:#e3b5a9}
        button:disabled{opacity:.5;cursor:default}
        .wide{overflow-x:auto}table{border-collapse:collapse;width:100%;font-size:14px}th,td{text-align:left;padding:8px;border-bottom:1px solid #e6eced;white-space:nowrap}
        th{color:var(--muted);font-weight:500}th:first-child,td:first-child{padding-left:0}th:last-child,td:last-child{padding-right:0}td.id{font:12px ui-monospace,monospace}
        .tag{display:inline-block;padding:1px 8px;border-radius:10px;font-size:12px}.tag.ok{background:#e3f3ea;color:var(--ok)}.tag.stop{background:#fbe6e1;color:var(--warn)}
        .login{max-width:380px;margin:40px auto}.login label{display:block}.login input{display:block;width:100%;margin-top:4px}
        .secret{font:13px ui-monospace,monospace;word-break:break-all;background:#fff7e0;padding:8px;border-radius:6px}
        .hidden{display:none}
        </style></head><body>
        <section id="login" class="card login hidden">
          <h2>Вход в панель администратора</h2>
          <label>Логин<input id="l-login" autocomplete="username"></label>
          <label>Пароль<input id="l-password" type="password" autocomplete="current-password"></label>
          <label>Код из приложения-аутентификатора<input id="l-code" inputmode="numeric" autocomplete="one-time-code" maxlength="7"></label>
          <div class="row"><button id="l-go" class="primary">Войти</button><span id="l-msg" class="err"></span></div>
          <p class="muted">Доступ только с разрешённых адресов. После 5 ошибок вход блокируется на 15 минут.</p>
        </section>
        <div id="app" class="hidden">
          <header><b>«Ось коммуникации» · администрирование</b><span class="who"><span id="who"></span><button id="logout">Выйти</button></span></header>
          <nav id="nav"></nav>
          <main id="main"></main>
        </div>
        <script src="/admin/app.js"></script></body></html>
        """;

    public const string Script = """
        'use strict';
        const $ = id => document.getElementById(id);
        let me = null, view = 'accounts';
        const ROLES = { admin: 'администратор', support: 'поддержка', auditor: 'аудитор' };
        const TABS = [['accounts', 'Пользователи', ['admin', 'support']], ['metrics', 'Метрики', ['admin', 'support', 'auditor']],
          ['audit', 'Журнал', ['admin', 'auditor']], ['users', 'Учётные записи панели', ['admin']]];
        const ERRORS = { ADMIN_LOGIN_FAILED: 'Неверный логин, пароль или код.', ADMIN_LOCKED: 'Вход временно заблокирован. Повторите позже.',
          RATE_LIMITED: 'Слишком много запросов. Повторите позже.', ADMIN_FORBIDDEN: 'Недостаточно прав.', ADMIN_CSRF: 'Обновите страницу и повторите.',
          NOT_FOUND: 'Не найдено.', REASON_REQUIRED: 'Укажите причину: от 3 до 200 символов.', ALREADY_SUSPENDED: 'Аккаунт уже приостановлен.',
          NOT_SUSPENDED: 'Аккаунт не приостановлен.', QUERY_INVALID: 'Введите accountId (36 символов) или полный номер.', PHONE_INVALID: 'Номер не распознан.',
          LOGIN_INVALID: 'Логин: 3–64 строчные латинские буквы, цифры, точка, дефис, подчёркивание.', LOGIN_TAKEN: 'Такой логин уже есть.',
          ROLE_INVALID: 'Неизвестная роль.', SELF_CHANGE_FORBIDDEN: 'Свою учётную запись так изменить нельзя.', LAST_ADMIN: 'Нужен хотя бы один активный администратор.',
          PASSWORD_WEAK: 'Новый пароль: от 12 символов.', PASSWORD_INVALID: 'Текущий пароль неверен.' };
        function el(tag, attrs, ...children) {
          const e = document.createElement(tag);
          for (const [k, v] of Object.entries(attrs || {})) { if (k === 'on') for (const [ev, f] of Object.entries(v)) e.addEventListener(ev, f); else if (k === 'text') e.textContent = v; else e.setAttribute(k, v); }
          for (const c of children) e.append(c instanceof Node ? c : document.createTextNode(String(c)));
          return e;
        }
        async function api(method, path, body) {
          const headers = { 'Accept': 'application/json' };
          if (method !== 'GET') { headers['Content-Type'] = 'application/json'; if (me) headers['X-Admin-Csrf'] = me.csrf; }
          const r = await fetch('/api/v1/admin' + path, { method, headers, credentials: 'same-origin', cache: 'no-store', body: body ? JSON.stringify(body) : undefined });
          if (r.status === 401 && path !== '/login') { me = null; showLogin(); throw new Error('Сессия завершена. Войдите снова.'); }
          if (!r.ok) { let code = ''; try { code = (await r.json()).code || ''; } catch (_) {} throw new Error(ERRORS[code] || ('Ошибка сервера ' + r.status + (code ? ' (' + code + ')' : '') + '.')); }
          return r.status === 204 ? null : (r.headers.get('content-type') || '').includes('json') ? r.json() : r.text();
        }
        const date = v => v ? new Date(v).toLocaleString('ru-RU') : '—';
        function busy(button, f) { return async () => { button.disabled = true; try { await f(); } finally { button.disabled = false; } }; }
        function showLogin() { $('app').classList.add('hidden'); $('login').classList.remove('hidden'); $('l-login').focus(); }
        async function start() {
          try { me = await api('GET', '/me'); } catch (_) { return showLogin(); }
          $('login').classList.add('hidden'); $('app').classList.remove('hidden');
          $('who').textContent = me.login + ' · ' + ROLES[me.role];
          const tabs = TABS.filter(t => t[2].includes(me.role));
          if (!tabs.some(t => t[0] === view)) view = tabs[0][0];
          $('nav').replaceChildren(...tabs.map(([id, name]) => el('button', { class: id === view ? 'on' : '', on: { click: () => { view = id; start(); } } }, name)));
          render();
        }
        $('l-go').addEventListener('click', busy($('l-go'), async () => {
          $('l-msg').textContent = '';
          try {
            me = await api('POST', '/login', { login: $('l-login').value, password: $('l-password').value, code: $('l-code').value });
            $('l-password').value = ''; $('l-code').value = ''; start();
          } catch (e) { $('l-msg').textContent = e.message; $('l-password').value = ''; $('l-code').value = ''; }
        }));
        $('l-code').addEventListener('keydown', e => { if (e.key === 'Enter') $('l-go').click(); });
        $('logout').addEventListener('click', async () => { try { await api('POST', '/logout'); } catch (_) {} me = null; showLogin(); });

        async function render() {
          const main = $('main'); main.replaceChildren(el('p', { class: 'muted' }, 'Загрузка…'));
          try { main.replaceChildren(...await VIEWS[view]()); } catch (e) { main.replaceChildren(el('p', { class: 'err' }, e.message)); }
        }
        const VIEWS = {
          async metrics() {
            const m = await api('GET', '/metrics');
            const kpi = (v, l) => el('div', { class: 'card kpi' }, el('div', { class: 'v' }, v), el('div', { class: 'l' }, l));
            return [el('section', { class: 'kpis' }, kpi(m.accounts, 'аккаунтов'), kpi(m.suspended, 'приостановлено'), kpi(m.activeDevices, 'активных устройств'),
              kpi(m.liveSessions, 'живых сессий'), kpi(m.queuedEnvelopes, 'сообщений в очереди'),
              kpi((m.smsSpentKopecksToday / 100).toLocaleString('ru-RU') + ' / ' + (m.smsBudgetKopecks / 100).toLocaleString('ru-RU') + ' ₽', 'SMS за сутки / бюджет'),
              kpi(m.adminSessions, 'сессий панели')), el('p', { class: 'muted' }, 'Время сервера: ' + date(m.serverTime) + '. Ошибки API в этой версии не собираются.')];
          },
          async accounts() {
            const out = el('div', {}), msg = el('p', { class: 'muted' }, 'Поиск по номеру: не больше 20 в час, номер не сохраняется, факт поиска пишется в журнал.');
            const q = el('input', { placeholder: 'accountId или полный номер +7…', 'aria-label': 'Поиск' });
            const list = async (path, method, body) => {
              try { const r = await api(method, path, body); out.replaceChildren(table(r.items)); if (!r.items.length) out.replaceChildren(el('p', { class: 'muted' }, 'Ничего не найдено.')); }
              catch (e) { out.replaceChildren(el('p', { class: 'err' }, e.message)); }
            };
            const find = el('button', { class: 'primary' }, 'Найти');
            find.addEventListener('click', busy(find, async () => {
              const v = q.value.trim();
              if (/^[0-9a-fA-F-]{36}$/.test(v)) await list('/accounts?q=' + encodeURIComponent(v), 'GET');
              else if (/^\+?[0-9 ()-]{7,20}$/.test(v)) await list('/accounts/search-phone', 'POST', { phone: v });
              else out.replaceChildren(el('p', { class: 'err' }, ERRORS.QUERY_INVALID));
            }));
            const all = el('button', {}, 'Все аккаунты');
            all.addEventListener('click', busy(all, () => list('/accounts', 'GET')));
            await list('/accounts', 'GET');
            return [el('section', { class: 'card' }, el('h2', {}, 'Пользователи'), el('div', { class: 'row' }, q, find, all), msg, out)];
          },
          async audit() {
            const action = el('select', { 'aria-label': 'Действие' }, el('option', { value: '' }, 'Все действия'),
              ...['login', 'logout', 'suspend-account', 'resume-account', 'revoke-sessions-account', 'search-phone', 'create-user', 'change-role',
                'disable-user', 'enable-user', 'reset-credentials', 'password-change', 'export-audit', 'session-network-changed'].map(a => el('option', { value: a }, a)));
            const body = el('tbody'), more = el('button', {}, 'Ещё');
            let next = null;
            const load = async reset => {
              const r = await api('GET', '/audit?limit=50' + (action.value ? '&action=' + encodeURIComponent(action.value) : '') + (!reset && next ? '&before=' + next : ''));
              if (reset) body.replaceChildren();
              for (const x of r.items) body.append(el('tr', {}, el('td', {}, date(x.at)), el('td', {}, x.login || '—'), el('td', {}, x.remote), el('td', {}, x.action),
                el('td', { class: 'id' }, x.target), el('td', {}, x.detail), el('td', {}, el('span', { class: 'tag ' + (x.success ? 'ok' : 'stop') }, x.success ? 'успех' : 'отказ'))));
              next = r.next; more.disabled = !next;
            };
            action.addEventListener('change', () => load(true));
            more.addEventListener('click', busy(more, () => load(false)));
            const csv = el('a', { href: '#' }, 'Выгрузить CSV');
            csv.addEventListener('click', async e => {
              e.preventDefault();
              try {
                const text = await api('GET', '/audit.csv' + (action.value ? '?action=' + encodeURIComponent(action.value) : ''));
                const a = el('a', { href: URL.createObjectURL(new Blob([text], { type: 'text/csv' })), download: 'admin-audit.csv' }); a.click(); URL.revokeObjectURL(a.href);
              } catch (err) { alert(err.message); }
            });
            await load(true);
            return [el('section', { class: 'card' }, el('h2', {}, 'Журнал действий'), el('div', { class: 'row' }, action, csv),
              el('div', { class: 'wide' }, el('table', {}, el('thead', {}, el('tr', {}, ...['Время', 'Кто', 'Адрес', 'Действие', 'Объект', 'Подробности', 'Итог'].map(h => el('th', {}, h)))), body)), more)];
          },
          async users() {
            const users = await api('GET', '/users');
            const out = el('div');
            const login = el('input', { placeholder: 'логин, например ivanova', 'aria-label': 'Логин' });
            const role = el('select', { 'aria-label': 'Роль' }, ...Object.entries(ROLES).map(([k, v]) => el('option', { value: k }, v)));
            const add = el('button', { class: 'primary' }, 'Создать');
            add.addEventListener('click', busy(add, async () => {
              try { showSecret(out, await api('POST', '/users', { login: login.value, role: role.value })); login.value = ''; }
              catch (e) { out.replaceChildren(el('p', { class: 'err' }, e.message)); }
            }));
            const body = el('tbody');
            for (const u of users) {
              const act = el('td');
              if (u.login !== me.login) {
                const sel = el('select', { 'aria-label': 'Роль ' + u.login }, ...Object.entries(ROLES).map(([k, v]) => { const o = el('option', { value: k }, v); if (k === u.role) o.selected = true; return o; }));
                sel.addEventListener('change', () => run(() => api('POST', '/users/' + u.id + '/role', { role: sel.value })));
                const sw = el('button', { class: u.disabled ? '' : 'danger' }, u.disabled ? 'Включить' : 'Заблокировать');
                sw.addEventListener('click', () => confirm((u.disabled ? 'Включить ' : 'Заблокировать ') + u.login + '?') && run(() => api('POST', '/users/' + u.id + (u.disabled ? '/enable' : '/disable'))));
                const reset = el('button', {}, 'Новый пароль и код');
                reset.addEventListener('click', async () => { if (!confirm('Выдать ' + u.login + ' новый пароль и новый ключ кода? Старые перестанут действовать.')) return;
                  try { showSecret(out, await api('POST', '/users/' + u.id + '/reset-credentials')); } catch (e) { out.replaceChildren(el('p', { class: 'err' }, e.message)); } });
                act.append(sel, ' ', sw, ' ', reset);
              } else act.append(el('span', { class: 'muted' }, 'это вы'));
              body.append(el('tr', {}, el('td', {}, u.login), el('td', {}, ROLES[u.role]), el('td', {}, el('span', { class: 'tag ' + (u.disabled || u.locked ? 'stop' : 'ok') },
                u.disabled ? 'заблокирована' : u.locked ? 'вход временно закрыт' : 'активна')), el('td', {}, date(u.createdAt)), act));
            }
            const cur = el('input', { type: 'password', placeholder: 'текущий пароль', autocomplete: 'current-password' }), nxt = el('input', { type: 'password', placeholder: 'новый пароль, от 12 символов', autocomplete: 'new-password' });
            const change = el('button', {}, 'Сменить мой пароль'), cmsg = el('span', { class: 'muted' });
            change.addEventListener('click', busy(change, async () => { try { await api('POST', '/me/password', { current: cur.value, next: nxt.value }); cmsg.textContent = 'Пароль изменён, другие ваши сессии завершены.'; cmsg.className = 'okt'; }
              catch (e) { cmsg.textContent = e.message; cmsg.className = 'err'; } cur.value = ''; nxt.value = ''; }));
            return [el('section', { class: 'card' }, el('h2', {}, 'Учётные записи панели'), el('div', { class: 'row' }, login, role, add), out,
              el('div', { class: 'wide' }, el('table', {}, el('thead', {}, el('tr', {}, ...['Логин', 'Роль', 'Состояние', 'Создана', ''].map(h => el('th', {}, h)))), body))),
              el('section', { class: 'card' }, el('h2', {}, 'Мой пароль'), el('div', { class: 'row' }, cur, nxt, change), cmsg)];
          },
        };
        async function run(f) { try { await f(); } catch (e) { alert(e.message); } render(); }
        function showSecret(out, s) {
          out.replaceChildren(el('div', { class: 'card' }, el('p', {}, 'Учётная запись ' + s.login + '. Передайте владельцу лично; повторно эти данные не показываются.'),
            el('p', {}, 'Пароль:'), el('div', { class: 'secret' }, s.password), el('p', {}, 'Ключ для приложения-аутентификатора (otpauth):'), el('div', { class: 'secret' }, s.otpAuthUri),
            el('button', { on: { click: () => { out.replaceChildren(); render(); } } }, 'Скрыть')));
        }
        function table(items) {
          const body = el('tbody');
          for (const a of items) {
            const btn = el('button', {}, 'Открыть');
            btn.addEventListener('click', () => detail(a.accountId));
            body.append(el('tr', {}, el('td', { class: 'id' }, a.accountId), el('td', {}, date(a.createdAt)), el('td', {}, a.kind === 'sms' ? 'SMS' : 'синтетический'),
              el('td', {}, a.activeGeneration ? 'поколение ' + a.activeGeneration : 'нет активного'), el('td', {}, a.pinEnabled ? 'вкл' : 'выкл'),
              el('td', {}, el('span', { class: 'tag ' + (a.suspended ? 'stop' : 'ok') }, a.suspended ? 'приостановлен' : 'активен')), el('td', {}, btn)));
          }
          return el('div', { class: 'wide' }, el('table', {}, el('thead', {}, el('tr', {}, ...['Аккаунт', 'Регистрация', 'Вход', 'Устройство', 'PIN', 'Состояние', ''].map(h => el('th', {}, h)))), body));
        }
        async function detail(id) {
          const main = $('main');
          try {
            const d = await api('GET', '/accounts/' + id), a = d.account;
            const reason = el('input', { placeholder: 'Причина (обязательно, попадёт в журнал)', 'aria-label': 'Причина', maxlength: '200' }), msg = el('p');
            const act = (path, text) => { const b = el('button', { class: path === 'resume' ? 'primary' : 'danger' }, text);
              b.addEventListener('click', busy(b, async () => {
                if (!confirm(text + ' аккаунт ' + id + '?')) return;
                try { const r = await api('POST', '/accounts/' + id + '/' + path, { reason: reason.value }); msg.textContent = 'Готово.' + (r && r.sessions !== undefined && path !== 'resume' ? ' Отозвано сессий: ' + r.sessions + '.' : ''); msg.className = 'okt'; setTimeout(() => detail(id), 800); }
                catch (e) { msg.textContent = e.message; msg.className = 'err'; }
              })); return b; };
            const actions = me.role === 'admin' ? [el('div', { class: 'row' }, reason), el('div', { class: 'row' }, a.suspended ? act('resume', 'Возобновить') : act('suspend', 'Приостановить'), act('revoke-sessions', 'Отозвать сессии')), msg]
              : [el('p', { class: 'muted' }, 'Действия доступны роли «администратор».')];
            const devs = el('tbody');
            for (const x of d.devices) devs.append(el('tr', {}, el('td', { class: 'id' }, x.deviceId), el('td', {}, x.generation), el('td', {}, x.kind === 'sms' ? 'SMS' : 'синтетическое'),
              el('td', {}, x.liveSessions), el('td', {}, el('span', { class: 'tag ' + (x.active ? 'ok' : 'stop') }, x.active ? 'активно' : 'отключено'))));
            const back = el('button', {}, '← К списку'); back.addEventListener('click', render);
            main.replaceChildren(el('section', { class: 'card' }, back, el('h2', {}, 'Аккаунт ' + id),
              el('p', {}, 'Регистрация: ' + date(a.createdAt) + ' · PIN: ' + (a.pinEnabled ? 'вкл' : 'выкл') + ' · ' + (a.suspended ? 'приостановлен: ' + (a.suspendReason || '') : 'активен')),
              el('p', { class: 'muted' }, 'Сообщений ждут доставки: ' + d.queuedForAccount + ' · его заблокировали: ' + d.blockedByOthers + ' · он заблокировал: ' + d.blocksMade +
                '. Номер телефона сервер не хранит; текст переписки недоступен (сквозное шифрование).'),
              el('div', { class: 'wide' }, el('table', {}, el('thead', {}, el('tr', {}, ...['Устройство', 'Поколение', 'Тип', 'Сессии', 'Состояние'].map(h => el('th', {}, h)))), devs)), ...actions));
          } catch (e) { main.replaceChildren(el('p', { class: 'err' }, e.message)); }
        }
        start();
        """;
}
