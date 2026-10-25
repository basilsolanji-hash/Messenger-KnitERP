#!/usr/bin/env bash
# Offline verification and restore test (step 8) on a SEPARATE Linux/WSL computer that holds the age identity.
# Never on the VPS. Needs python3, age, docker and a checkout of this repository.
# Restores into a brand-new PostgreSQL 18 container without network and with its own anonymous volume,
# prints row counts, then deletes the container and every decrypted file.
#   restore-test.sh BACKUP_DIR IDENTITY_FILE
set -euo pipefail
[ $# -eq 2 ] || { sed -n '2,6p' "$0" >&2; exit 2; }
repo=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d "$HOME/os-restore-XXXXXX")
name=os-restore-test-$(date +%s)
cleanup() { docker rm --force --volumes "$name" >/dev/null 2>&1 || true; rm -rf -- "$work"; }
trap cleanup EXIT
cp -r -- "$1" "$work/backup"
python3 "$repo/scripts/server-backup.py" verify --backup "$work/backup" --identity "$2" --output "$work/verified"
docker run --detach --rm --name "$name" --network none -e POSTGRES_HOST_AUTH_METHOD=trust \
    -e POSTGRES_USER=messenger -e POSTGRES_DB=restorecheck postgres:18 >/dev/null
for _ in $(seq 1 60); do
    docker exec "$name" psql --host=127.0.0.1 -U messenger -d restorecheck -XAtc 'SELECT 1;' >/dev/null 2>&1 && break; sleep 1
done
docker exec -i "$name" pg_restore -U messenger -d restorecheck --exit-on-error --single-transaction --no-owner --no-acl \
    < "$work/verified/database.dump"
echo '== restored row counts (must match the counts printed by backup-now.sh)'
docker exec "$name" psql -U messenger -d restorecheck -At -F ' ' -c \
  "SELECT 'accounts', count(*) FROM accounts UNION ALL SELECT 'devices', count(*) FROM devices
   UNION ALL SELECT 'envelopes', count(*) FROM envelopes UNION ALL SELECT 'refresh_families', count(*) FROM refresh_families;"
echo "== postgres: $(docker exec "$name" postgres --version)"
echo "== server state files restored: $(find "$work/verified/server-state" -type f | wc -l) (names not shown)"
echo 'RESTORE_TEST_OK - container and decrypted files are deleted on exit.'
