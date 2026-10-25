# Закрытый стенд на VPS — отчёт (черновик, стенд частично принят)

Задание: `docs/tasks/2026-10-25-01-closed-stand.md`. Основа: `main` @ `e1145f5b48b7aeed0d3cf9dbcfeb82b1af9fa1c6`.

Исполнитель работает в облачной сессии **без SSH-доступа к VPS**: исходящее TCP к `155.212.217.23:22` из сессии заблокировано (`SSH_TCP_FAIL`), доступы по решению пользователя не передаются в чат. Все команды на VPS выполнял пользователь по шагам из чата; ниже отмечено выполненным только то, что подтверждено выводом команды (снимком экрана). Готовность стенда к переписке **не заявляется**.

Версии на VPS: Ubuntu 26.04.1 LTS (ядро 7.0.0-38), Docker 29.1.3, Docker Compose 2.40.3, buildx 0.30.1, nginx 1.28.3, Python 3.14.4, OpenSSL 3.5.5, age 1.2.1, certbot 4.0.0, PostgreSQL образ `postgres:18`. Развёрнутый код сервера: `e1145f5b`.

## Статус

- [x] Взят актуальный `main`, SHA выше.
- [x] Прочитаны `deploy/README.md`, `docs/CLOSED_HTTPS.md`, `docs/DECISIONS.md`, шаблоны systemd, `init-dev.py`, `prepare-closed-https.py`, `server-backup.py` и серверный код для прокси, лимитов SMS и отзыва сессий.
- [x] Шаг 2 — инвентаризация VPS выполнена пользователем (вывод `inventory.txt`, сфотографирован с экрана), см. ниже.
- [x] Подготовка VPS: swap 1 ГБ, установка docker.io/compose/buildx/nginx/age, общий `nginx.service` отключён, перезагрузка на новое ядро.
- [x] Шаг 3 (частично): checkout `/opt/os-communications` @ `e1145f5b…`, `.env` через `init-dev.py` с правами `600 root`, игнорируется git. `AllowedHosts` ещё не задан — ждёт домена.
- [x] Шаг 6 (частично): compose unit установлен, `systemd-analyze verify` OK, запущен (не включён в автозапуск); `/health/ready` через loopback = ready; слушает только `127.0.0.1:5080`.
- [x] Шаг 5 (измерение): фактический peer, видимый API, = `::ffff:172.18.0.1` = gateway compose-сети `172.18.0.1`. В `.env` ещё не внесён.
- [x] SSH-ключ ed25519 создан на компьютере пользователя (Windows, с passphrase), публичная часть добавлена в `authorized_keys`; `ssh -i … -o PasswordAuthentication=no … "echo KEY_OK"` → `KEY_OK`.
- [x] SSH: вход по паролю отключён. `/etc/ssh/sshd_config.d/00-os-closed.conf` (`PasswordAuthentication no`, `KbdInteractiveAuthentication no`, `PermitRootLogin prohibit-password`); `sshd -t && systemctl reload ssh` → `SSHD_RELOADED`; `sshd -T`: `permitrootlogin prohibit-password`, `pubkeyauthentication yes`, `passwordauthentication no`, `kbdinteractiveauthentication no`. **Снаружи** с компьютера пользователя: `ssh -o PubkeyAuthentication=no -o PreferredAuthentications=password …` → `Permission denied (publickey)`; вход по ключу → `KEY_STILL_OK`. Принятый пользователем риск: один из паролей был случайно набран в shell и виден на присланном снимке; пользователь решил его не менять (значение нигде не записано).
- [x] DNS: `chat.kniterp.online` (DNS у Beget: ns1/ns2.beget.com/.pro). A → `155.212.217.23`, AAAA нет; на сервере `getent ahostsv4` и `resolvectl query` → `155.212.217.23`.
- [x] Сертификат публичной CA (шаг 4, часть 1): `certbot 4.0.0` (пакет Ubuntu), DNS-01 вручную через TXT `_acme-challenge.chat.kniterp.online` в панели Beget; ключ RSA 2048 (совместимость с Android 8), `--reuse-key`, без email. Первые две попытки отклонены CA (`NXDOMAIN` — подтверждение отправлено до появления TXT); третья — через `--manual-auth-hook` с ожиданием файла-сигнала после проверки TXT на `ns1.beget.com` и `ns2.beget.pro`. Результат: `subject=CN=chat.kniterp.online`, `issuer=C=US, O=Let's Encrypt, CN=YR1`, `notBefore=Oct 25 13:10:05 2026 GMT`, `notAfter=Jan 23 13:10:04 2027 GMT`, SAN `DNS:chat.kniterp.online`. Цепочка в `fullchain.pem`: `chat.kniterp.online ← YR1 ← Root YR ← ISRG Root X1` (ISRG Root X1 есть в системном хранилище Android 7.1.1+, на физическом Android 8 не проверено). `reuse_key = True`, `key_type = rsa`, `rsa_key_size = 2048`: при продлении leaf pin сохраняется. Продление ручное (DNS-01 через панель Beget) до 23.01.2027; автоматизации нет.
- [x] TLS-файлы: системный пользователь `os-closed-https` (uid 999, nologin); `/etc/os-communications-tls/` `0750 root:os-closed-https`; `current.key` `0640 root:os-closed-https`; резервный RSA-2048 ключ создан на сервере в `/root/backup-tls.key` (0600), публичный самоподписанный `backup-public.pem`. **Резервный приватный ключ ещё не перенесён офлайн и не удалён с сервера** — отложено пользователем.
- [x] `.env`: добавлены `AllowedHosts=chat.kniterp.online`, `Auth__TrustedProxies__0=172.18.0.1` (скриптом, без вывода секретов); `stat` → `600 root`; restart compose → `active`, `/health/ready` с `Host: chat.kniterp.online` → `{"status":"ready"}`, с `Host: localhost` → `400`.
- [x] `prepare-closed-https.py --hostname chat.kniterp.online … --tester 95.27.146.208/32 --operator 95.27.146.208/32 --bind 0.0.0.0 --output /etc/os-communications-https-v1` → Prepared. Pins: `sha256/PdblEU06…Qg8=` (current), `sha256/ToQqmPR4…Mg8k=` (backup) — совпали с независимым расчётом `openssl … | dgst -sha256`. Права: каталог 0750 и конфиги 0640 группы `os-closed-https`, `runtime/` 0700 владелец `os-closed-https`, JSON 0600 root. IP телефонов в allowlist ещё нет.
- [x] **Находка (документация):** `runuser -u os-closed-https -- nginx -t …` из `docs/CLOSED_HTTPS.md` даёт `syntax is ok`, но `bind() to 0.0.0.0:443 failed (13: Permission denied)` → `test failed`, потому что `nginx -t` открывает listen-сокеты. Корректная проверка — с той же capability, что у unit: `systemd-run --wait --pipe -p User=os-closed-https -p AmbientCapabilities=CAP_NET_BIND_SERVICE -p CapabilityBoundingSet=CAP_NET_BIND_SERVICE -p NoNewPrivileges=yes nginx -t …` → `syntax is ok`, `test is successful`. Исправлено в `docs/CLOSED_HTTPS.md`.
- [x] Firewall: `ufw allow 22/tcp`, `ufw allow 443/tcp`, default deny incoming / allow outgoing / deny routed, `ufw --force enable` → `Status: active`, разрешены только 22 и 443 (v4/v6).
- [x] HTTPS unit: установлен из шаблона, `systemd-analyze verify` без ошибок, запущен → `active / disabled`; `ss`: `0.0.0.0:443` nginx, `127.0.0.1:5080` docker-proxy. С самого сервера (IP вне allowlist) `https://chat.kniterp.online/health/ready` → `403`.
- [x] Снаружи (телефон пользователя, Chrome, Wi‑Fi с IP из allowlist, VPN выключен): `https://chat.kniterp.online/health/ready` → `{"status":"ready"}` без предупреждения о сертификате — цепочка Let's Encrypt принята браузером телефона (версия Android этого телефона не зафиксирована). С включённым на телефоне VPN страница не загрузилась (IP VPN вне allowlist) — ожидаемо.
- [x] Runbook и скрипты оставшихся шагов: `deploy/closed-stand/` (см. ниже). На VPS ещё не запускались.
- [ ] Шаг 5: отрицательная проверка поддельного `X-Forwarded-For` и лимита 5 SMS/10 мин — `sms-limit-probe.ps1` + `ip-counters.py`; **не выполнена**.
- [ ] Шаг 6: закрытость 80/5080/5432/8080 снаружи — `check-external.ps1`; **не проверена снаружи** (на сервере: firewall только 22/443, API слушает только loopback). Автозапуск unit'ов не включён, перезагрузка со стендом не проверена.
- [ ] Шаг 7: IP вне списка с мобильного интернета — пользователь сообщил «получилось», снимок `403` не прислан, поэтому не засчитано; операторские пути для тестировщика, неверный pin, HTTP — `check-external.ps1`, **не выполнено**.
- [ ] Шаг 8: backup/verify/restore — `backup-now.sh`, `restore-test.sh`, `enable-backup-timer.sh`; **не выполнено**, timer не включён.
- [ ] WireGuard для мобильного интернета (решение пользователя): `wireguard.sh`, **не выполнено**.
- [ ] Шаг 9: `server-endpoint.json` (`origin=https://chat.kniterp.online`, два pins) готов на VPS, **не передан**. IP второго телефона (другой Wi‑Fi) в allowlist не внесён.
- [ ] Шаг 10: отзыв сессий и выключение операторских путей — `revoke-sessions.sh`, `operator-endpoints.sh`; **не выполнено**.

