# Переезд закрытого стенда на Yandex Cloud

Решение пользователя 4 ноября 2026 года (`docs/DECISIONS.md`): стенд `chat.kniterp.online` переезжает с VPS Beget на VM в Yandex Cloud. Данные стенда тестовые и не переносятся. Сертификат и ключи выпускаются заново, поэтому pins меняются. Телефонам pins ещё не передавались.

Правила те же, что для Beget-стенда:
- команды выполняет пользователь, по одной;
- шаг засчитывается только по присланному выводу;
- токены, ключи и пароли в чат и git не попадают;
- Development guard, Fake SMS и TLS-проверки остаются как есть.

VPS Beget не выключается, пока новый стенд не принят. Вывод Beget из работы — отдельное решение пользователя.

Где что запускать:
- **Windows** — PowerShell на компьютере оператора, приглашение `PS C:\…>`.
- **VM** — после `ssh -i "$HOME\.ssh\os_vps" yc-user@IP_VM`. Команды на VM — через `sudo`, кроме входа.

Параметры по умолчанию:

| Параметр | Значение | Почему |
|---|---|---|
| Зона | `ru-central1-a` | Москва |
| Платформа | `standard-v3`, 2 vCPU, 100% доли ядра, 2 ГБ RAM | На Beget 1 ГБ RAM хватило только со swap; сборка .NET-образа требует памяти |
| Диск | 20 ГБ `network-ssd`, образ `ubuntu-2404-lts` | На Beget 8.6 ГБ оказалось впритык: образы, сборка, зашифрованные копии |
| IP | Статический публичный IPv4 | Он стоит в DNS и в allowlist у телефонов (WireGuard) |
| Снаружи | TCP 443 всем, TCP 22 только с IP оператора, UDP 51820 (WireGuard) | Как на Beget, плюс группа безопасности облака поверх ufw |

Стоимость пользователь смотрит в калькуляторе Yandex Cloud до шага Я-5: VM, диск и статический IP оплачиваются, пока существуют. Этот runbook платные сервисы (WAF, Smart Web Security, Managed PostgreSQL) не включает.

## Я-1. CLI (Windows)

```powershell
iex (New-Object System.Net.WebClient).DownloadString('https://storage.yandexcloud.net/yandexcloud-yc/install.ps1')
```

Затем открыть PowerShell заново:

```powershell
yc version
```

Ожидается номер версии.

## Я-2. Вход в облако (Windows)

```powershell
yc init
```

Мастер запросит OAuth-токен: ссылку на него он покажет сам. Токен вставляется только в окно `yc init`, в чат не присылается. Затем мастер спросит облако, каталог и зону по умолчанию: `ru-central1-a`.

Проверка без секретов:

```powershell
yc config list | Select-String -NotMatch token; yc resource-manager folder list
```

Ожидаются `cloud-id`, `folder-id`, `compute-default-zone: ru-central1-a` и список каталогов.

## Я-3. Сеть и группа безопасности (Windows)

`IP_ОПЕРАТОРА` — внешний IP компьютера оператора. Его показывает `curl.exe -s https://api.ipify.org`.

```powershell
yc vpc network create --name os-chat-net
```

```powershell
yc vpc subnet create --name os-chat-a --zone ru-central1-a --range 10.130.0.0/24 --network-name os-chat-net
```

```powershell
yc vpc security-group create --name os-chat-sg --network-name os-chat-net `
  --rule "description=https,direction=ingress,port=443,protocol=tcp,v4-cidrs=[0.0.0.0/0]" `
  --rule "description=ssh-operator,direction=ingress,port=22,protocol=tcp,v4-cidrs=[IP_ОПЕРАТОРА/32]" `
  --rule "description=wireguard,direction=ingress,port=51820,protocol=udp,v4-cidrs=[0.0.0.0/0]" `
  --rule "description=egress,direction=egress,protocol=any,v4-cidrs=[0.0.0.0/0]"
```

Ожидается описание группы с четырьмя правилами. Порты 80, 5080 и 5432 не открываются: Let's Encrypt выпускается через DNS-01, API и база слушают только loopback.

## Я-4. Статический IP (Windows)

```powershell
yc vpc address create --name os-chat-ip --external-ipv4 zone=ru-central1-a
```

Ожидается `external_ipv4_address: address: …` — это `IP_VM`.

## Я-5. VM (Windows, только после подтверждения стоимости)

Используется тот же SSH-ключ, что для Beget. Нужна его публичная часть `os_vps.pub`.

```powershell
yc compute instance create --name os-chat --zone ru-central1-a --platform standard-v3 `
  --cores 2 --core-fraction 100 --memory 2 `
  --create-boot-disk "image-folder-id=standard-images,image-family=ubuntu-2404-lts,size=20,type=network-ssd" `
  --network-interface "subnet-name=os-chat-a,nat-ip-version=ipv4,nat-address=IP_VM,security-group-ids=ID_ГРУППЫ" `
  --ssh-key "$HOME\.ssh\os_vps.pub"
```

