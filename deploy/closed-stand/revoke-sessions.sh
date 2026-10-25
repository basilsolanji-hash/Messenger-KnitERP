#!/usr/bin/env bash
# Operator session revocation for the closed stand. The API checks every request against these rows
# and its queue worker closes revoked SignalR connections within about a second.
#   revoke-sessions.sh list               devices and live public sessions (no names or phone numbers)
#   revoke-sessions.sh all                revoke every live public (SMS) session; phones must sign in again
#   revoke-sessions.sh device DEVICE_ID   deactivate one device and revoke its sessions (also synthetic dev devices)
set -euo pipefail
cd /opt/os-communications
sql() { docker compose exec -T db psql -U messenger -d messenger -v ON_ERROR_STOP=1 -At -F ' | ' -c "$1"; }
list() {
    echo 'device_id | active | live_sessions'
    sql 'SELECT d."Id", d."Active", count(f."Id") FILTER (WHERE f."RevokedAt" IS NULL AND f."AbsoluteExpiresAt" > now())
         FROM devices d LEFT JOIN refresh_families f ON f."DeviceId" = d."Id" GROUP BY d."Id", d."Active" ORDER BY d."Active" DESC;'
}
case "${1:-}" in
    list) list ;;
    all)
        sql "UPDATE refresh_families SET \"RevokedAt\" = now(), \"RevokedReason\" = 'operator' WHERE \"RevokedAt\" IS NULL;"
        list ;;
    device)
        id=${2:-}
        [[ $id =~ ^[0-9a-fA-F-]{36}$ ]] || { echo 'Give a device id from "list".' >&2; exit 2; }
        sql "BEGIN; UPDATE devices SET \"Active\" = false WHERE \"Id\" = '$id';
             UPDATE refresh_families SET \"RevokedAt\" = now(), \"RevokedReason\" = 'operator' WHERE \"DeviceId\" = '$id' AND \"RevokedAt\" IS NULL; COMMIT;"
        list ;;
    *) sed -n '2,6p' "$0" >&2; exit 2 ;;
esac