## Выполнено на VPS после инвентаризации (выводы пользователя 25.10)

- Swap: `fallocate -l 1G /swapfile`, `mkswap`, `swapon`, запись в `/etc/fstab` → `Swap: 1.0Gi`; переживает перезагрузку.
- `apt-get install --no-install-recommends docker.io docker-compose-v2 docker-buildx nginx age`; `systemctl disable --now nginx`. Версии: Docker 29.1.3, Docker Compose 2.40.3, buildx 0.30.1, nginx 1.28.3 (Ubuntu), age 1.2.1. После установки снаружи слушает только 22, `docker: active`, `nginx: inactive/disabled`.
- Во время установки в фоне работал `unattended-upgrades` (~615 МБ кэша apt, держал lock); обновил ядро до 7.0.0-38. После `apt-get clean` и перезагрузки: swap активен, docker active, nginx inactive. Hostname после перезагрузки сменился (назначается хостингом) — на стенд не влияет.
- `git clone --depth 1 --branch main …/Messenger-KnitERP.git /opt/os-communications` → `commit e1145f5b48b7aeed0d3cf9dbcfeb82b1af9fa1c6`; `python3 scripts/init-dev.py` → `.env` `600 root`, `git status --ignored` = `!! .env`.
- `docker compose pull db` и `docker compose build api` прошли на 1 ГБ RAM + 1 ГБ swap: образы `kniterp-messenger-dev-api:latest` 383 МБ, `postgres:18` 650 МБ. Диск после очистки: 8.6G, занято 5.9G, свободно 2.8G.
- `install -m 0644 deploy/systemd/os-communications-compose.service.example /etc/systemd/system/os-communications-compose.service`; `systemd-analyze verify` → `VERIFY_OK` (предупреждения только о системных xfs_scrub unit); `systemctl start` → `active / disabled`; `api Up 127.0.0.1:5080->8080/tcp`, `db Up (healthy) 5432/tcp`; `curl -H 'Host: localhost' http://127.0.0.1:5080/health/ready` → `{"status":"ready"}` (первая попытка — reset, API ещё стартовал); `ss -tlnp` — только `127.0.0.1:5080` (docker-proxy), 5432 на хосте не слушает.
- Peer IP: `docker network inspect kniterp-messenger-dev_default` → `gateway=172.18.0.1 subnet=172.18.0.0/16`; удерживаемое TCP-соединение к `127.0.0.1:5080`, прочитанное из `/proc/net/tcp6` контейнера api → `PEER_SEEN_BY_API ::ffff:172.18.0.1`. Docker использует userland `docker-proxy`. Значение для `.env`: `Auth__TrustedProxies__0=172.18.0.1` (проверено только на сервере; внешняя отрицательная проверка XFF — после HTTPS).