`ID_ГРУППЫ` берётся из вывода Я-3 (`id:`) или `yc vpc security-group get os-chat-sg`. Ожидается `status: RUNNING`. Затем вход:

```powershell
ssh -i "$HOME\.ssh\os_vps" yc-user@IP_VM "lsb_release -ds; nproc; free -h | sed -n 2p; df -h / | tail -1"
```

## Я-6. Базовая защита VM

```bash
sudo bash -c 'install -m 0644 /dev/stdin /etc/ssh/sshd_config.d/00-os-closed.conf <<EOF
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
EOF
sshd -t && systemctl reload ssh && sshd -T | grep -E "^(passwordauthentication|kbdinteractiveauthentication|permitrootlogin|pubkeyauthentication) "'
```

```bash
sudo bash -c 'ufw default deny incoming && ufw default allow outgoing && ufw default deny routed && ufw allow 22/tcp && ufw allow 443/tcp && ufw --force enable && ufw status verbose'
```

UDP 51820 в ufw открывает `wireguard.sh` на шаге Я-11.

## Я-7. Пакеты и swap

```bash
sudo bash -c 'fallocate -l 1G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile && echo "/swapfile none swap sw 0 0" >> /etc/fstab && apt-get update -q && DEBIAN_FRONTEND=noninteractive apt-get install -y -q --no-install-recommends docker.io docker-compose-v2 docker-buildx nginx age git python3 certbot && systemctl disable --now nginx && docker --version && docker compose version && nginx -v && age --version && free -h | sed -n 3p'
```

## Я-8. Код и `.env`

```bash
sudo bash -c 'git clone --depth 1 --branch main https://github.com/basilsolanji-hash/Messenger-KnitERP.git /opt/os-communications && cd /opt/os-communications && git log -1 --format="%H %cs" && python3 scripts/init-dev.py && chmod 600 .env && stat -c "%a %U" .env'
```

Дописать несекретные ключи так, чтобы файл не выводился на экран. Peer-адрес Nginx снова определяется на месте, как в отчёте Beget: обычно это gateway compose-сети, например `172.18.0.1`.

```bash
sudo python3 - <<'PY'
p = '/opt/os-communications/.env'
lines = [l for l in open(p).read().splitlines() if not l.startswith(('AllowedHosts=', 'Auth__TrustedProxies__0='))]
lines += ['AllowedHosts=chat.kniterp.online']
open(p, 'w').write('\n'.join(lines) + '\n')
print('ENV_OK')
PY
```

Далее выполнить шаги 4–6 из `deploy/README.md` с выводом каждой команды:
1. Установить compose unit и проверить его через `systemd-analyze verify`.
2. Запустить его и проверить `/health/ready` через loopback.
3. Измерить peer IP, как в отчёте Beget.
4. Внести `Auth__TrustedProxies__0`.

## Я-9. DNS и сертификат

1. В панели DNS Beget у `chat.kniterp.online` заменить A-запись на `IP_VM`. TTL — минимальный. Проверка с Windows: `Resolve-DnsName chat.kniterp.online -Server ns1.beget.com`.
2. Новый сертификат через DNS-01 вручную, ключ RSA 2048 ради Android 8. Порядок тот же, что в `docs/reviews/2026-10-25-closed-stand.md`, раздел про сертификат: TXT `_acme-challenge` в панели Beget и проверка TXT на `ns1`/`ns2` до подтверждения.
3. Резервный ключ для второго pin создать на компьютере оператора, не на VM. На VM передаётся только `backup-public.pem`.

## Я-10. HTTPS и allowlist

1. `scripts/prepare-closed-https.py` с `--hostname chat.kniterp.online`, IP оператора и телефонов.
2. Проверить `nginx -t` через `systemd-run` с capability unit'а, по `docs/CLOSED_HTTPS.md`.
3. Установить HTTPS unit.
4. Снаружи: `https://chat.kniterp.online/api/v1/me` из разрешённой сети → `401`, из неразрешённой → `403`.

## Я-11. Остальное по `deploy/closed-stand/README.md`

Порядок шагов:
1. WireGuard.
2. Внешние проверки.
3. Лимит SMS и XFF.
4. Автозапуск и перезагрузка.
5. Backup → verify → restore.
6. Timer.
7. `server-endpoint.json` для телефонов.
8. Отзыв сессий.
9. Выключение операторских путей после теста.

Пути на VM те же (`/opt/os-communications`), команды — через `sudo`.

## Откат

Пока A-запись не переключена, стенд Beget продолжает работать как прежде. Если новый стенд не прошёл проверки:
1. Вернуть A-запись на `155.212.217.23`.
2. Телефонам вернуть прежний `server-endpoint.json`.

Удаление VM, диска и IP в Yandex Cloud — только по решению пользователя: `yc compute instance delete os-chat`, `yc vpc address delete os-chat-ip`.
