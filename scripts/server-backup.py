#!/usr/bin/env python3
"""Encrypted closed-test backup and safe offline verification; never restores over a live deployment.
Requires Docker Compose and age for backup. Verification requires an offline native age identity.
"""
import argparse
from contextlib import contextmanager
import fcntl
from datetime import datetime, timezone
import hashlib
import io
import json
import os
from pathlib import Path
import re
import selectors
import time
import shutil
import subprocess
import tarfile
import tempfile
import uuid

MAX_STATE = 8 * 1024 * 1024
MAX_DUMP = 20 * 1024 * 1024 * 1024


class BackupError(RuntimeError):
    pass


def private_file(path):
    return os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), 'wb')


def digest(path):
    result = hashlib.sha256()
    with path.open('rb') as stream:
        while block := stream.read(1024 * 1024):
            result.update(block)
    return result.hexdigest()


def run(command, **kwargs):
    try:
        result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120, **kwargs)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise BackupError('A required backup command failed or timed out.') from error
    if result.returncode:
        # Child output can contain credentials/identifiers. Keep it out of stdout and journals.
        raise BackupError('A required backup command returned an error; no backup was published.')
    return result.stdout


def compose(root, *arguments):
    return ['docker', 'compose', '--project-directory', str(root), '-f', str(root / 'compose.yaml'), *arguments]


def encryption(recipient):
    return ['age', '--encrypt', '--recipient', recipient]


def encrypt_stream(command, destination, recipient):
    producer = encryptor = None
    try:
        with tempfile.TemporaryFile() as producer_errors, tempfile.TemporaryFile() as crypto_errors, private_file(destination) as output:
            producer = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=producer_errors)
            encryptor = subprocess.Popen(encryption(recipient), stdin=producer.stdout, stdout=output, stderr=crypto_errors)
            producer.stdout.close()
            crypto_code = encryptor.wait(timeout=600)
            source_code = producer.wait(timeout=30)
            if crypto_code or source_code or destination.stat().st_size == 0:
                raise BackupError('Dump or encryption failed; no backup was published.')
    except (OSError, subprocess.TimeoutExpired) as error:
        raise BackupError('Dump or encryption failed or timed out.') from error
    finally:
        for process in (encryptor, producer):
            if process is not None and process.poll() is None:
                process.kill()
                process.wait()


def encrypt_bytes(value, destination, recipient):
    with private_file(destination) as output, tempfile.TemporaryFile() as errors:
        try:
            result = subprocess.run(encryption(recipient), input=value, stdout=output, stderr=errors, timeout=120)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise BackupError('State encryption failed or timed out.') from error
        if result.returncode:
            raise BackupError('State encryption failed; no backup was published.')


def state_archive(env, protection, metadata):
    if env.is_symlink() or not env.is_file() or env.stat().st_mode & 0o077:
        raise BackupError('The deployment .env must be a private regular file (0600).')
    if env.stat().st_size > 1024 * 1024:
        raise BackupError('Deployment environment file is too large.')
    entries = {'.env': env.read_bytes(), 'metadata.json': json.dumps(metadata).encode()}
    if protection.is_symlink() or not protection.is_dir():
        raise BackupError('Data Protection snapshot is missing.')
    for file in protection.iterdir():
        if file.is_symlink() or not file.is_file() or not re.fullmatch(r'[A-Za-z0-9_-][A-Za-z0-9_.-]{0,199}', file.name):
            raise BackupError('Unexpected Data Protection file or link.')
        if len(entries) >= 512 or file.stat().st_size + sum(map(len, entries.values())) > MAX_STATE - 512 * 1024:
            raise BackupError('Data Protection state exceeds the supported closed-test limit.')
        entries['protection/' + file.name] = file.read_bytes()
    if len(entries) > 512 or sum(map(len, entries.values())) > MAX_STATE - 1024 * len(entries):
        raise BackupError('Server state exceeds the supported closed-test limit.')
    memory = io.BytesIO()
    with tarfile.open(fileobj=memory, mode='w') as archive:
        for name, value in entries.items():
            item = tarfile.TarInfo(name)
            item.size, item.mode = len(value), 0o600
            archive.addfile(item, io.BytesIO(value))
    if memory.tell() > MAX_STATE:
        raise BackupError('Server state archive is too large.')
    return memory.getvalue()