## Инвентаризация VPS (шаг 2, вывод пользователя 25.10)

Факты из вывода команд, без изменений на сервере:

- Хостинг Beget, регион SPB. Ubuntu 26.04.1 LTS, ядро 7.0.0-31-generic.
- 2 vCPU; память 955Mi (available 768Mi); **swap 0**; диск `/dev/vda1` 8.6G, занято 2.9G, свободно 5.8G.
- Сеть: один публичный IPv4 `/32` на `eth0`, IPv6 только link-local (IPv6-подсеть не подключена).
- Работающие сервисы: только системные + `beget-agent`, `fail2ban`, `ssh`. **Чужих сайтов, Docker и Nginx нет.**
- TCP LISTEN: `22` (sshd), `127.0.0.53/54:53` (systemd-resolved). **443 и 80 свободны.** UDP: resolver и chrony только на loopback.
- Firewall: `ufw` inactive, iptables policy ACCEPT для INPUT/FORWARD/OUTPUT — фильтрации нет.
- SSH: port 22, `permitrootlogin yes`, `passwordauthentication yes`, `pubkeyauthentication yes`.
- Версии: Python 3.14.4, OpenSSL 3.5.5, git 2.53.0. **Не установлены:** docker (в репозитории `docker.io` 29.1.3), nginx, age.

