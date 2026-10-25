# Runbook закрытого стенда (`chat.kniterp.online`)

Здесь собраны оставшиеся шаги задания `docs/tasks/2026-10-25-01-closed-stand.md` для VPS, где база уже развёрнута: compose, HTTPS-unit и firewall запущены. Ход установки и фактические выводы записаны в `docs/reviews/2026-10-25-closed-stand.md`. Каждый шаг заканчивается строкой `…_OK`/`PASS` или явной ошибкой. Шаг считается выполненным только после того, как его вывод получен.

Скрипты не выводят содержимое `.env`, токены и ключи. Где что запускать:

- **VPS:** после `ssh -i "$HOME\.ssh\os_vps" root@155.212.217.23`. Приглашение `root@…#`.
- **Windows:** PowerShell на компьютере оператора. Приглашение `PS C:\…>`.
- **WSL:** Linux на отдельном компьютере с офлайн-ключом age. На VPS этот шаг не выполнять.

## 0. Получить скрипты

VPS: скопировать только каталог скриптов, развёрнутый код сервера не трогать.

```bash
cd /opt/os-communications && git fetch -q --depth 1 origin claude/2026-10-25-closed-stand && git checkout FETCH_HEAD -- deploy/closed-stand && bash deploy/closed-stand/status.sh
```

Windows:

```powershell
mkdir $HOME\os-stand -Force | Out-Null; cd $HOME\os-stand
foreach ($f in 'check-external.ps1','sms-limit-probe.ps1','move-backup-key.ps1') { curl.exe -fsSLO "https://raw.githubusercontent.com/basilsolanji-hash/Messenger-KnitERP/claude/2026-10-25-closed-stand/deploy/closed-stand/$f" }
scp -i "$HOME\.ssh\os_vps" root@155.212.217.23:/etc/os-communications-https-v1/server-endpoint.json .
```

## 1. Убрать резервный TLS-ключ с сервера (Windows)

```powershell
powershell -ExecutionPolicy Bypass -File .\move-backup-key.ps1
```

Ожидается `BACKUP_KEY_MOVED`. Файл `C:\Users\…\.ssh\os-communications-backup-tls.key` перенести на отдельный носитель.

## 2. Список IP (VPS)

Внешний IP каждой сети Wi‑Fi показывает `https://api.ipify.org`, открытый в браузере телефона. Мобильный интернет и VPN должны быть выключены. Каждый запуск создаёт новый каталог `/etc/os-communications-https-vN`, проверяет `nginx -t` с capability unit'а и переключает unit через drop-in. Если unit не стартует, происходит откат.

```bash
bash /opt/os-communications/deploy/closed-stand/set-allowlist.sh --tester IP_ОПЕРАТОРА --tester IP_ТЕЛЕФОНА_2 --operator IP_ОПЕРАТОРА
```

Ожидается `ALLOWLIST_OK /etc/os-communications-https-vN`. Pins не меняются: сертификат и ключи те же.

## 3. Внешние проверки (Windows)

```powershell
powershell -ExecutionPolicy Bypass -File .\check-external.ps1 -Role operator
```

Ожидается `RESULT: ALL EXTERNAL CHECKS PASSED`. Проверяются:

- доступ по роли;
- закрытость операторских путей при разных написаниях;
- правильный и неверный pin;
- HTTP на портах 80 и 443;
- HTTPS по IP;
- открыты только TCP 22 и 443, а 80, 5080, 5432 и 8080 закрыты.

Как проверить, что операторские пути закрыты для тестировщика:

1. На VPS временно убрать оператора: `set-allowlist.sh --tester IP_ОПЕРАТОРА --tester IP_ТЕЛЕФОНА_2`.
2. На Windows выполнить `check-external.ps1 -Role tester`.
3. На VPS вернуть команду из шага 2.

Как проверить «IP вне списка»: на телефоне с мобильным интернетом без VPN открыть `https://chat.kniterp.online/health/ready`. Ожидается `403`.

## 4. Подделка X-Forwarded-For и лимит SMS

Windows. Скрипт расходует квоту SMS этого IP на 10 минут, поэтому телефоны за тем же IP регистрировать не раньше чем через 10 минут:

```powershell
powershell -ExecutionPolicy Bypass -File .\sms-limit-probe.ps1
```

Ожидается 5 × `200`, затем `429 RATE_LIMITED`, и `RESULT: PASS`.

VPS: какой адрес API фактически учёл в лимите.

```bash
python3 /opt/os-communications/deploy/closed-stand/ip-counters.py IP_ОПЕРАТОРА 172.18.0.1 198.51.100.1 198.51.100.6
```

Ожидается счётчик `sms-ip` = 5 у IP оператора и `0` у `172.18.0.1` и у поддельных адресов. Это подтверждает, что `Auth__TrustedProxies__0` указывает на фактический peer Nginx, а входящий заголовок Nginx заменяет.

## 5. Резервная копия и восстановление

1. **WSL, отдельный компьютер.** Установить зависимости: `sudo apt install -y age python3 docker.io git`. Клонировать репозиторий.
2. **WSL.** Создать ключ: `age-keygen -o ~/os-backup.identity`, затем `chmod 600 ~/os-backup.identity`. Строку `Public key: age1…` скопировать. Сам файл хранить офлайн и сделать вторую копию.
3. **VPS.** Запустить `bash /opt/os-communications/deploy/closed-stand/backup-now.sh age1…`. Скрипт печатает счётчики строк. Затем `Encrypted backup created: …`, и API снова `ready`.
4. **Windows.** Скопировать копию: `scp -r -i "$HOME\.ssh\os_vps" root@155.212.217.23:/var/backups/os-communications/ИМЯ .`
5. **WSL.** Запустить `deploy/closed-stand/restore-test.sh /mnt/c/Users/…/os-stand/ИМЯ ~/os-backup.identity`. Ожидаются те же счётчики и `RESTORE_TEST_OK`.
6. **VPS, только после успеха шага 5.** Запустить `bash /opt/os-communications/deploy/closed-stand/enable-backup-timer.sh`.

Запрещено: `docker compose down -v`, `prune`, восстановление в рабочую БД или рабочий volume.

## 6. Передача клиенту

Тестировщикам и Android-исполнителю передать только `server-endpoint.json` (`{origin, pins}`). Каталог конфигурации, TLS-ключи, `.env` и `Dev__ProvisioningKey` не передавать.

## 7. После приёмки: автозапуск

```bash
systemctl enable os-communications-compose.service os-communications-https.service && bash /opt/os-communications/deploy/closed-stand/status.sh
```

Проверка перезапуском: `reboot`, затем снова `status.sh`. Ожидаются `active / enabled` и `ready`.

## 8. Отзыв сессий и выключение операторских путей (VPS)

```bash
bash /opt/os-communications/deploy/closed-stand/revoke-sessions.sh list
bash /opt/os-communications/deploy/closed-stand/revoke-sessions.sh all              # все SMS-сессии
bash /opt/os-communications/deploy/closed-stand/revoke-sessions.sh device DEVICE_ID # одно устройство
bash /opt/os-communications/deploy/closed-stand/operator-endpoints.sh off           # после теста
```

Отзыв действует на следующем запросе: API сверяет `refresh_families` и `devices` при каждой проверке токена. SignalR закрывается примерно через 1 секунду. После `revoke-sessions.sh all` в `list` должно быть `live_sessions = 0`. После `operator-endpoints.sh off` выводится `… -> 404` и `OPERATOR_ENDPOINTS_OFF_OK`. Перезапуск API стирает Fake-коды SMS из памяти, поэтому выключать операторские пути во время регистрации телефона нельзя.
