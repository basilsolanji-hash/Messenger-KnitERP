#!/usr/bin/env python3
"""Prepare (never install) an isolated Nginx configuration and public pins.

Requires Python 3.10+, OpenSSL and existing certificates/unencrypted TLS key.
No private key or server secret is copied into the output bundle.
"""
import argparse
import base64
from datetime import datetime, timezone
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import subprocess


class InvalidConfiguration(ValueError):
    pass


def openssl(*arguments, data=None):
    try:
        result = subprocess.run(['openssl', *map(str, arguments)], input=data if data is not None else b'',
                                capture_output=True, timeout=10, check=False)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise InvalidConfiguration('OpenSSL is unavailable or timed out.') from error
    if result.returncode:
        # OpenSSL output can include private paths/input; do not forward it to logs.
        raise InvalidConfiguration('OpenSSL rejected a certificate or TLS key.')
    return result.stdout


def safe_path(value):
    path = Path(value).resolve()
    if not re.fullmatch(r'/[A-Za-z0-9_./-]+', str(path)):
        raise InvalidConfiguration('Configuration paths require ASCII letters/digits, slash, dot, underscore or hyphen.')
    return path


def hostname(value):
    if value != value.lower() or len(value) > 253 or '.' not in value:
        raise InvalidConfiguration('Use a lowercase ASCII DNS hostname, without scheme, port or path.')
    labels = value.split('.')
    if any(not re.fullmatch(r'[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?', label) for label in labels):
        raise InvalidConfiguration('Invalid DNS hostname.')
    try:
        ipaddress.ip_address(value)
    except ValueError:
        return value
    raise InvalidConfiguration('A DNS hostname, not an IP address, is required.')


def networks(values):
    result = []
    for value in values:
        try:
            network = ipaddress.ip_network(value, strict=True)
        except ValueError as error:
            raise InvalidConfiguration('Allowlist entries must be canonical IP addresses or CIDRs.') from error
        if network.prefixlen == 0 or network.is_multicast or network.is_unspecified:
            raise InvalidConfiguration('Allow-all, multicast and unspecified allowlist entries are forbidden.')
        if str(network) not in result:
            result.append(str(network))
    for version in (4, 6):
        collapsed = ipaddress.collapse_addresses(ipaddress.ip_network(item) for item in result
                                                if ipaddress.ip_network(item).version == version)
        if any(item.prefixlen == 0 for item in collapsed):
            raise InvalidConfiguration('Allowlist must not cover the entire IP address space.')
    if len(result) > 256:
        raise InvalidConfiguration('At most 256 entries per allowlist are supported.')
    return result


def certificate_public_key(path):
    public_pem = openssl('x509', '-in', path, '-pubkey', '-noout')
    return openssl('pkey', '-pubin', '-outform', 'DER', data=public_pem)


def certificate_valid_now(path):
    lines = openssl('x509', '-in', path, '-noout', '-dates').decode('ascii').splitlines()
    dates = {name: datetime.strptime(value, '%b %d %H:%M:%S %Y GMT').replace(tzinfo=timezone.utc)
             for name, value in (line.split('=', 1) for line in lines)}
    now = datetime.now(timezone.utc)
    if not dates['notBefore'] <= now < dates['notAfter']:
        raise InvalidConfiguration('Current certificate is not valid at the current time.')


def pin(public_der):
    return 'sha256/' + base64.b64encode(hashlib.sha256(public_der).digest()).decode('ascii')


def location(pattern, allowed, api_port, websocket=False):
    acl = '\n'.join('        allow ' + item + ';' for item in allowed)
    connection = '$os_closed_connection' if websocket else '""'
    upgrade = '$http_upgrade' if websocket else '""'
    return f'''    # Regex order and case-insensitivity protect ASP.NET's case-insensitive routes.
    location ~* {pattern} {{
{acl}
        deny all;
        proxy_pass http://127.0.0.1:{api_port};
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header Upgrade {upgrade};
        proxy_set_header Connection {connection};
        proxy_connect_timeout 5s;
        proxy_send_timeout 30s;
        proxy_read_timeout {'75s' if websocket else '30s'};
        proxy_next_upstream off;
        proxy_buffering off;
        proxy_request_buffering off;
        proxy_cache off;
        proxy_store off;
    }}
'''