Выводы для дальнейших шагов:

- Конфликта по 443 нет (п. 6 находок снят для этого VPS).
- 1 ГБ RAM без swap рискованно для сборки API образом `dotnet/sdk:10.0.402`: нужен swap-файл (бесплатно, 1 ГБ — диск ограничен).
- Диск: Docker engine + SDK/aspnet/postgres образы + слои сборки ≈ 3 ГБ из 5.8 ГБ; место под зашифрованные backup ограничено. Контролировать `df -h` после каждой установки.
- После установки пакета `nginx` глобальный `nginx.service` нужно сразу остановить и отключить: стенду нужен только бинарь для выделенного unit.
- Firewall отсутствует — закрыть до запуска HTTPS (шаг 6); вход по паролю для root отключить только после проверенного входа по ключу.

## Найдено при анализе (до VPS)

1. `scripts/init-dev.py` не записывает `AllowedHosts` и `Auth__TrustedProxies__0`, но по умолчанию включает `Dev__EnableOperatorEndpoints=true`. После генерации эти несекретные ключи нужно дописать в `.env`, не выводя файл. Сделано однострочным Python-правлением `.env` без вывода файла.
2. Peer IP Nginx, который видит API: host Nginx подключается к `127.0.0.1:5080`, затем трафик проходит через Docker-проброс в контейнер. Ожидается gateway compose-сети, например `172.x.0.1`, а не `127.0.0.1`. Kestrel видит адрес как `::ffff:172.x.0.1`. `ForwardedHeaders` сопоставляет его с IPv4 (тест `TrustedProxyTests` это покрывает). Адрес нужно определить на VPS фактически: через `/proc/net/tcp` в контейнере api, пока открыто соединение.
3. Остаточный риск: любой локальный процесс на VPS, который подключается к `127.0.0.1:5080`, приходит с того же доверенного gateway. Значит, он может подделать `X-Forwarded-For`. Снаружи это закрыто: Nginx перезаписывает заголовок через `$remote_addr`.
4. Отрицательная проверка лимита «5 SMS за 10 минут» требует валидного `SmsChallengeRequest`: P-256 SPKI `AuthKey`, `IdentityKey` из 33 байт с префиксом 0x05, разные телефоны (лимит 3 на номер за 15 минут). Проверка расходует квоту IP: 5 за 10 минут и 30 в сутки. Если телефоны тестировщиков выходят с того же NAT IP, проверку делать заранее или с другого разрешённого IP.
5. Операторского endpoint для отзыва сессий в API нет. Отзыв без изменения кода выполняется SQL в контейнере `db`: `refresh_families."RevokedAt"` для SMS-сессий или `devices."Active"=false` для устройства. API проверяет это на каждом запросе. SignalR закрывается примерно за 1 секунду через `QueueWorker.CloseRevoked`. Синтетические dev-сессии без `sid` отзываются только через `devices."Active"`.
6. Если порт 443 на VPS уже занят чужим сайтом, выделенный Nginx на `0.0.0.0:443` не запустится. Варианты (второй IP, другой порт, SNI-проброс через чужой Nginx) требуют **решения пользователя**: чужие сайты не трогаем. На этом VPS снят: 443 был свободен.
7. Debian/Ubuntu Nginx от непривилегированного пользователя выдаёт `[alert]` о `/var/log/nginx/error.log` до чтения конфигурации. Это не ошибка `nginx -t`. Учесть при чтении вывода.

