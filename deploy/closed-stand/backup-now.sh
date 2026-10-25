#!/usr/bin/env bash
# Manual encrypted backup on the VPS (step 8). Briefly stops the API (--quiesce-api) and starts it again.
# Only the PUBLIC age recipient is given; the identity (private key) stays on the offline computer.
#   backup-now.sh age1...
set -euo pipefail
ROOT=/opt/os-communications
recipient=${1:-}
[[ $recipient =~ ^age1[02-9ac-hj-np-z]{58}$ ]] || { echo 'Give the full public age recipient (age1..., 62 characters).' >&2; exit 2; }
install -d -m 0700 /etc/os-communications /var/backups/os-communications
( umask 077; printf 'OS_BACKUP_RECIPIENT=%s\n' "$recipient" > /etc/os-communications/backup.env )
cd "$ROOT"
echo '== row counts at backup time (compare after restore)'
docker compose exec -T db psql -U messenger -d messenger -At -F ' ' -c \
  "SELECT 'accounts', count(*) FROM accounts UNION ALL SELECT 'devices', count(*) FROM devices
   UNION ALL SELECT 'envelopes', count(*) FROM envelopes UNION ALL SELECT 'refresh_families', count(*) FROM refresh_families;"
python3 scripts/server-backup.py backup --root "$ROOT" --recipient "$recipient" \
    --destination /var/backups/os-communications --quiesce-api
HOST=$(grep -m1 '^AllowedHosts=' .env | cut -d= -f2-)
echo -n '== API after backup: '; curl -sS --max-time 10 -H "Host: $HOST" http://127.0.0.1:5080/health/ready; echo
ls -lt /var/backups/os-communications | head -3
df -h / | tail -1
