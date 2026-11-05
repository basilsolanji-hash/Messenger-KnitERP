# API панели администратора сервиса

Реализация: `server/Messenger.Api/Admin.cs`, страница `AdminPage.cs`, тесты `server/Messenger.Tests/AdminTests.cs`. Требования и модель угроз — `ADMIN_PANEL.md`. Макеты утверждены владельцем 5 ноября 2026 года.

## Включение

- Флаг `Admin__Enabled=true` в `.env`. Без флага все маршруты отвечают `404`. Значения, отличные от `true` и `false`, останавливают запуск.
- Nginx пропускает `/admin` и `/api/v1/admin` только с адресов из списка администраторов. Список задаётся флагом `--admin` в `scripts/prepare-closed-https.py` и `deploy/closed-stand/set-allowlist.sh`. Пустой список — отказ всем.
- **Первая учётная запись создаётся только на сервере.** Пароль и ключ TOTP команда печатает один раз:

  ```bash
  cd /opt/os-communications && docker compose run --rm --no-deps api admin create --login ivanova --role admin
  ```

  Строку `otpauth://…` добавить в приложение-аутентификатор: например, создать из неё QR-код на своём компьютере или ввести ключ вручную. Пароль хранить офлайн, затем очистить терминал.
- **Список учётных записей:** `docker compose run --rm --no-deps api admin list`.

## Аутентификация

**Вход:** `POST /api/v1/admin/login` с телом `{ "login", "password", "code" }`.
- При успехе — `200` с телом `{ login, role, csrf, expiresAt }` и cookie `__Host-os-admin` (`Secure; HttpOnly; SameSite=Strict; Path=/`).
- При любой ошибке — `401 ADMIN_LOGIN_FAILED`: неверный логин, пароль или код, повтор того же кода, учётная запись заблокирована.
- После 5 ошибок — `429 ADMIN_LOCKED` с `Retry-After` на 15 минут.
- Отдельный лимит: 30 попыток с одного IP за 15 минут.

**Сессия:**
- 30 минут без действий, 8 часов максимум.
- Привязана к сети клиента (/24 для IPv4). Запрос из другой сети завершает сессию.

**Изменяющие запросы** (всё, кроме `GET`) требуют заголовок `X-Admin-Csrf` со значением `csrf` из ответа входа или `/me`. Если есть заголовок `Origin`, он должен совпадать с `Host`. Иначе — `403 ADMIN_CSRF`.

**Роль ниже нужной** — `403 ADMIN_FORBIDDEN`.

## Маршруты

