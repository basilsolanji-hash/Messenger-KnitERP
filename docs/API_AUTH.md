# Auth API v1 — контракт

Все значения в примерах синтетические. JSON в camelCase, `byte[]` передаются стандартным base64, время — ISO 8601 UTC. Лишние поля запроса отклоняются (`400 INVALID_REQUEST`). Ответы `/api/v1/auth/*` имеют `Cache-Control: no-store`. Токены передаются только в теле и в `Authorization: Bearer`, никогда в URL.

Ошибки — Problem Details:

```json
{ "status": 400, "title": "CODE_INVALID", "code": "CODE_INVALID", "traceId": "00-…",
  "attemptsRemaining": 4, "retryAfterSeconds": 42 }
```

`attemptsRemaining` и `retryAfterSeconds` есть только там, где они осмысленны. При `retryAfterSeconds` также ставится заголовок `Retry-After`. Клиент принимает решения по `code`, а не по тексту.

## 1. Запрос SMS

`POST /api/v1/auth/sms/challenges` — без авторизации.

```json
{
  "requestId": "7b0e7c55-2d3a-4b5e-9a51-0c9a3f6e1d20",
  "phone": "+79001234567",
  "deviceId": "0b6f3c2e-91d4-4f7a-8e25-6c3d9a1b7e40",
  "registrationId": 4242,
  "identityKey": "BRERERERERERERERERERERERERERERERERERERERERER",
  "authKey": "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE…"
}
```

- `requestId` — ключ идемпотентности, новый UUID на каждое намерение отправить SMS. При повторе после потери ответа клиент шлёт **тот же** `requestId` и тело.
- `phone` — международный формат с `+`, пробелы и скобки допустимы. Сервер нормализует номер в E.164.
- `identityKey` — сериализованный публичный identity key libsignal (33 байта, первый `0x05`).
- `authKey` — X.509 SubjectPublicKeyInfo DER ключа P-256 из Android Keystore.

`200 OK`:

```json
{ "challengeId": "3f0c1a52-7d4e-4b8a-9c61-2e5f8a9b0c1d", "nonce": "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
  "expiresAt": "2026-10-22T10:20:00Z", "resendAfter": "2026-10-22T10:16:00Z", "codeLength": 6, "maxAttempts": 5 }
```

Ответ одинаков для нового и уже зарегистрированного номера. Повтор с тем же `requestId` и телом возвращает тот же ответ без новой SMS.

| Статус | code | Значение |
| --- | --- | --- |
| 400 | `PHONE_INVALID`, `PHONE_NOT_MOBILE`, `PHONE_REGION_NOT_ALLOWED` | Номер неверен, не мобильный или страна не разрешена |
| 400 | `INVALID_IDENTITY_KEY`, `INVALID_AUTH_KEY`, `INVALID_REQUEST` | Неверные ключи или поля |
| 409 | `IDEMPOTENCY_CONFLICT` | Тот же `requestId` с другим телом |
| 409 | `DEVICE_ALREADY_EXISTS` | deviceId уже зарегистрирован: нужен новый |
| 429 | `RESEND_TOO_EARLY` | С прошлой SMS на номер не прошло 60 с |
| 429 | `RATE_LIMITED` | Лимит IP/сети/номера/устройства |
| 429 | `SMS_UNAVAILABLE` | Исчерпан глобальный часовой лимит или дневной бюджет |
| 503 | `SMS_BUSY` | Слишком много одновременных отправок, повторить позже с тем же `requestId` |
| 502 | `SMS_NOT_SENT` | Провайдер однозначно отказал. Новая попытка — с новым `requestId` |

Лимиты по умолчанию (`appsettings.json` → `Auth:Sms:Limits`): IP 5 за 10 мин и 30 в сутки; сеть (/24 или /48) 20 за 10 мин; номер 3 за 15 мин и 10 в сутки; deviceId 5 в сутки; глобально 300 в час; бюджет 100 000 коп. в сутки; не больше 10 одновременных отправок. Окна фиксированные, счётчики хранятся в PostgreSQL.

