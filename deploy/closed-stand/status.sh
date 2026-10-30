#!/usr/bin/env bash
# Read-only summary of the closed stand on the VPS. Prints no secrets.
set -u
ROOT=/opt/os-communications
cd "$ROOT" || exit 1
HOST=$(grep -m1 '^AllowedHosts=' .env | cut -d= -f2-)
CONF=$(systemctl show -p ExecStart --value os-communications-https | grep -o -- '-c [^ ;]*' | head -1 | cut -c4- | xargs dirname 2>/dev/null)

echo "== checkout: $(git log -1 --format='%H %cs')"
for unit in os-communications-compose.service os-communications-https.service os-communications-backup.timer; do
    echo "== $unit: $(systemctl is-active "$unit" 2>/dev/null) / $(systemctl is-enabled "$unit" 2>/dev/null)"
done
echo "== https config dir: ${CONF:-unknown}"
docker compose ps --format 'table {{.Service}}\t{{.Status}}\t{{.Ports}}'
echo "== listeners"; ss -tlnH | awk '{print $4}' | grep -v '^127.0.0.5' | sort -u
echo "== firewall"; ufw status | sed -n '1p;/ALLOW/p'
echo "== env (non-secret keys only)"
grep -E '^(ASPNETCORE_ENVIRONMENT|AllowedHosts|Auth__TrustedProxies__[0-9]+|Auth__Sms__Provider|Dev__EnableOperatorEndpoints)=' .env
echo "== .env mode: $(stat -c '%a %U' .env)"
echo -n "== health via loopback: "; curl -sS --max-time 5 -H "Host: $HOST" http://127.0.0.1:5080/health/ready; echo
if [ -n "$CONF" ] && [ -f "$CONF/server-endpoint.json" ]; then
    echo "== allowlist in $CONF/nginx-http.conf"
    grep -E '^\s*location|^\s*allow' "$CONF/nginx-http.conf" | sed 's/^ *//' | uniq
fi
echo "== certificate"; openssl x509 -in /etc/os-communications-tls/fullchain.pem -noout -subject -enddate
if [ -e /root/backup-tls.key ]; then echo "== WARNING: backup TLS private key is still on the server (/root/backup-tls.key)"; fi
echo "== resources"; free -h | sed -n '1,3p'; df -h / | tail -1
