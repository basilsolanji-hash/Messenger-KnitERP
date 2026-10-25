#!/usr/bin/env python3
"""Show which client IPs the API charged SMS IP limits to, without printing secrets.

Counter keys are HMACs of the client address (AuthSecrets.CounterKey), so this recomputes them
for the given candidate addresses with the key from .env and reads the matching counters.
Usage: ip-counters.py IP [IP...]
"""
import base64
import hashlib
import hmac
import ipaddress
from pathlib import Path
import subprocess
import sys

ROOT = Path('/opt/os-communications')


def phone_index_key():
    for line in (ROOT / '.env').read_text(encoding='utf-8').splitlines():
        name, _, value = line.partition('=')
        if name == 'Auth__PhoneIndexKey':
            return base64.b64decode(value, validate=True)
    raise SystemExit('Auth__PhoneIndexKey is missing in .env')


def counter_prefix(key, scope, value):
    mac = hmac.new(key, ('counter-v1:' + scope + '\0' + value).encode(), hashlib.sha256).digest()
    return scope + ':' + mac[:20].hex() + ':'


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    addresses = [str(ipaddress.IPv4Address(item)) for item in sys.argv[1:]]
    key = phone_index_key()
    prefixes = {counter_prefix(key, scope, ip): (scope, ip) for ip in addresses for scope in ('sms-ip', 'sms-ip-day')}
    # Prefixes are lowercase hex plus fixed scope names, safe as SQL literals.
    where = ' OR '.join(f'"Key" LIKE \'{prefix}%\'' for prefix in prefixes)
    sql = f'SELECT "Key", "Count", "WindowEnd" FROM auth_counters WHERE {where};'
    rows = subprocess.run(['docker', 'compose', '--project-directory', str(ROOT), 'exec', '-T', 'db',
                           'psql', '-U', 'messenger', '-d', 'messenger', '-At', '-F', '|', '-c', sql],
                          check=True, capture_output=True, text=True).stdout.splitlines()
    found = {}
    for row in rows:
        counter, count, window_end = row.split('|')
        for prefix, label in prefixes.items():
            if counter.startswith(prefix):
                found.setdefault(label, []).append(f'{count} (window ends {window_end})')
    for ip in addresses:
        for scope in ('sms-ip', 'sms-ip-day'):
            print(f'{ip:<16} {scope:<11} ' + ('; '.join(found.get((scope, ip), [])) or '0'))


if __name__ == '__main__':
    main()
