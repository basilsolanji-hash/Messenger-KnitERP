#!/usr/bin/env python3
"""Destructive-to-fixtures-only GitHub-hosted CI check. Never run on a deployment/VPS.
Builds the actual image, backs up a synthetic opaque envelope, and restores into a new isolated PostgreSQL.
This is not a libsignal or physical-phone test.
"""
import base64
import importlib.util
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
PHASE = 'preflight'


def phase(value):
    global PHASE
    PHASE = value
    print('CI container phase: ' + value, flush=True)


def execute(command, *, input=None, timeout=180):
    result = subprocess.run(command, input=input, capture_output=True, timeout=timeout)
    if result.returncode:
        # Logs/exception arguments can expose .env/JWT/DP state. Report only the operation class.
        raise RuntimeError('CI container operation failed: ' + command[0])
    return result.stdout


def request(path, *, body=None, bearer=None, operator=None):
    headers = {'Content-Type': 'application/json'}
    if bearer:
        headers['Authorization'] = 'Bearer ' + bearer
    if operator:
        headers['X-Dev-Provisioning-Token'] = operator
    data = None if body is None else json.dumps(body).encode()
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(urllib.request.Request('http://127.0.0.1:5080' + path, data=data, headers=headers), timeout=5) as response:
        return json.load(response)


def wait_for(check, timeout=60):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            check()
            return
        except (RuntimeError, OSError, urllib.error.URLError):
            time.sleep(.5)
    raise RuntimeError('CI readiness timeout')