| Метод и путь | Роли | Ответ |
|---|---|---|
| `GET /api/v1/admin/me` | все | `{ login, role, csrf, expiresAt }` |
| `POST /api/v1/admin/logout` | все | `204`, сессия завершена |
| `POST /api/v1/admin/me/password` `{ current, next }` | все | `204`; остальные свои сессии завершены. `PASSWORD_WEAK` (короче 12), `PASSWORD_INVALID` |
| `GET /api/v1/admin/metrics` | все | `{ accounts, suspended, activeDevices, liveSessions, queuedEnvelopes, smsSpentKopecksToday, smsBudgetKopecks, adminSessions, serverTime }` |
| `GET /api/v1/admin/accounts?q=ACCOUNT_ID&after=ACCOUNT_ID` | admin, support | `{ items: [AccountRow], next }`, по 50 |
| `POST /api/v1/admin/accounts/search-phone` `{ phone }` | admin, support | `{ items }`. Не больше 20 в час на учётную запись. Номер не сохраняется; в журнал пишется только факт поиска и число найденных |
| `GET /api/v1/admin/accounts/{id}` | admin, support | `{ account, devices: [DeviceRow], queuedForAccount, blockedByOthers, blocksMade }` |
| `POST /api/v1/admin/accounts/{id}/suspend` `{ reason }` | admin | `{ sessions }`: отозваны все сессии. Вход по SMS — `403 ACCOUNT_SUSPENDED`, доставка ему — `403 DELIVERY_UNAVAILABLE`, его ключи не выдаются |
| `POST /api/v1/admin/accounts/{id}/resume` `{ reason }` | admin | `{ sessions: 0 }`. Телефон входит заново по SMS |
| `POST /api/v1/admin/accounts/{id}/revoke-sessions` `{ reason }` | admin | `{ sessions }` |
| `GET /api/v1/admin/audit?action=&before=&limit=` | admin, auditor | `{ items: [AuditRow], next }`, новые первыми, до 200 |
| `GET /api/v1/admin/audit.csv?action=` | admin, auditor | CSV (`;`), до 10 000 строк. Ячейки, начинающиеся с `= + - @`, экранируются; выгрузка пишется в журнал |
| `GET /api/v1/admin/users` | admin | `[{ id, login, role, disabled, locked, createdAt }]` |
| `POST /api/v1/admin/users` `{ login, role }` | admin | `{ id, login, password, otpAuthUri }` — показывается один раз |
| `POST /api/v1/admin/users/{id}/role` `{ role }` | admin | `204`; сессии пользователя завершены |
| `POST /api/v1/admin/users/{id}/disable` и `/enable` | admin | `204` |
| `POST /api/v1/admin/users/{id}/reset-credentials` | admin | новые пароль и ключ, один раз; старые не действуют |

**Роли:** `admin`, `support`, `auditor`.

**Защита от потери доступа:**
- Свою учётную запись через эти маршруты изменить нельзя: `409 SELF_CHANGE_FORBIDDEN`.
- Последнего активного администратора нельзя отключить или понизить: `409 LAST_ADMIN`.

**Причина действия:** 3–200 символов, без управляющих символов, иначе `400 REASON_REQUIRED`. Записывается в журнал и, при приостановке, в аккаунт.

**`AccountRow`:** `{ accountId, createdAt, devices, activeGeneration, pinEnabled, suspended, suspendReason, kind }`.
- `kind` — `sms` или `synthetic`.
- Номера нет: сервер хранит только HMAC номера.

**`DeviceRow`:** `{ deviceId, generation, active, kind, liveSessions }`.

**`AuditRow`:** `{ id, at, login, remote, action, target, detail, success }`.

## Журнал

Таблица `admin_audit` только дополняется: `UPDATE`, `DELETE` и `TRUNCATE` отклоняет триггер базы (миграция `AdminPanel`). Действия:
- вход и выход;
- смена сети сессии;
- приостановка, возобновление, отзыв сессий;
- поиск по номеру;
- создание и блокировка учётных записей, смена роли, новые учётные данные, смена пароля;
- выгрузка журнала.

Пароли, коды, токены и номера в журнал не попадают (проверено тестами).

Ограничение: срок хранения журнала (1 год) пока не автоматизирован. Удаление старых записей потребует отдельной процедуры с отключением триггера.

## Проверки

- `AdminTests`, 7 тестов на PostgreSQL:
  - без флага маршрутов нет;
  - пароль, TOTP, повтор кода, блокировка;
  - CSRF и Origin, простой сессии, смена сети, выход;
  - роли и защита последнего администратора;
  - приостановка: отказ `/me`, доставки и ключей, затем возобновление;
  - журнал неизменяем, CSV;
  - поиск по номеру с лимитом и без записи номера;
  - в ответах нет токенов, шифротекста и секретов.
- `AuthFlowTests.SuspendedAccountCannotSignInRefreshOrUseItsToken`.
- Nginx: `test_admin_panel_only_for_admin_addresses_including_normalization_variants` с настоящим Nginx.
- Ручная проверка в Chromium на 1280 и 390 px:
  - вход с неверным и верным кодом;
  - список и карточка аккаунта, приостановка с причиной;
  - метрики, журнал;
  - создание учётной записи, выход.
