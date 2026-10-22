# Вход knitERP Messenger: контракт для Android

**Сделано на сервере (ветка `claude/auth-v1`):** все endpoint из [API_AUTH.md](API_AUTH.md), проверка подписи устройства, PIN, refresh, перенос.
**Не сделано в Android:** экраны входа и PIN, ключ Keystore для подписи, хранение refresh token, клиент refresh, обработка смены устройства в UI. Текущая сборка 0.2 по-прежнему работает только с операторской сессией через dev-endpoint.

## 0. Ключи устройства

При первой установке, до запроса SMS:

1. Signal: `IdentityKeyPair.generate()`, `registrationId` 1–16383 — как сейчас в `mobile/core`. Приватные ключи остаются в SQLCipher.
2. Ключ подтверждения владения — отдельный, в Android Keystore:

```kotlin
val spec = KeyGenParameterSpec.Builder("kniterp-device-auth-v1", KeyProperties.PURPOSE_SIGN)
    .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
    .setDigests(KeyProperties.DIGEST_SHA256)
    .build() // Не экспортируемый. StrongBox — по желанию: если недоступен, без него.
val kpg = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
kpg.initialize(spec)
val authKey: ByteArray = kpg.generateKeyPair().public.encoded // X.509 SPKI DER → поле authKey
```

Подпись: `Signature.getInstance("SHA256withECDSA")` с приватным ключом из Keystore. Результат в DER — именно этот формат ждёт сервер, raw r‖s не принимается. Identity key Signal (X25519/XEdDSA) для этого **не** использовать.

3. `deviceId` = случайный UUID. Каждая новая активация (новый телефон, переустановка, вход после logout) — новый `deviceId`, новый Keystore-ключ и новые ключи Signal.

## 1. Подписываемый payload

ASCII, строки через `\n`, в конце тоже `\n`, порядок фиксирован. Неприменимое поле — `-`. `b64url` — base64url без `=`.

```
kniterp-device-proof-v1
op:<activate|refresh>
challenge:<challengeId, UUID в нижнем регистре с дефисами>
nonce:<b64url(nonce)>
expires:<expiresAt в Unix-секундах>
phone:<E.164 | ->
account:<accountId | ->
token:<id refresh token | ->
device:<deviceId>
registration:<registrationId десятичным числом>
identity:<b64url(33 байта identity key)>
authkey:<b64url(SHA-256(authKey SPKI DER))>
```

- Вход (`op:activate`): `phone` — **нормализованный E.164** (тот же, что вы отправите в verify; формируйте его из выбора страны и номера, без пробелов), `account:-`, `token:-`.
- Refresh (`op:refresh`): `phone:-`, `account:<accountId>`, `token:<вторая часть refreshToken: rt1.<id>.<secret>>`.
- `expires` и `nonce` берутся из ответа challenge как есть.

Тестовый вектор с синтетическим ключом: [`fixtures/device-proof-v1.json`](fixtures/device-proof-v1.json). Подпись в нём сделана OpenSSL, сервер проверяет её в `DeviceProofMatchesPublishedAndroidFixture`. ECDSA рандомизирована: ваша подпись того же payload будет другой, но должна проходить проверку тем же публичным ключом. Для unit-теста клиента сравнивайте байты payload и SHA-256 из fixture.

## 2. Последовательность входа

```
Телефон                                       Сервер
  ключи (раздел 0)
  POST /auth/sms/challenges  {requestId,…} →  резерв + SMS
                             ← challengeId, nonce, expiresAt, resendAfter
  пользователь вводит код
  POST /auth/sms/verify {code, signature} →
       ← ACTIVATED + session           (новый номер или аккаунт без PIN)
       ← PIN_REQUIRED                  (аккаунт с PIN)
  POST /auth/pin/verify {pin, тот же signature} → ACTIVATED + session
  POST /keys/bundles (новые prekeys)
```

- Сохраните `requestId` до ответа. При таймауте повторяйте с **тем же** `requestId` и телом: новая SMS не уйдёт.
- Кнопка «Отправить снова» доступна после `resendAfter`. Это новый `requestId` (и тот же deviceId/ключи), прежний код перестаёт действовать.
- `attemptsRemaining` показывайте пользователю. После `CHALLENGE_LOCKED`/`CHALLENGE_EXPIRED` нужен новый challenge.
- `PIN_LOCKED`: показать время из `retryAfterSeconds`, ввод заблокировать. Обходного пути (поддержка, SMS, recovery) нет — `recoveryAvailable: false`, так и написать.
- После `ACTIVATED` с `newAccount: false` это перенос: прежняя история на сервере не хранится и не восстанавливается, у собеседников сменится код безопасности.

## 3. Хранение сессии

- `refreshToken` хранить только в SQLCipher (или зашифровать ключом Keystore), не в SharedPreferences/логах/бэкапе. `accessToken` — в памяти.
- Обновлять access заранее (за ~60 с до `expiresAt`) или по `401`. **Один refresh одновременно на всё приложение (mutex).**
- Refresh: `POST /auth/refresh/challenges {deviceId}` → подпись `op:refresh` → `POST /auth/refresh`. Новую пару атомарно сохранить **до** использования.
- Если ответ refresh потерян (таймаут, обрыв): повторить тот же запрос (тот же challengeId и signature) один раз в пределах 2 минут. Не брать новый challenge со старым token — сервер расценит это как кражу и отзовёт сессию.
- `REFRESH_REUSED`, `SESSION_REVOKED`, `SESSION_EXPIRED` → сессия кончилась: остановить сеть, локальную историю и outbox **не удалять**, предложить вход заново. `SESSION_REVOKED` после переноса означает, что номер активирован на другом телефоне.

## 4. PIN

- Предложить сразу после первой активации: первая установка разрешена только 10 минут после входа (`REAUTH_REQUIRED` позже).
- `PUT /account/pin {pin, currentPin: null}`; смена — с `currentPin`; отключение — `POST /account/pin/disable {currentPin}`.
- Предупредить: PIN нельзя восстановить; без него перенос номера на другой телефон невозможен.

## 5. Logout и новый телефон

- `POST /auth/logout` → удалить refresh token. Устройство на сервере остаётся, но следующий вход создаёт новое устройство (новые ключи), и недоставленные ему сообщения стираются.
- Новый телефон — тот же поток входа. Старый телефон получит `401` на ближайшем запросе, SignalR будет закрыт; приложение показывает «аккаунт используется на другом устройстве».
- У собеседников: `claim` вернёт новый `identityKey` и `generation`, отправка на старое устройство — `409 RECIPIENT_DEVICE_CHANGED`. Клиент обязан снять отметку проверки, остановить отправку и показать смену кода безопасности. Автоматического доверия нет (это уже поведение `mobile/core`).

## 6. Тексты ошибок

Все `code` из [API_AUTH.md](API_AUTH.md) нужно добавить в ресурсы ru/be/kk/uz. Не показывать пользователю `traceId` как основной текст. Сервер не различает «номер зарегистрирован / не зарегистрирован» до ввода кода — UI тоже не должен этого обещать.