## Решение пользователя 25.10: мобильный интернет через WireGuard

На вопрос «только Wi‑Fi или и мобильный интернет» пользователь ответил «все возможности». Это записано как выбор WireGuard на том же VPS (вариант без новых платных ресурсов). Следствие: снаружи открыт ещё `UDP 51820`, отступление от исходного «только 443 и 22». Скрипт `deploy/closed-stand/wireguard.sh` подготовлен, но на VPS не запускался, и у исполнителя он не выполнялся: в среде нет `wg`. Риск: блокировка WireGuard мобильными операторами РФ. Обходной вариант (AmneziaWG) требует отдельного решения. Запись в `docs/DECISIONS.md` вне области этого задания — её должен внести ведущий исполнитель.

## Скрипты `deploy/closed-stand/`

Runbook — `deploy/closed-stand/README.md`. Что проверено у исполнителя (без VPS):

- `bash -n` для всех `.sh`, `py_compile` для `ip-counters.py`.
- `revoke-sessions.sh` (`list`, `device`, `all`, отказ на некорректный id) и `ip-counters.py` прогнаны на локальном PostgreSQL 16 с минимальными таблицами `devices`/`refresh_families`/`auth_counters` (имена столбцов как в `MessengerDbModelSnapshot`) через shim `docker compose exec`. Формула ключа счётчика повторяет `AuthSecrets.CounterKey`, но сверена только с самой собой, не с работающим API.
- PowerShell-скрипты (`check-external.ps1`, `sms-limit-probe.ps1`, `move-backup-key.ps1`) **не запускались**: в среде исполнителя нет PowerShell. Поддержка `--pinnedpubkey` в `curl.exe` Windows (Schannel) ожидается, но не проверена.
- `set-allowlist.sh`, `backup-now.sh`, `restore-test.sh`, `enable-backup-timer.sh`, `operator-endpoints.sh` не запускались: нет Docker/systemd/VPS.

## Непроверенное и блокеры

- Исполнитель не имеет доступа к VPS; всё выполняет пользователь, каждый результат — по снимку вывода.
- **Резервный TLS-ключ всё ещё на сервере** (`/root/backup-tls.key`) — перенос отложен пользователем (`move-backup-key.ps1`).
- IP второго телефона в другой сети Wi‑Fi неизвестен. Домашние IP могут меняться: тогда нужно повторить `set-allowlist.sh`. Мобильный интернет не поддерживается (решение 25.10); VPN на телефоне делает стенд недоступным.
- Шаг 8 требует отдельного компьютера с Linux/WSL, Docker, age и Python; у пользователя Windows — WSL и Docker ещё не подготовлены.
- Физические телефоны (Android 8 и актуальный) со стендом не проверены; версия Android телефона, открывшего `/health/ready`, не зафиксирована.
- Сертификат продлевается вручную до 23.01.2027.
- Остаточный риск: локальные процессы VPS, обращающиеся к `127.0.0.1:5080`, приходят с доверенного `172.18.0.1` и могут подделать `X-Forwarded-For`; снаружи Nginx заменяет заголовок.
- Принятый пользователем риск: пароль, набранный в shell, виден на одном снимке; не сменён.
