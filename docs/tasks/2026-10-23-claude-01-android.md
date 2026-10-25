> Устарело 25 октября 2026 года: заменено заданием [2026-10-25-02-android-acceptance.md](2026-10-25-02-android-acceptance.md).

# Claude 1 — Android: приёмка пользовательского входа и чата

Репозиторий basilsolanji-hash/Messenger-KnitERP. Продукт «Ось коммуникации».
Создай ветку claude/android-closed-test-qa от актуальной codex/android-device-proof (PR #3), запиши SHA.
Старую claude/android-auth-v1 не продолжать: её UI уже интегрирован с исправлениями.
Цель к 26 октября 2026 года — закрытая текстовая переписка двух телефонов; срок не считать подтверждённым.
Основную ERP/систему «Ось» не менять. Внешний integration API пока проект.

## Область работы

mobile/app, instrumentation и docs/reviews/android-closed-test-qa.md. Сервер/инфраструктура — другой исполнитель.
Изменения общего mobile/network сначала обозначить в PR точным файлом/ошибкой и согласовать с интегратором.
Не переписывать криптографическое ядро и исправления refresh recovery.

## Приёмка

1. Один application owner/UserSession при навигации, recreation, двух ViewModel и process death.
2. Отдельный профиль каждой активации; журнал до ключей/HTTP. Подготовка продолжается с прежним deviceId,
   готовые отсутствующие/инвалидированные ключи не заменяются. История и outbox старого профиля сохраняются.
3. Восстановление экрана аккаунта. /me сверяет account/device/generation/Signal key/registration до bind,
   publish и readiness. Потеря ответа публикации повторяет прежние ключи. Другая origin/pins не получает старый challenge.
4. RU/BY/KZ/UZ: ASCII SMS/PIN, 60 секунд и Retry-After, неверный/истёкший/исчерпанный код,
   PIN_REQUIRED/PIN_LOCKED, установка/смена/отключение PIN, logout и перенос устройства.
5. Секреты не попадают в SavedState/Intent/clipboard/логи/снимки. Logout durable до HTTP, без refresh ради выхода.
6. Реальный Delivery этого же аккаунта: текст в обе стороны, офлайн-очередь, потеря send/ACK ответа,
   повтор без дублей, перезапуск, refresh и отзыв старого устройства. Recovery ambiguity завершает старую сеть без очистки данных.
7. Узкий экран и четыре языка. UUID собеседника и ручная синхронизация допустимы в закрытом тесте;
   контакты по номеру/автоматическое обновление/демо-ответы не выдавать за готовую сетевую переписку.

## Проверка и сдача

Запустить полный mobile с настоящим libsignal, без stub core:
:core:test :network:test :app:assembleDebug :app:assembleDebugAndroidTest.
Запустить instrumentation с реальными Keystore и SQLCipher на API26 и современной Android-версии.
Предыдущий эмуляторный прогон был прерван очисткой, его нельзя считать успешным.
Проверить UI против живого API. Fake SMS только в закрытом стенде: оператор получает код вне APK;
provisioning/admin secret в APK не добавлять. TLS trust/hostname/SPKI не отключать. Платные SMS по умолчанию не отправлять.

Draft PR на codex/android-device-proof. Отчёт: SHA, версии, команды, фактические результаты, ограничения.
Различать сборку, JVM, эмулятор, HTTP и два физических телефона. Не запускалось — так и написать с причиной.
Не сливать main, не публиковать production/Google Play. Синхронизировать публичный JSON стенда с серверным исполнителем.
