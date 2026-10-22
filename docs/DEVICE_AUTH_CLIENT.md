# Подтверждение устройства: клиентская основа

Дополнение к `ANDROID_AUTH.md`, совместимое с серверным PR #1 на `971cce9c55ea0af682b65d05612497de0f46db1a`.

## Реализовано

- `mobile/network/.../DeviceProofPayload.java`: канонические ASCII-байты для activate и refresh, Unix-секунды, UUID в нижнем регистре, base64url без padding, SHA-256 публичного SPKI, завершающий LF. Нормализованный E.164 обязателен; форматирование номера и выбор страны остаются задачей экрана входа.
- `mobile/app/.../storage/DeviceAuthKey.kt`: отдельный неэкспортируемый ключ ECDSA P-256 в Android Keystore. Для каждой активации свой alias по deviceId. Создание не перезаписывает существующий ключ; открытие отсутствующего ключа завершается ошибкой. Ключи Signal не используются для подписи.
- `DeviceAuthKeyTest`: инструментальная проверка повторного открытия, неэкспортируемого private key, подписи DER, отказа при изменении payload, повторном создании и потере ключа.
- `scripts/check-device-proof.py`: компилирует настоящий Java-класс без Gradle, использует опубликованный серверный fixture, проверяет точные байты и digest, подпись OpenSSL в JCA, подпись JCA в DER, refresh, привязку полей и отклонение некорректных входов. Скрипт добавлен в mobile workflow.

## Что действительно проверено

Команда: `python3 scripts/check-device-proof.py`.
Среда: OpenJDK 17.0.20, Python 3; компиляция через `java com.sun.tools.javac.Main --release 17`.
Результат: `Device proof JVM contract: 33 checks passed`.

Это совместимость сериализации и криптографических форматов на JVM с опубликованным серверным вектором. Live HTTP-вход и новая серверная проверка .NET здесь не запускались. Не запущены сборка Android, инструментальный Keystore-тест, тесты транспорта и обмен двух телефонов. Новый APK в этой итерации не собран.

## Подключение к потоку входа — следующий этап

1. Создать новую активацию с deviceId, Signal identity и `DeviceAuthKey.create(deviceId)`; сохранить публичные параметры и состояние до HTTP-запроса в зашифрованном хранилище.
2. В SMS challenge передать `publicKeySpki()` в base64. Для verify получить байты через `DeviceProofPayload.activation(...)`, подписать `DeviceAuthKey.open(deviceId).sign(payload)` и отправить DER в base64.
3. Для refresh использовать `DeviceProofPayload.refresh(...)` с accountId и UUID токена из `rt1.<id>.<secret>`, затем тот же Keystore-ключ.
4. При потере ключа остановить auth, без тихой генерации замены и без очистки локальной истории.

HTTP-клиент SMS/PIN/refresh, хранилище refresh, single-flight обновление и экраны входа пока не подключены. Существующая сборка 0.2 использует операторскую тестовую сессию.
