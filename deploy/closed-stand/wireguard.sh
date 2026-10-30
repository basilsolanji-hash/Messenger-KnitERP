#!/usr/bin/env bash
# Private WireGuard access to the closed stand so phones work from any network, including mobile data.
# Split tunnel: phones send ONLY traffic for the stand's own IP through the tunnel; no forwarding or NAT on the VPS.
# Nginx then sees each phone as a fixed 10.66.0.N address, which goes into the HTTPS allowlist.
#   wireguard.sh init            install, create the server key, start wg0, open UDP 51820
#   wireguard.sh add NAME N      new peer NAME with 10.66.0.N (2-254); shows a QR code to scan in the WireGuard app
#   wireguard.sh forget          erase stored peer configs (their private keys) after the QR codes were scanned
#   wireguard.sh status
set -euo pipefail
WG=/etc/wireguard
CONF=$WG/wg0.conf
PEERS=$WG/os-peers
PORT=51820
[ "$(id -u)" = 0 ] || { echo 'Run as root.' >&2; exit 1; }
cmd=${1:-}
case "$cmd" in
init)
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends wireguard-tools qrencode >/dev/null
    install -d -m 0700 "$WG" "$PEERS"
    if [ ! -f "$CONF" ]; then
        ( umask 077; wg genkey > "$WG/server.key"
          # The private key stays in its own file; wg0.conf never contains it.
          printf '[Interface]\nAddress = 10.66.0.1/24\nListenPort = %s\nPostUp = wg set %%i private-key %s/server.key\n' "$PORT" "$WG" > "$CONF" )
    fi
    ufw allow "$PORT/udp" >/dev/null
    systemctl enable --now wg-quick@wg0 >/dev/null 2>&1
    systemctl is-active wg-quick@wg0
    wg show wg0 listening-port
    echo WG_INIT_OK ;;
add)
    name=${2:-}; n=${3:-}
    [[ $name =~ ^[a-z0-9-]{1,20}$ ]] || { echo 'NAME: lowercase letters, digits, hyphen.' >&2; exit 2; }
    [[ $n =~ ^[0-9]+$ ]] && [ "$n" -ge 2 ] && [ "$n" -le 254 ] || { echo 'N must be 2..254.' >&2; exit 2; }
    grep -qE "^# peer $name\$|^AllowedIPs = 10\.66\.0\.$n/32\$" "$CONF" && { echo "Peer $name or 10.66.0.$n already exists." >&2; exit 1; }
    host=$(grep -m1 '^AllowedHosts=' /opt/os-communications/.env | cut -d= -f2-)
    public_ip=$(getent ahostsv4 "$host" | awk 'NR==1{print $1}')
    [ -n "$public_ip" ] || { echo "Cannot resolve $host." >&2; exit 1; }
    umask 077
    private=$(wg genkey); public=$(printf '%s' "$private" | wg pubkey); psk=$(wg genpsk)
    printf '\n[Peer]\n# peer %s\nPublicKey = %s\nPresharedKey = %s\nAllowedIPs = 10.66.0.%s/32\n' "$name" "$public" "$psk" "$n" >> "$CONF"
    wg set wg0 peer "$public" preshared-key <(printf '%s\n' "$psk") allowed-ips "10.66.0.$n/32"
    client=$PEERS/$name.conf
    printf '[Interface]\nPrivateKey = %s\nAddress = 10.66.0.%s/32\n\n[Peer]\nPublicKey = %s\nPresharedKey = %s\nEndpoint = %s:%s\nAllowedIPs = %s/32\nPersistentKeepalive = 25\n' \
        "$private" "$n" "$(wg show wg0 public-key)" "$psk" "$public_ip" "$PORT" "$public_ip" > "$client"
    unset private psk
    qrencode -t ansiutf8 < "$client"
    echo "Scan this QR in the WireGuard app on the phone ($name). Do not photograph or share it: it contains the phone's key."
    echo "Allowlist entry for this phone: --tester 10.66.0.$n/32"
    echo WG_PEER_OK ;;
forget)
    find "$PEERS" -type f -name '*.conf' -print -exec shred -u {} \;
    echo WG_PEER_CONFIGS_ERASED ;;
status)
    systemctl is-active wg-quick@wg0
    # Shows public keys, addresses and last handshakes only.
    wg show wg0 | grep -vi 'preshared' ;;
*) sed -n '2,10p' "$0" >&2; exit 2 ;;
esac