def prepare(*, host, certificate, private_key, backup_certificate, testers, operators, output,
            bind='127.0.0.1', port=443, api_port=5080):
    host = hostname(host)
    try:
        bind_ip = ipaddress.ip_address(bind)
    except ValueError as error:
        raise InvalidConfiguration('Bind must be an IP address.') from error
    if not isinstance(port, int) or not isinstance(api_port, int) or not 1 <= port <= 65535 or not 1 <= api_port <= 65535:
        raise InvalidConfiguration('Ports must be integers from 1 to 65535.')
    tester_networks = networks(testers)
    operator_networks = networks(operators)
    if not tester_networks:
        raise InvalidConfiguration('An explicit tester allowlist is required.')
    certificate, private_key, backup_certificate, output = map(safe_path,
        (certificate, private_key, backup_certificate, output))
    for file in (certificate, private_key, backup_certificate):
        if not file.is_file():
            raise InvalidConfiguration('A certificate or key file is missing.')
    if private_key.stat().st_mode & 0o007:
        raise InvalidConfiguration('TLS private key must not be accessible to other users.')
    certificate_valid_now(certificate)
    if b'DNS:' not in openssl('x509', '-in', certificate, '-noout', '-ext', 'subjectAltName'):
        raise InvalidConfiguration('Current certificate requires a DNS subjectAltName.')
    # OpenSSL x509 -checkhost may print a mismatch while returning success on some versions.
    check = openssl('x509', '-in', certificate, '-noout', '-checkhost', host)
    if b'does match certificate' not in check:
        raise InvalidConfiguration('Current certificate does not match the hostname.')
    current_public = certificate_public_key(certificate)
    key_public = openssl('pkey', '-in', private_key, '-pubout', '-outform', 'DER')
    if current_public != key_public:
        raise InvalidConfiguration('TLS private key does not match the current certificate.')
    pins = [pin(current_public), pin(certificate_public_key(backup_certificate))]
    if pins[0] == pins[1]:
        raise InvalidConfiguration('Current and backup SPKI keys must be different.')
    address = f'[{bind_ip}]' if bind_ip.version == 6 else str(bind_ip)
    listen = f'{address}:{port}'
    tls = f'''    ssl_certificate {certificate};
    ssl_certificate_key {private_key};
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5:!3DES;
    ssl_session_tickets off;
'''
    config = f'''# Generated include for nginx's http {{ }} context, never the main context.
# Bind defaults to loopback. No port 80 listener and no automatic certificate issuance.
# No real_ip_header or set_real_ip_from: ACLs use the direct TCP peer.
server_tokens off;
map $http_upgrade $os_closed_connection {{
    default upgrade;
    '' close;
}}
server {{
    listen {listen} ssl default_server;
    server_name _;
{tls}    access_log off;
    error_log {output}/runtime/nginx-error.log crit;
    return 444;
}}
server {{
    listen {listen} ssl;
    server_name {host};
{tls}    ssl_session_cache shared:OS_CLOSED_TLS:1m;
    ssl_session_timeout 10m;
    access_log off;
    error_log {output}/runtime/nginx-error.log crit;
    merge_slashes on;
    client_max_body_size 1m;
    client_header_timeout 15s;
    client_body_timeout 15s;
    keepalive_timeout 30s;
    add_header Cache-Control "no-store" always;
    add_header X-Content-Type-Options nosniff always;
    if ($ssl_server_name != {host}) {{ return 421; }}
'''
    config += location(r'^/api/v1/dev(?:/|$)', operator_networks, api_port)
    config += location(r'^/(?:openapi|health)(?:/|$)', operator_networks, api_port)
    config += location(r'^/api/v1(?:/|$)', tester_networks, api_port)
    config += location(r'^/hubs/events(?:/|$)', tester_networks, api_port, websocket=True)
    config += '    location / { return 404; }\n}\n'
    origin = 'https://' + host + ('' if port == 443 else ':' + str(port))
    main_config = f'''# Dedicated closed-test instance: no inherited host HTTP/global configuration.
# Run as an unprivileged service user with read access to the TLS key.
daemon off;
master_process off;
worker_processes 1;
pid {output}/runtime/nginx.pid;
error_log {output}/runtime/nginx-error.log crit;
events {{ worker_connections 1024; }}
http {{
    client_body_temp_path {output}/runtime/body;
    proxy_temp_path {output}/runtime/proxy;
    fastcgi_temp_path {output}/runtime/fastcgi;
    uwsgi_temp_path {output}/runtime/uwsgi;
    scgi_temp_path {output}/runtime/scgi;
    include {output}/nginx-http.conf;
}}
'''
    files = {'nginx.conf': main_config, 'nginx-http.conf': config, 'server-endpoint.json': json.dumps({'origin': origin, 'pins': pins}, indent=2) + '\n',
             'pins.json': json.dumps(pins, indent=2) + '\n'}
    output.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    try:
        output.mkdir(mode=0o700)
    except FileExistsError as error:
        raise InvalidConfiguration('Output already exists; nothing was overwritten.') from error
    try:
        (output / 'runtime').mkdir(mode=0o700)
        for name, content in files.items():
            descriptor = os.open(output / name, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(descriptor, 'w', encoding='utf-8') as stream:
                stream.write(content)
    except BaseException:
        shutil.rmtree(output)
        raise
    return {'origin': origin, 'pins': pins}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--hostname', required=True)
    parser.add_argument('--certificate', required=True, help='Existing current PEM leaf + chain')
    parser.add_argument('--private-key', required=True, help='Existing current unencrypted PEM key, never copied')
    parser.add_argument('--backup-certificate', required=True, help='Public certificate for a distinct backup key')
    parser.add_argument('--tester', action='append', required=True, help='Allowed direct tester IP/CIDR; repeatable')
    parser.add_argument('--operator', action='append', default=[], help='Allowed direct operator IP/CIDR; absent means deny all')
    parser.add_argument('--output', required=True, help='New private output directory, never overwritten')
    parser.add_argument('--bind', default='127.0.0.1', help='Listener IP; explicit 0.0.0.0 for a public IPv4 interface')
    parser.add_argument('--port', type=int, default=443)
    parser.add_argument('--api-port', type=int, default=5080, help='Loopback-only upstream API port')
    args = parser.parse_args()
    try:
        prepare(host=args.hostname, certificate=args.certificate, private_key=args.private_key,
                backup_certificate=args.backup_certificate, testers=args.tester, operators=args.operator,
                output=args.output, bind=args.bind, port=args.port, api_port=args.api_port)
    except (InvalidConfiguration, OSError, ValueError) as error:
        parser.exit(2, 'Preparation failed: ' + str(error) + '\n')
    print('Prepared isolated Nginx configuration and public endpoint/pins JSON. Not installed or deployed.')


if __name__ == '__main__':
    main()