@contextmanager
def deployment_lock(root):
    descriptor = os.open(Path(root) / '.server-backup.lock', os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, 'r+b') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise BackupError('Another backup for this deployment is already running.') from error
        try:
            yield
        finally:
            fcntl.flock(lock, fcntl.LOCK_UN)


def backup(root, recipient, destination, *, quiesce=False, revision=None):
    root = Path(root).resolve()
    if not (root / 'compose.yaml').is_file():
        raise BackupError('Deployment compose.yaml is missing.')
    with deployment_lock(root):
        return _backup(root, recipient, destination, quiesce=quiesce, revision=revision)


def _backup(root, recipient, destination, *, quiesce=False, revision=None):
    if not quiesce:
        raise BackupError('Pass --quiesce-api to acknowledge a short API stop for a matching DB/key snapshot.')
    if not re.fullmatch(r'age1[0-9a-z]{58}', recipient):
        raise BackupError('Use an independently verified native age X25519 public recipient.')
    if revision is not None and not re.fullmatch(r'[0-9a-f]{40}', revision):
        raise BackupError('Revision must be a full Git SHA.')
    root, destination = map(lambda p: Path(p).resolve(), (root, destination))
    if not (root / 'compose.yaml').is_file():
        raise BackupError('Deployment compose.yaml is missing.')
    destination.mkdir(mode=0o700, parents=True, exist_ok=True)
    if destination.stat().st_mode & 0o077:
        raise BackupError('Backup destination must be private (0700).')
    # Validate the public recipient before stopping API. No identity/private key is needed here.
    run(encryption(recipient), input=b'')
    running = run(compose(root, 'ps', '--status', 'running', '--services')).decode().splitlines()
    if 'db' not in running:
        raise BackupError('The Compose database must already be running.')
    restart = 'api' in running
    created = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')
    identifier = uuid.uuid4().hex
    final = destination / ('backup-' + created + '-' + identifier)
    with tempfile.TemporaryDirectory(prefix='.pending-', dir=destination) as temporary:
        stage = Path(temporary)
        payload = stage / 'payload'
        payload.mkdir(mode=0o700)
        try:
            if restart:
                run(compose(root, 'stop', '--timeout', '30', 'api'))
            protection = stage / 'protection'
            protection.mkdir(mode=0o700)
            run(compose(root, 'cp', 'api:/data/protection/.', str(protection)))
            database = payload / 'database.dump.age'
            encrypt_stream(compose(root, 'exec', '-T', 'db', 'pg_dump', '--username=messenger',
                                   '--dbname=messenger', '--format=custom', '--no-owner', '--no-acl'),
                           database, recipient)
            metadata = {'format': 1, 'id': identifier, 'createdUtc': created, 'revision': revision,
                        'databaseSha256': digest(database)}
            encrypt_bytes(state_archive(root / '.env', protection, metadata), payload / 'server-state.tar.age', recipient)
            manifest = metadata | {'files': {file.name: digest(file) for file in payload.iterdir()}}
            with private_file(payload / 'manifest.json') as file:
                file.write((json.dumps(manifest, indent=2) + '\n').encode())
            if final.exists():
                raise BackupError('Backup output already exists.')
            payload.rename(final)
        finally:
            if restart:
                try:
                    run(compose(root, 'start', 'api'))
                except BackupError as error:
                    raise BackupError('API restart failed. Check the deployment immediately; a completed backup may exist.') from error
    return final


def decrypt(source, output, identity, limit):
    process = None
    try:
        with private_file(output) as target, tempfile.TemporaryFile() as errors:
            process = subprocess.Popen(['age', '--decrypt', '--identity', str(identity), str(source)], stdout=subprocess.PIPE, stderr=errors)
            total = 0
            with selectors.DefaultSelector() as selector:
                selector.register(process.stdout, selectors.EVENT_READ)
                deadline = time.monotonic() + 600
                while True:
                    if time.monotonic() >= deadline:
                        raise BackupError('Backup decryption timed out.')
                    if not selector.select(timeout=1):
                        continue
                    block = os.read(process.stdout.fileno(), 1024 * 1024)
                    if not block:
                        break
                    total += len(block)
                    if total > limit:
                        raise BackupError('Decrypted data exceeds the allowed verification limit.')
                    target.write(block)
            if process.wait(timeout=30):
                raise BackupError('age rejected the backup. Do not use partial decrypted data.')
    except (OSError, subprocess.TimeoutExpired) as error:
        raise BackupError('Backup decryption failed or timed out.') from error
    finally:
        if process is not None:
            if process.stdout is not None:
                process.stdout.close()
            if process.poll() is None:
                process.kill()
                process.wait()