## 2. Проверка кода

`POST /api/v1/auth/sms/verify`

```json
{ "challengeId": "3f0c1a52-…", "phone": "+79001234567", "code": "123456", "signature": "MEUCIQ…" }
```

`signature` — подпись payload с `op:activate` (см. [ANDROID_AUTH.md](ANDROID_AUTH.md)).

`200 OK`, вход выполнен:

```json
{ "status": "ACTIVATED", "recoveryAvailable": false, "session": {
  "accountId": "…", "deviceId": "0b6f3c2e-…", "generation": 1,
  "accessToken": "eyJ…", "expiresAt": "2026-10-22T10:30:00Z",
  "refreshToken": "rt1.6a1f…-….AbCd…", "refreshExpiresAt": "2026-11-21T10:15:00Z", "newAccount": true } }
```

`200 OK`, нужен PIN: `{ "status": "PIN_REQUIRED", "session": null, "recoveryAvailable": false }`. Код уже потреблён, challenge ждёт PIN до своего `expiresAt`.

| Статус | code | Значение | Тратит попытку |
| --- | --- | --- | --- |
| 400 | `CODE_INVALID` | Неверный код | да |
| 400 | `CHALLENGE_MISMATCH` | Номер не совпадает с challenge | да |
| 403 | `DEVICE_PROOF_INVALID` | Подпись не сходится с ключом и полями challenge | да |
| 403 | `CHALLENGE_LOCKED` | 5 неудач, нужен новый challenge | — |
| 404 | `CHALLENGE_NOT_FOUND` | Нет такого challenge или он для другой операции | — |
| 409 | `CHALLENGE_USED`, `CHALLENGE_SUPERSEDED`, `CHALLENGE_NOT_SENT` | Уже использован, заменён новым, не отправлен | — |
| 409 | `DEVICE_ALREADY_EXISTS`, `CONCURRENT_ACTIVATION` | Гонка активаций, начать заново | — |
| 410 | `CHALLENGE_EXPIRED` | Прошло 5 минут | — |

## 3. PIN при переносе

`POST /api/v1/auth/pin/verify`

```json
{ "challengeId": "3f0c1a52-…", "phone": "+79001234567", "pin": "482913", "signature": "MEUCIQ…" }
```

Та же подпись, что для `/sms/verify` (тот же payload). Успех — как `ACTIVATED` выше, с `newAccount: false` и `generation` на 1 больше. Старое устройство в той же транзакции отключено.

| Статус | code | Значение |
| --- | --- | --- |
| 403 | `PIN_INVALID` | Неверный PIN, `attemptsRemaining` до блокировки. Устройство и generation не меняются |
| 429 | `PIN_LOCKED` | Блок: 15 мин после 5 ошибок, далее удвоение до 24 ч. Новая SMS не помогает |
| 409 | `CODE_REQUIRED` | Сначала `/sms/verify` |
| 403 / 400 | `DEVICE_PROOF_INVALID`, `CHALLENGE_MISMATCH` | Как выше |

`POST /api/v1/auth/recovery` → всегда `501 RECOVERY_NOT_AVAILABLE`.

## 4. Refresh

Два шага. Перед каждым refresh нужен свежий challenge.

`POST /api/v1/auth/refresh/challenges` `{ "deviceId": "0b6f3c2e-…" }` → `200` `{ "challengeId", "nonce", "expiresAt" (+2 мин), "resendAfter": null, "codeLength": 0, "maxAttempts": 1 }`. Отключённое устройство получает `401 SESSION_REVOKED`. Лимиты: 120 за 10 мин на IP, 20 за 10 мин на устройство.

`POST /api/v1/auth/refresh`

```json
{ "refreshToken": "rt1.6a1f…", "challengeId": "…", "signature": "MEUCIQ…" }
```

