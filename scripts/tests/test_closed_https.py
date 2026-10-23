"""Unit + real-Nginx integration checks. NGINX_BINARY must name an installed test binary.
Run: NGINX_BINARY=/path/to/nginx python3 -m unittest discover -s scripts/tests -v
No real domain, provider, PostgreSQL, system CA store or deployment is modified.
"""
import base64
import hashlib
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import json
import os
from pathlib import Path
import shutil
import socket
import ssl
import subprocess
import tempfile
import threading
import time
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'prepare-closed-https.py'
spec = importlib.util.spec_from_file_location('closed_https', SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
HOST = 'closed.os.test'


def command(*args):
    result = subprocess.run(args, capture_output=True, timeout=15)
    if result.returncode:
        raise RuntimeError('Fixture command failed: ' + str(args[0]) + ': ' + result.stderr.decode(errors='replace'))
    return result.stdout


def cert(root, name, host=HOST, key=None):
    key = key or root / (name + '.key')
    public = root / (name + '.pem')
    args = ['openssl', 'req', '-x509', '-nodes', '-days', '2', '-subj', '/CN=' + host,
            '-addext', 'subjectAltName=DNS:' + host, '-out', str(public)]
    if key.exists():
        args += ['-key', str(key)]
    else:
        args += ['-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256', '-keyout', str(key)]
    command(*args)
    return public, key


class GeneratorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='os-https-generator-')
        self.root = Path(self.temp.name)
        self.current, self.key = cert(self.root, 'current')
        self.backup, _ = cert(self.root, 'backup')
        self.arguments = dict(host=HOST, certificate=self.current, private_key=self.key, backup_certificate=self.backup,
                              testers=['192.0.2.10'], operators=[], output=self.root / 'bundle')

    def tearDown(self):
        self.temp.cleanup()

    def prepare(self, **changes):
        return module.prepare(**(self.arguments | changes))

    def test_outputs_only_public_endpoint_data_and_exact_spki_pins(self):
        result = self.prepare()
        endpoint = json.loads((self.root / 'bundle/server-endpoint.json').read_text())
        self.assertEqual(result, endpoint)
        self.assertEqual({'origin', 'pins'}, set(endpoint))
        self.assertEqual('https://' + HOST, endpoint['origin'])
        self.assertEqual(2, len(set(endpoint['pins'])))
        for pin, certificate in zip(endpoint['pins'], [self.current, self.backup]):
            public = command('openssl', 'x509', '-in', str(certificate), '-pubkey', '-noout')
            der = subprocess.run(['openssl', 'pkey', '-pubin', '-outform', 'DER'], input=public, capture_output=True, check=True).stdout
            self.assertEqual('sha256/' + base64.b64encode(hashlib.sha256(der).digest()).decode(), pin)
        self.assertEqual(endpoint['pins'], json.loads((self.root / 'bundle/pins.json').read_text()))
        private = self.key.read_bytes()
        for file in (self.root / 'bundle').glob('*'):
            if file.is_file():
                self.assertNotIn(private, file.read_bytes())
                self.assertNotIn(b'BEGIN PRIVATE KEY', file.read_bytes())
                self.assertEqual(0o600, file.stat().st_mode & 0o777)
        self.assertEqual(0o700, (self.root / 'bundle').stat().st_mode & 0o777)

    def test_rejects_invalid_hosts_without_creating_bundle(self):
        for host in ['https://closed.os.test', 'closed.os.test/path', 'CLOSED.os.test', '127.0.0.1', 'closed.os.test;', 'closed.os.test\n', '*.os.test']:
            with self.subTest(host=host), self.assertRaises(module.InvalidConfiguration):
                self.prepare(host=host)
        self.assertFalse((self.root / 'bundle').exists())

    def test_rejects_missing_or_dangerous_allowlists(self):
        for entries in [[], ['0.0.0.0/0'], ['::/0'], ['0.0.0.0/1', '128.0.0.0/1'], ['::/1', '8000::/1'], ['all'], ['192.0.2.1; allow all'], ['192.0.2.1/24'], ['224.0.0.1'], ['0.0.0.0']]:
            with self.subTest(entries=entries), self.assertRaises(module.InvalidConfiguration):
                self.prepare(testers=entries)
        with self.assertRaises(module.InvalidConfiguration):
            self.prepare(operators=['0.0.0.0/0'])

    def test_same_key_in_different_certificates_is_not_a_backup(self):
        same, _ = cert(self.root, 'same', key=self.key)
        with self.assertRaises(module.InvalidConfiguration):
            self.prepare(backup_certificate=same)

    def test_mismatched_private_key_is_rejected(self):
        with self.assertRaises(module.InvalidConfiguration):
            self.prepare(private_key=self.root / 'backup.key')

    def test_wrong_hostname_is_rejected(self):
        with self.assertRaises(module.InvalidConfiguration):
            self.prepare(host='another.os.test')

    def test_certificate_without_dns_san_is_rejected(self):
        public = self.root / 'cn-only.pem'
        command('openssl', 'req', '-x509', '-key', str(self.key), '-days', '2', '-subj', '/CN=' + HOST, '-out', str(public))
        with self.assertRaises(module.InvalidConfiguration):
            self.prepare(certificate=public)

    def test_world_readable_key_is_rejected(self):
        self.key.chmod(0o644)
        with self.assertRaises(module.InvalidConfiguration):
            self.prepare()

    def test_existing_bundle_is_preserved(self):
        self.prepare()
        before = (self.root / 'bundle/nginx.conf').read_bytes()
        with self.assertRaises(module.InvalidConfiguration):
            self.prepare()
        self.assertEqual(before, (self.root / 'bundle/nginx.conf').read_bytes())

    def test_config_injection_paths_and_ports_are_rejected(self):
        for changes in [dict(output=self.root / 'bad$path'), dict(port=0), dict(api_port=65536), dict(bind='localhost'), dict(port='443')]:
            with self.subTest(changes=changes), self.assertRaises(module.InvalidConfiguration):
                self.prepare(**changes)

    def test_empty_operator_allowlist_is_explicit_deny_and_config_is_isolated(self):
        self.prepare()
        main = (self.root / 'bundle/nginx.conf').read_text()
        include = (self.root / 'bundle/nginx-http.conf').read_text()
        self.assertNotIn('/etc/nginx', main)
        self.assertNotIn('set_real_ip_from', main)
        self.assertIn('master_process off;', main)
        self.assertIn('listen 127.0.0.1:443 ssl;', include)
        self.assertIn('proxy_set_header X-Forwarded-For $remote_addr;', include)
        self.assertIn('proxy_next_upstream off;', include)
        self.assertIn('ssl_protocols TLSv1.2 TLSv1.3;', include)
        first = include.split('location ~* ^/api/v1/dev')[1].split('location ~*')[0]
        self.assertIn('deny all;', first)
        self.assertNotIn('allow ', first)


class NginxIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.binary = os.environ.get('NGINX_BINARY') or shutil.which('nginx')
        if not cls.binary:
            raise RuntimeError('Set NGINX_BINARY to run required real-Nginx integration tests; no silent skip.')
        cls.temp = tempfile.TemporaryDirectory(prefix='os-https-nginx-')
        cls.root = Path(cls.temp.name)
        cls.current, cls.key = cert(cls.root, 'current')
        cls.backup, _ = cert(cls.root, 'backup')
        cls.requests = []
        class Upstream(BaseHTTPRequestHandler):
            protocol_version = 'HTTP/1.1'
            def do_GET(self):
                cls.requests.append((self.path, dict(self.headers)))
                body = json.dumps({'path': self.path, 'xff': self.headers.get('X-Forwarded-For'),
                                   'host': self.headers.get('Host'), 'upgrade': self.headers.get('Upgrade')}).encode()
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            def do_POST(self):
                self.rfile.read(int(self.headers.get('Content-Length', 0)))
                self.do_GET()
            def log_message(self, *args):
                pass
        cls.upstream = ThreadingHTTPServer(('127.0.0.1', 0), Upstream)
        cls.thread = threading.Thread(target=cls.upstream.serve_forever, daemon=True)
        cls.thread.start()
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            cls.port = sock.getsockname()[1]
        cls.bundle = cls.root / 'bundle'
        cls.endpoint = module.prepare(host=HOST, certificate=cls.current, private_key=cls.key,
            backup_certificate=cls.backup, testers=['127.0.0.1'], operators=['127.0.0.2'], output=cls.bundle,
            port=cls.port, api_port=cls.upstream.server_port)
        # This container has UID0 with no capabilities or mapped alternate UID. Keep its existing UID
        # only in the fixture, instead of asking Nginx to chown temp paths to its compiled-in nobody.
        cls.fixture_options = ['-g', 'user root;'] if os.geteuid() == 0 else []
        command(cls.binary, *cls.fixture_options, '-t', '-p', str(cls.bundle) + '/', '-c', str(cls.bundle / 'nginx.conf'), '-e', 'stderr')
        cls.log = (cls.root / 'launcher.log').open('wb')
        cls.process = subprocess.Popen([cls.binary, *cls.fixture_options, '-p', str(cls.bundle) + '/', '-c', str(cls.bundle / 'nginx.conf'), '-e', 'stderr'], stdout=cls.log, stderr=subprocess.STDOUT)
        cls.context = ssl.create_default_context(cafile=str(cls.current))
        cls.context.minimum_version = ssl.TLSVersion.TLSv1_2
        deadline = time.monotonic() + 10
        while True:
            if cls.process.poll() is not None:
                cls.log.flush()
                raise RuntimeError('Nginx exited: ' + (cls.root / 'launcher.log').read_text())
            try:
                status, _, _ = cls.request('/api/v1/me')
                if status == 200:
                    break
            except OSError:
                pass
            if time.monotonic() >= deadline:
                raise RuntimeError('Nginx startup timeout')
            time.sleep(.05)

    @classmethod
    def tearDownClass(cls):
        cls.process.terminate()
        cls.process.wait(timeout=5)
        cls.log.close()
        cls.upstream.shutdown()
        cls.upstream.server_close()
        cls.temp.cleanup()

    @classmethod
    def request(cls, path, source='127.0.0.1', headers=None, context=None, host=HOST, sni=HOST):
        sock = socket.create_connection(('127.0.0.1', cls.port), timeout=3, source_address=(source, 0))
        try:
            tls = (context or cls.context).wrap_socket(sock, server_hostname=sni)
        except BaseException:
            sock.close()
            raise
        try:
            extra = ''.join(key + ': ' + value + '\r\n' for key, value in (headers or {}).items())
            tls.sendall(f'GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n{extra}\r\n'.encode())
            response = http.client.HTTPResponse(tls)
            response.begin()
            return response.status, response.read(), dict(response.getheaders())
        finally:
            tls.close()

    def test_tester_reaches_api_with_verified_tls_and_real_source_header(self):
        status, body, headers = self.request('/api/v1/me', headers={'X-Forwarded-For': '203.0.113.77'})
        self.assertEqual(200, status)
        self.assertEqual('127.0.0.1', json.loads(body)['xff'])
        self.assertEqual(HOST, json.loads(body)['host'])
        self.assertIn('no-store', headers['Cache-Control'])

    def test_non_tester_is_denied_even_with_forged_forwarded_headers(self):
        before = len(self.requests)
        status, _, _ = self.request('/api/v1/me', source='127.0.0.3', headers={'X-Forwarded-For': '127.0.0.1'})
        self.assertEqual(403, status)
        self.assertEqual(before, len(self.requests))

    def test_operator_paths_are_denied_to_tester_including_normalization_variants(self):
        paths = ['/api/v1/dev/accounts', '/api/v1/DEV/sms/code', '/API/V1/DEV/sms/code',
                 '/api/v1/%64ev/sms/code', '/api/v1/dev%2fsms/code', '/api/v1//dev/sms/code',
                 '/api/v1/other/../dev/sms/code', '/api/v1/dev', '/openapi/v1.json', '/OpenAPI/v1.json', '/health/live']
        for path in paths:
            with self.subTest(path=path):
                before = len(self.requests)
                status, _, _ = self.request(path, headers={'X-Forwarded-For': '127.0.0.2', 'X-Dev-Provisioning-Token': 'synthetic'})
                self.assertEqual(403, status)
                self.assertEqual(before, len(self.requests))

    def test_operator_can_reach_operator_routes_but_not_tester_routes(self):
        for path in ['/api/v1/dev/accounts', '/API/V1/DEV/sms/code', '/openapi/v1.json', '/health/live']:
            with self.subTest(path=path):
                self.assertEqual(200, self.request(path, source='127.0.0.2')[0])
        self.assertEqual(403, self.request('/api/v1/me', source='127.0.0.2')[0])

    def test_unknown_routes_are_not_proxied(self):
        before = len(self.requests)
        self.assertEqual(404, self.request('/not-a-route')[0])
        self.assertEqual(before, len(self.requests))

    def test_host_mismatch_is_not_proxied(self):
        before = len(self.requests)
        with self.assertRaises(http.client.RemoteDisconnected):
            self.request('/api/v1/me', host='other.os.test')
        self.assertEqual(before, len(self.requests))

    def test_untrusted_certificate_and_wrong_hostname_are_rejected_by_client(self):
        with self.assertRaises(ssl.SSLCertVerificationError):
            self.request('/api/v1/me', context=ssl.create_default_context())
        with self.assertRaises(ssl.SSLCertVerificationError):
            self.request('/api/v1/me', sni='wrong.os.test')

    def test_tls12_and_tls13_succeed_and_tls11_is_rejected(self):
        for version in [ssl.TLSVersion.TLSv1_2, ssl.TLSVersion.TLSv1_3]:
            with self.subTest(version=version):
                context = ssl.create_default_context(cafile=str(self.current))
                context.minimum_version = context.maximum_version = version
                self.assertEqual(200, self.request('/api/v1/me', context=context)[0])
        # OpenSSL's security level is lowered only in this negative client fixture, never in Nginx.
        context = ssl.create_default_context(cafile=str(self.current))
        import warnings
        with warnings.catch_warnings():
            warnings.simplefilter('ignore', DeprecationWarning)
            context.minimum_version = context.maximum_version = ssl.TLSVersion.TLSv1_1
        context.set_ciphers('ALL:@SECLEVEL=0')
        with self.assertRaises(ssl.SSLError):
            self.request('/api/v1/me', context=context)

    def test_curl_accepts_current_pin_and_rejects_backup_only_before_http(self):
        base = ['curl', '--silent', '--show-error', '--noproxy', '*', '--max-time', '5',
                '--cacert', str(self.current), '--resolve', f'{HOST}:{self.port}:127.0.0.1',
                '--output', os.devnull, '--write-out', '%{http_code}']
        url = f'https://{HOST}:{self.port}/api/v1/me'
        current = 'sha256//' + self.endpoint['pins'][0].removeprefix('sha256/')
        backup = 'sha256//' + self.endpoint['pins'][1].removeprefix('sha256/')
        accepted = subprocess.run(base + ['--pinnedpubkey', current, url], capture_output=True, timeout=8)
        self.assertEqual(0, accepted.returncode)
        self.assertEqual(b'200', accepted.stdout)
        before = len(self.requests)
        rejected = subprocess.run(base + ['--pinnedpubkey', backup, url], capture_output=True, timeout=8)
        self.assertEqual(90, rejected.returncode)
        self.assertEqual(before, len(self.requests))

    def test_upgrade_headers_reach_only_hub_route(self):
        self.assertEqual('websocket', json.loads(self.request('/hubs/events', headers={'Upgrade': 'websocket'})[1])['upgrade'])
        self.assertIsNone(json.loads(self.request('/api/v1/me', headers={'Upgrade': 'websocket'})[1])['upgrade'])


if __name__ == '__main__':
    unittest.main()