def unpack_state(archive_path, output, expected):
    with tarfile.open(archive_path, 'r:') as archive:
        members = archive.getmembers()
        names = [member.name for member in members]
        if len(members) > 512 or len(names) != len(set(names)) or sum(m.size for m in members) > MAX_STATE:
            raise BackupError('Invalid server state archive.')
        for member in members:
            allowed = member.name in ('.env', 'metadata.json') or re.fullmatch(r'protection/[A-Za-z0-9_-][A-Za-z0-9_.-]{0,199}', member.name)
            if not allowed or not member.isfile() or member.size < 0:
                raise BackupError('Unsafe path or file type in server state archive.')
        if '.env' not in names or 'metadata.json' not in names:
            raise BackupError('Server state is incomplete.')
        metadata = json.load(archive.extractfile('metadata.json'))
        if not isinstance(metadata, dict):
            raise BackupError('Invalid encrypted metadata.')
        if any(metadata.get(key) != expected.get(key) for key in ('format', 'id', 'createdUtc', 'revision', 'databaseSha256')):
            raise BackupError('Encrypted state does not belong to this database backup.')
        for member in members:
            target = output / member.name
            target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
            with private_file(target) as stream:
                shutil.copyfileobj(archive.extractfile(member), stream)


def verify(source, identity, output):
    source, identity, output = map(lambda p: Path(p).resolve(), (source, identity, output))
    if identity.is_symlink() or not identity.is_file() or identity.stat().st_mode & 0o077 or identity.stat().st_size > 65536:
        raise BackupError('A private native age identity file (0600) is required on the restore host.')
    lines = [line for line in identity.read_text().splitlines() if line and not line.startswith('#')]
    if not lines or any(not re.fullmatch(r'AGE-SECRET-KEY-1[0-9A-Z]+', line) for line in lines):
        raise BackupError('Only unencrypted native age identity files are supported by offline verification.')
    if output.exists():
        raise BackupError('Verification output already exists; nothing was overwritten.')
    manifest_path = source / 'manifest.json'
    if not manifest_path.is_file() or manifest_path.stat().st_size > 16384:
        raise BackupError('Invalid backup manifest.')
    manifest = json.loads(manifest_path.read_bytes())
    if not isinstance(manifest, dict):
        raise BackupError('Invalid backup manifest.')
    files = manifest.get('files', {})
    if manifest.get('format') != 1 or not isinstance(files, dict) or set(files) != {'database.dump.age', 'server-state.tar.age'}:
        raise BackupError('Unsupported or incomplete backup format.')
    if manifest.get('databaseSha256') != files['database.dump.age']:
        raise BackupError('Manifest database binding mismatch.')
    for name, expected in files.items():
        file = source / name
        if file.is_symlink() or not file.is_file() or digest(file) != expected:
            raise BackupError('Backup ciphertext checksum mismatch.')
    output.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.verify-', dir=output.parent) as temporary:
        stage = Path(temporary)
        decrypt(source / 'database.dump.age', stage / 'database.dump', identity, MAX_DUMP)
        decrypt(source / 'server-state.tar.age', stage / 'state.tar', identity, MAX_STATE)
        unpack_state(stage / 'state.tar', stage / 'server-state', manifest)
        (stage / 'state.tar').unlink()
        stage.rename(output)
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    create = sub.add_parser('backup')
    create.add_argument('--root', required=True)
    create.add_argument('--recipient', required=True)
    create.add_argument('--destination', required=True)
    create.add_argument('--quiesce-api', action='store_true')
    create.add_argument('--revision')
    check = sub.add_parser('verify')
    check.add_argument('--backup', required=True)
    check.add_argument('--identity', required=True)
    check.add_argument('--output', required=True)
    args = parser.parse_args()
    try:
        if args.command == 'backup':
            result = backup(args.root, args.recipient, args.destination,
                            quiesce=args.quiesce_api, revision=args.revision)
            print('Encrypted backup created: ' + result.name)
        else:
            verify(args.backup, args.identity, args.output)
            print('Decryption and state binding verified. No database or live deployment was restored.')
    except (BackupError, OSError, ValueError, tarfile.TarError, KeyError) as error:
        # No raw command output or exception arguments containing secret material.
        parser.exit(2, str(error) + '\n' if isinstance(error, BackupError) else 'Backup operation failed; no live restore performed.\n')


if __name__ == '__main__':
    main()