Подпись — payload с `op:refresh`, `account:<accountId>`, `token:<id из refreshToken>`. Ответ — объект `session` (как выше, `newAccount: false`). Старый refresh token после этого потреблён.

| Статус | code | Значение |
| --- | --- | --- |
| 401 | `REFRESH_INVALID` | Token не распознан |
| 401 | `REFRESH_REUSED` | Повтор уже использованного token: вся семья отозвана, нужен новый вход по SMS |
| 401 | `SESSION_REVOKED` | Logout, перенос на другой телефон или отзыв устройства |
| 401 | `SESSION_EXPIRED` | 30 дней без refresh или 90 дней с входа |
| 403 | `DEVICE_PROOF_INVALID` | Подпись неверна. Challenge сгорает, token не тратится |
| 403 | `CHALLENGE_LOCKED`, 409 `CHALLENGE_USED`, 410 `CHALLENGE_EXPIRED`, 404 `CHALLENGE_NOT_FOUND`, 400 `CHALLENGE_MISMATCH` | Взять новый challenge |

**Потерянный ответ.** Если ответ refresh не дошёл, клиент повторяет **байт в байт тот же запрос** в течение 2 минут и получает новую пару. Новый challenge с тем же старым token считается повторным использованием, и семья отзывается. Параллельные refresh из одного приложения запрещены.

## 5. Авторизованные операции

Нужен `Authorization: Bearer <accessToken>`. Токены dev-стенда без `sid` получают `403 SESSION_NOT_ELIGIBLE`.

| Запрос | Тело | Успех | Ошибки |
| --- | --- | --- | --- |
| `POST /api/v1/auth/logout` | `{}` | 204 | — |
| `PUT /api/v1/account/pin` | `{ "pin": "482913", "currentPin": null }` | 204 | 400 `PIN_FORMAT`, `PIN_TOO_SIMPLE`; 403 `PIN_REQUIRED` (PIN уже задан), `PIN_INVALID`, `REAUTH_REQUIRED` (первая установка позже 10 мин после входа); 429 `PIN_LOCKED` |
| `POST /api/v1/account/pin/disable` | `{ "currentPin": "482913" }` | 204 | 409 `PIN_NOT_SET`; 403 `PIN_INVALID`; 429 `PIN_LOCKED` |

PIN — ровно 6 ASCII-цифр, без одинаковых цифр подряд и без возрастающих/убывающих последовательностей.

## Только для синтетического стенда

`POST /api/v1/dev/sms/code` `{ "challengeId": "…" }` с заголовком `X-Dev-Provisioning-Token` → `{ "code": "123456" }`. Endpoint возвращает код, «отправленный» fake-провайдером в память этого экземпляра. Он регистрируется **только** при `Auth:Sms:Provider=Fake`, а fake-провайдер разрешён только в Development. С SMS.ru этого маршрута нет (404). Нужен для автоматических сквозных тестов клиента. Номер и код в URL не попадают.

## Access JWT

HS256, `iss=kniterp-messenger-dev`, `aud=kniterp-messenger-android`, `exp` ровно 15 минут после выдачи (секундная точность, `expiresAt` в ответе — это и есть `exp`). Claims: `sub` (accountId), `device`, `generation`, `sid` (семья refresh), `jti`. На каждом запросе и при подключении SignalR сервер проверяет активное устройство, совпадение generation и живую семью `sid`.

## Конфигурация сервера

Секреты — только из окружения/хранилища секретов, шаблон в [`.env.example`](../.env.example): `Auth__SigningKey`, `Auth__PhoneIndexKey`, `Auth__SmsCodeKey`, `Auth__PinPepper` (base64, ≥ 32 случайных байт, все разные), `Auth__Sms__Provider` (`Fake` только в Development или `SmsRu`), `Auth__Sms__SmsRu__ApiKey`, `Auth__TrustedProxies__N`. Без них сервер не стартует. Смена `Auth__PhoneIndexKey` делает существующие аккаунты недоступными по номеру. Смена `Auth__PinPepper` делает недействительными все PIN.
