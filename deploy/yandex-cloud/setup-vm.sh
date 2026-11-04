#!/usr/bin/env bash
# One-shot base setup of the new Yandex Cloud VM (README steps Я-6..Я-8): SSH by key only, ufw, swap, packages,
# checkout, .env, compose unit, health and the trusted proxy address. Prints no secrets; never prints .env.
# Run on the VM (OS_BRANCH: the branch to deploy, main by default):
#   curl -fsSL https://raw.githubusercontent.com/basilsolanji-hash/Messenger-KnitERP/$OS_BRANCH/deploy/yandex-cloud/setup-vm.sh -o setup-vm.sh && sudo OS_BRANCH=$OS_BRANCH bash setup-vm.sh
# Safe to re-run: existing swap, checkout and .env are kept (.env is never regenerated).
set -euo pipefail
[ "$(id -u)" = 0 ] || { echo 'Run with sudo.' >&2; exit 1; }
ROOT=/opt/os-communications
HOST=chat.kniterp.online
BRANCH=${OS_BRANCH:-main}
[[ $BRANCH =~ ^[A-Za-z0-9._/-]+$ ]] || { echo "Bad OS_BRANCH" >&2; exit 2; }
step() { echo; echo "== $*"; }

step "SSH: key only, no root login"
install -m 0644 /dev/stdin /etc/ssh/sshd_config.d/00-os-closed.conf <<'EOF'
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
EOF
sshd -t && systemctl reload ssh
sshd -T | grep -E '^(passwordauthentication|kbdinteractiveauthentication|permitrootlogin|pubkeyauthentication) '

step "Firewall (the cloud security group is the outer layer)"
ufw default deny incoming >/dev/null; ufw default allow outgoing >/dev/null; ufw default deny routed >/dev/null
ufw allow 22/tcp >/dev/null; ufw allow 443/tcp >/dev/null; ufw --force enable >/dev/null
ufw status | sed -n '1p;/ALLOW/p'

step "Swap 1 GB"
if ! swapon --show=NAME --noheadings | grep -q /swapfile; then
    [ -f /swapfile ] || { fallocate -l 1G /swapfile; chmod 600 /swapfile; mkswap /swapfile >/dev/null; }
    swapon /swapfile
    grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi
free -h | sed -n '1p;3p'

step "Packages"
apt-get update -q >/dev/null
DEBIAN_FRONTEND=noninteractive apt-get install -y -q --no-install-recommends docker.io docker-compose-v2 docker-buildx nginx age git python3 certbot >/dev/null
systemctl disable --now nginx >/dev/null 2>&1 || true
docker --version; docker compose version; nginx -v 2>&1; age --version; certbot --version 2>&1

step "Checkout and .env"
[ -d "$ROOT/.git" ] || git clone -q --depth 1 --branch "$BRANCH" https://github.com/basilsolanji-hash/Messenger-KnitERP.git "$ROOT"
cd "$ROOT"
git log -1 --format='commit %H %cs'
[ -f .env ] || python3 scripts/init-dev.py >/dev/null
chmod 600 .env
python3 - "$HOST" <<'PY'
import sys
p, host = '/opt/os-communications/.env', sys.argv[1]
lines = [l for l in open(p).read().splitlines() if not l.startswith('AllowedHosts=')]
open(p, 'w').write('\n'.join(lines + ['AllowedHosts=' + host]) + '\n')
PY
echo ".env: $(stat -c '%a %U' .env), $(grep -c '' .env) lines (not shown)"

step "Compose unit"
install -m 0644 deploy/systemd/os-communications-compose.service.example /etc/systemd/system/os-communications-compose.service
systemd-analyze verify /etc/systemd/system/os-communications-compose.service 2>&1 | grep -v xfs_scrub || true
systemctl daemon-reload
systemctl start os-communications-compose.service   # builds the API image on first start: several minutes
echo "compose unit: $(systemctl is-active os-communications-compose.service) / $(systemctl is-enabled os-communications-compose.service)"

step "Trusted proxy = gateway of the compose network (what the API sees behind docker-proxy)"
gw=$(docker network inspect kniterp-messenger-dev_default --format '{{(index .IPAM.Config 0).Gateway}}')
[[ $gw =~ ^[0-9.]+$ ]] || { echo "Unexpected gateway: $gw" >&2; exit 1; }
python3 - "$gw" <<'PY'
import sys
p = '/opt/os-communications/.env'
lines = [l for l in open(p).read().splitlines() if not l.startswith('Auth__TrustedProxies__0=')]
open(p, 'w').write('\n'.join(lines + ['Auth__TrustedProxies__0=' + sys.argv[1]]) + '\n')
PY
systemctl restart os-communications-compose.service
echo "Auth__TrustedProxies__0=$gw"

step "Health through loopback"
for i in $(seq 1 30); do
    out=$(curl -s --max-time 3 -H "Host: $HOST" http://127.0.0.1:5080/health/ready || true)
    [ "$out" = '{"status":"ready"}' ] && break; sleep 2
done
echo "ready: $out"
echo "Host localhost -> $(curl -s -o /dev/null -w '%{http_code}' -H 'Host: localhost' http://127.0.0.1:5080/health/ready) (expected 400)"
echo "listeners:"; ss -tlnH | awk '{print "  " $4}' | sort -u
df -h / | tail -1
[ "$out" = '{"status":"ready"}' ] && echo && echo "SETUP_VM_OK" || { echo "API not ready" >&2; exit 1; }
