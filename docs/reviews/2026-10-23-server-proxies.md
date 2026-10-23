# Сервер: проверка доверенных прокси, 23 октября 2026

База интеграции: df572a59a4a2c9915eada48a10164680ce51ebf1, codex/android-device-proof.
Развитие исправления PR #2 (предыдущий HEAD 8cdf46f0f24ac8fa7d727928644008c65f44cf2d).

## Проблема и изменение

При пустом Auth:TrustedProxies очищались KnownProxies и KnownIPNetworks, но обработка X-Forwarded-For оставалась включена.
Прямой клиент мог менять учитываемый IP и обходить IP/сетевой слой SMS-лимитов. Лимиты номера и общий бюджет от этого не исчезали.

TrustedProxyPolicy теперь отключает forwarded headers, если явных прокси нет. Для настроенных адресов включается только
X-Forwarded-For, один переход. Прокси-адреса разбираются при регистрации сервисов: неверный адрес останавливает запуск,
а не вызывает ошибку на первом пользовательском запросе. Program использует эту же политику.

Добавлены восемь HTTP-проверок без БД через настоящий ASP.NET Core middleware и TestServer:
пустой список, неизвестный прокси, доверенный прокси, один переход в цепочке, отсутствие заголовка,
IPv4-mapped IPv6 peer и две ошибочные конфигурации (hostname/CIDR). Проверяется также отсутствие доверия к X-Forwarded-Proto.
TestServer не открывает TCP-сокет: исходный peer задаётся тестом до middleware.

Регрессия EmptyTrustedProxyListDoesNotAllowSpoofingSmsIpLimit сохранена: шесть номеров/поддельных forwarded IP,
один реальный peer и два API на общей PostgreSQL; ожидаются пять Fake SMS и шестой ответ 429. Она скомпилирована, но не выполнена здесь.

## Фактически выполнено

.NET SDK 10.0.402 загружен с официального Microsoft endpoint и проверен по SHA512 release metadata.
Restore с --locked-mode завершился успешно без изменения packages.lock.json. Release build прошёл с TreatWarningsAsErrors.

`dotnet test server/Messenger.Tests -c Release --no-restore --filter 'Category!=PostgreSQL'`:
24 passed, 0 failed, 0 skipped, включая 8 новых случаев. Это выбранные тесты без PostgreSQL, не весь серверный набор.

Отрицательный контроль: только в локальном временном исходнике восстановлено небезопасное включение forwarding при пустом списке.
Из шести HTTP-случаев ровно один ожидаемо упал: вместо реального 198.18.0.5 использовался подставленный 203.0.113.5.
Безопасный исходник восстановлен в finally; после этого все 24 теста снова прошли. В коммит попадает безопасная версия.

Результаты: ../test-results/2026-10-23/server-proxy-unit.trx, server-proxy-negative-control.trx и server-proxy.txt.

## Ограничения

PostgreSQL не запускался: доступная среда работает с UID 0 и запрещает переключение пользователя
(runuser: cannot set groups: Operation not permitted; unshare: uid_map: Operation not permitted).
Проверку PostgreSQL от root не обходили. Нужен полный прогон в CI или окружении с непривилегированным PostgreSQL.

Нет подтверждения серверных SQL-лимитов на двух реальных процессах/TCP, HTTPS/Beget, SMS.ru, двух телефонов и нагрузки.
Production guard сохранён. Android, основная ERP, схема БД и внешние API-контракты не изменялись. Развёртывание не выполнялось.
Оценка готовности всего продукта от этих выборочных тестов не повышается.

Справка Microsoft по доверенным прокси и middleware:
https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0

## Наблюдение GitHub Actions

Для коммита 833d53d1a9e5fd295ceced16994b0708b2b73087 server run 39952921850 / job 120411570337
и mobile run 39952921898 / job 120411570636 завершены с failure. У обоих runner_id=0 и steps=[].
Задания не дошли до выполнения restore/build/test. Это не подтверждение ошибки тестов приложения;
причина отсутствия runner не установлена. Полный серверный CI по этим запускам не выполнен.