def main():
    if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('RUNNER_ENVIRONMENT') != 'github-hosted':
        raise RuntimeError('Only an isolated GitHub-hosted runner is supported; VPS execution is forbidden.')
    if (ROOT / '.env').exists():
        raise RuntimeError('Refusing to use an existing deployment environment.')
    compose = ['docker', 'compose', '--project-directory', str(ROOT), '-f', str(ROOT / 'compose.yaml')]
    # Compose cannot parse the required password interpolation before init-dev creates .env.
    # Inspect existing project labels directly, without evaluating configuration or creating credentials.
    if 'name: kniterp-messenger-dev' not in (ROOT / 'compose.yaml').read_text():
        raise RuntimeError('Unexpected source project name; review the isolation guard.')
    if execute(['docker', 'ps', '--all', '--quiet', '--filter', 'label=com.docker.compose.project=kniterp-messenger-dev']).strip():
        raise RuntimeError('Refusing to use existing Compose containers.')
    spec = importlib.util.spec_from_file_location('server_backup', ROOT / 'scripts/server-backup.py')
    backup = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(backup)
    restore_name = 'os-restore-ci-' + uuid.uuid4().hex
    source_created = restore_created = False
    try:
        phase('initialize isolated source environment')
        execute(['python3', str(ROOT / 'scripts/init-dev.py')])
        values = dict(line.split('=', 1) for line in (ROOT / '.env').read_text().splitlines() if line and not line.startswith('#'))
        source_created = True
        phase('build actual image and start source Compose')
        execute(compose + ['up', '--build', '--detach', '--wait'], timeout=600)
        wait_for(lambda: request('/health/ready'))
        phase('seed synthetic API storage and Data Protection cursor')
        session = request('/api/v1/dev/accounts', operator=values['Dev__ProvisioningKey'], body={
            'displayName': 'Synthetic CI restore fixture', 'deviceId': str(uuid.uuid4()), 'registrationId': 1,
            'identityKey': base64.b64encode(b'\x05' + secrets.token_bytes(32)).decode()})
        # Random bytes exercise opaque server storage only; they are not represented as valid Signal ciphertext.
        envelope = base64.b64encode(secrets.token_bytes(256)).decode()
        request('/api/v1/messages', bearer=session['accessToken'], body={
            'clientMessageId': str(uuid.uuid4()), 'recipientAccountId': session['accountId'],
            'recipientDeviceId': session['deviceId'], 'recipientGeneration': session['generation'],
            'messageType': 3, 'ciphertext': envelope})
        inbox = request('/api/v1/inbox', bearer=session['accessToken'])
        assert len(inbox['items']) == 1 and inbox['items'][0]['ciphertext'] == envelope and inbox['nextCursor']
        with tempfile.TemporaryDirectory(prefix='os-container-check-') as directory:
            work = Path(directory)
            identity = work / 'backup.identity'
            execute(['age-keygen', '-o', str(identity)])
            recipient = execute(['age-keygen', '-y', str(identity)]).decode().strip()
            phase('real Compose encrypted backup and verification')
            saved = backup.backup(ROOT, recipient, work / 'backups', quiesce=True,
                                  revision=os.environ.get('GITHUB_SHA'))
            verified = backup.verify(saved, identity, work / 'verified')
            assert (verified / 'server-state/.env').read_bytes() == (ROOT / '.env').read_bytes()
            keys = list((verified / 'server-state/protection').glob('key-*.xml'))
            assert keys, 'A real Data Protection key must survive the encrypted round trip.'
            # No network, no published port, brand-new anonymous PostgreSQL volume; never the source instance.
            phase('restore into separate empty PostgreSQL instance')
            execute(['docker', 'run', '--detach', '--rm', '--name', restore_name, '--network', 'none',
                     '--env', 'POSTGRES_HOST_AUTH_METHOD=trust', '--env', 'POSTGRES_USER=messenger',
                     '--env', 'POSTGRES_DB=restorecheck', 'postgres:18'])
            restore_created = True
            wait_for(lambda: execute(['docker', 'exec', restore_name, 'pg_isready', '-U', 'messenger', '-d', 'restorecheck']))
            with (verified / 'database.dump').open('rb') as source:
                result = subprocess.run(['docker', 'exec', '-i', restore_name, 'pg_restore', '--username=messenger',
                    '--dbname=restorecheck', '--exit-on-error', '--single-transaction', '--no-owner', '--no-acl'],
                    stdin=source, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120)
            if result.returncode:
                raise RuntimeError('Restore into the separate empty PostgreSQL instance failed.')
            counts = execute(['docker', 'exec', restore_name, 'psql', '--username=messenger', '--dbname=restorecheck',
                '-X', '-A', '-t', '--command', 'SELECT (SELECT count(*) FROM accounts), (SELECT count(*) FROM devices), (SELECT count(*) FROM envelopes);']).decode().strip()
            assert counts == '1|1|1', 'Restored fixture row counts do not match.'
            phase('check restored data and original API recovery')
            # Original API was restarted by backup and still accepts the original session/cursor.
            wait_for(lambda: request('/health/ready'))
            assert request('/api/v1/me', bearer=session['accessToken'])['accountId'] == session['accountId']
            from urllib.parse import quote
            assert request('/api/v1/inbox?cursor=' + quote(inbox['nextCursor'], safe=''), bearer=session['accessToken'])['items'] == []
            report = {'sourceCommit': os.environ.get('GITHUB_SHA'), 'imageBuild': True, 'realCompose': True,
                'realPostgreSqlDumpRestore': True, 'separateEmptyRestoreInstance': True,
                'restoredCounts': {'accounts': 1, 'devices': 1, 'envelopes': 1},
                'encryptedEnvironmentRoundTrip': True, 'dataProtectionKeyFilesRecovered': len(keys),
                'originalApiSessionAndCursorAfterBackup': True, 'validSignalCiphertext': False,
                'restoredApiStarted': False, 'publicHttps': False, 'physicalPhones': False}
            (ROOT / 'server-container-report.json').write_text(json.dumps(report, indent=2) + '\n')
            print('Actual container build, API storage, encrypted backup and isolated PostgreSQL restore passed.')
    finally:
        if restore_created:
            subprocess.run(['docker', 'rm', '--force', '--volumes', restore_name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if source_created:
            # No down -v / prune. Source test volumes are left for disposable runner teardown.
            subprocess.run(compose + ['down'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=90)
            (ROOT / '.env').unlink(missing_ok=True)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('Container acceptance failed during ' + PHASE + ': ' + type(error).__name__)
        raise SystemExit(1)
