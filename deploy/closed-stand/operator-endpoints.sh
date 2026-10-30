#!/usr/bin/env bash
# Turn the operator /api/v1/dev/* routes on or off in the API (Dev__EnableOperatorEndpoints) and verify via loopback.
# Restarting the API drops Fake SMS codes held in memory: do not run during an ongoing phone registration.
#   operator-endpoints.sh on|off
set -euo pipefail
ROOT=/opt/os-communications
case "${1:-}" in on) value=true; expect='400 401' ;; off) value=false; expect='404' ;; *) sed -n '2,4p' "$0" >&2; exit 2 ;; esac
cd "$ROOT"
python3 - "$value" <<'PY'
import sys
from pathlib import Path
path = Path('.env')
lines = [l for l in path.read_text(encoding='utf-8').splitlines() if not l.startswith('Dev__EnableOperatorEndpoints=')]
path.write_text('\n'.join(lines + ['Dev__EnableOperatorEndpoints=' + sys.argv[1]]) + '\n', encoding='utf-8')
PY
HOST=$(grep -m1 '^AllowedHosts=' .env | cut -d= -f2-)
systemctl restart os-communications-compose
for _ in $(seq 1 30); do curl -fsS -o /dev/null -H "Host: $HOST" http://127.0.0.1:5080/health/ready 2>/dev/null && break; sleep 2; done
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "Host: $HOST" -H 'Content-Type: application/json' -d '{}' \
    http://127.0.0.1:5080/api/v1/dev/sms/code)
echo "Dev__EnableOperatorEndpoints=$value; POST /api/v1/dev/sms/code without key -> $code (expected: $expect)"
[[ " $expect " == *" $code "* ]] && echo OPERATOR_ENDPOINTS_${1^^}_OK || { echo 'UNEXPECTED RESULT' >&2; exit 1; }
