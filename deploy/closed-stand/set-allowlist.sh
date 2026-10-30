#!/usr/bin/env bash
# Replace the HTTPS allowlist: builds a NEW config directory, tests it with the unit's own
# capability, switches the dedicated unit through a drop-in and rolls back if it does not start.
# Usage: set-allowlist.sh --tester IP [--tester IP]... [--operator IP]...
set -euo pipefail
ROOT=/opt/os-communications
UNIT=os-communications-https.service
DROPIN_DIR=/etc/systemd/system/$UNIT.d
DROPIN=$DROPIN_DIR/10-config-dir.conf
TLS=/etc/os-communications-tls
[ "$(id -u)" = 0 ] || { echo 'Run as root.' >&2; exit 1; }
[ $# -gt 0 ] || { sed -n '2,4p' "$0" >&2; exit 2; }

HOST=$(grep -m1 '^AllowedHosts=' "$ROOT/.env" | cut -d= -f2-)
last=$(ls -d /etc/os-communications-https-v* 2>/dev/null | sed 's/.*-v//' | sort -n | tail -1)
NEW=/etc/os-communications-https-v$(( ${last:-0} + 1 ))

python3 "$ROOT/scripts/prepare-closed-https.py" --hostname "$HOST" \
    --certificate $TLS/fullchain.pem --private-key $TLS/current.key --backup-certificate $TLS/backup-public.pem \
    --bind 0.0.0.0 --output "$NEW" "$@"
chgrp os-closed-https "$NEW" "$NEW/nginx.conf" "$NEW/nginx-http.conf"
chmod 0750 "$NEW"; chmod 0640 "$NEW/nginx.conf" "$NEW/nginx-http.conf"
chown os-closed-https:os-closed-https "$NEW/runtime"; chmod 0700 "$NEW/runtime"

# nginx -t opens the listen socket: test with the same single capability the unit has.
# EADDRINUSE from the running instance is ignored by nginx in test mode; EACCES is not.
systemd-run --quiet --wait --pipe --collect -p User=os-closed-https -p Group=os-closed-https \
    -p AmbientCapabilities=CAP_NET_BIND_SERVICE -p CapabilityBoundingSet=CAP_NET_BIND_SERVICE -p NoNewPrivileges=yes \
    /usr/sbin/nginx -t -p "$NEW/" -c "$NEW/nginx.conf"

mkdir -p "$DROPIN_DIR"
previous=$(mktemp)
if [ -f "$DROPIN" ]; then cp "$DROPIN" "$previous"; else : > "$previous"; fi
cat > "$DROPIN" <<CONF
[Service]
ExecStartPre=
ExecStartPre=/usr/sbin/nginx -t -p $NEW/ -c $NEW/nginx.conf
ExecStart=
ExecStart=/usr/sbin/nginx -p $NEW/ -c $NEW/nginx.conf
ReadWritePaths=
ReadWritePaths=$NEW/runtime
CONF
systemctl daemon-reload
if systemctl restart "$UNIT" && sleep 2 && systemctl is-active --quiet "$UNIT"; then
    rm -f "$previous"
    echo "ALLOWLIST_OK $NEW"
    grep -E '^\s*allow' "$NEW/nginx-http.conf" | sed 's/^ *//' | sort -u
else
    if [ -s "$previous" ]; then cp "$previous" "$DROPIN"; else rm -f "$DROPIN"; fi
    rm -f "$previous"
    systemctl daemon-reload; systemctl restart "$UNIT" || true
    echo "ROLLED_BACK: $UNIT kept the previous configuration; new directory $NEW left for inspection." >&2
    exit 1
fi
