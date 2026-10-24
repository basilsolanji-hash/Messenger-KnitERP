"""Real age round trips; Docker/pg_dump are explicit transport fixtures, not PostgreSQL acceptance."""
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / 'server-backup.py'
spec = importlib.util.spec_from_file_location('server_backup', SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class BackupTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix='os-backup-key-test-')
        cls.root = Path(cls.temp.name)
        cls.identity = cls.root / 'identity.txt'
        subprocess.run(['age-keygen', '-o', str(cls.identity)], capture_output=True, check=True, timeout=10)
        cls.recipient = module.run(['age-keygen', '-y', str(cls.identity)]).decode().strip()

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def setUp(self):
        self.case = tempfile.TemporaryDirectory(prefix='os-backup-test-')
        self.work = Path(self.case.name)
        self.deployment = self.work / 'deployment'
        self.deployment.mkdir()
        (self.deployment / 'compose.yaml').write_text('services: {}\n')
        self.env = b'SYNTHETIC_ENV=private-state-00000\n'
        (self.deployment / '.env').write_bytes(self.env)
        (self.deployment / '.env').chmod(0o600)
        self.dump = b'PGDMP-SYNTHETIC-NOT-A-REAL-POSTGRES-DUMP\n' + os.urandom(100000)
        self.dump_file = self.work / 'fixture.dump'
        self.dump_file.write_bytes(self.dump)
        self.events = []
        self.running = True
        self.fail_copy = False
        self.fail_dump = False

    def tearDown(self):
        self.case.cleanup()

    def fake_command(self, root, *args):
        # Commands are still launched and piped into actual age. Only Docker transport is replaced.
        if args[:4] == ('exec', '-T', 'db', 'pg_dump'):
            if self.fail_dump:
                return ['python3', '-c', 'import sys;sys.stdout.buffer.write(b"partial");sys.exit(1)']
            return ['python3', '-c', 'import pathlib,sys;sys.stdout.buffer.write(pathlib.Path(sys.argv[1]).read_bytes())', str(self.dump_file)]
        self.events.append(args)
        if args[:2] == ('ps', '--status'):
            return ['python3', '-c', 'print("db\\napi" if ' + repr(self.running) + ' else "db")']
        if args and args[0] == 'cp':
            if self.fail_copy:
                return ['python3', '-c', 'raise SystemExit(1)']
            return ['python3', '-c', 'import pathlib,sys;(pathlib.Path(sys.argv[1])/"key-test.xml").write_text("SYNTHETIC-DP-KEY-ONLY")', args[-1]]
        return ['python3', '-c', 'pass']

    def create(self):
        with patch.object(module, 'compose', self.fake_command):
            return module.backup(self.deployment, self.recipient, self.work / 'backups', quiesce=True,
                                 revision='a' * 40)

    def test_real_encryption_round_trip_needs_only_public_recipient_for_backup(self):
        backup = self.create()
        self.assertEqual({'manifest.json', 'database.dump.age', 'server-state.tar.age'}, {p.name for p in backup.iterdir()})
        for file in backup.iterdir():
            self.assertNotIn(self.env, file.read_bytes())
            self.assertNotIn(b'SYNTHETIC-DP-KEY-ONLY', file.read_bytes())
            self.assertEqual(0o600, file.stat().st_mode & 0o777)
        self.assertTrue(any(event[0] == 'stop' for event in self.events))
        self.assertEqual(('start', 'api'), self.events[-1])
        output = self.work / 'verified'
        module.verify(backup, self.identity, output)
        self.assertEqual(self.dump, (output / 'database.dump').read_bytes())
        self.assertEqual(self.env, (output / 'server-state/.env').read_bytes())
        self.assertEqual(b'SYNTHETIC-DP-KEY-ONLY', (output / 'server-state/protection/key-test.xml').read_bytes())
        self.assertEqual(0o700, output.stat().st_mode & 0o777)
        self.assertEqual(0o600, (output / 'server-state/.env').stat().st_mode & 0o777)
        self.assertFalse(list((self.work / 'backups').glob('.pending-*')))

    def test_explicit_quiesce_acknowledgement_and_native_recipient_required(self):
        with self.assertRaises(module.BackupError):
            module.backup(self.deployment, self.recipient, self.work / 'backups')
        with self.assertRaises(module.BackupError):
            module.backup(self.deployment, '1234ABCD', self.work / 'backups', quiesce=True)

    def test_failed_copy_restarts_api_without_publishing(self):
        self.fail_copy = True
        with self.assertRaises(module.BackupError):
            self.create()
        self.assertEqual(('start', 'api'), self.events[-1])
        self.assertFalse(list((self.work / 'backups').iterdir()))

    def test_failed_dump_is_not_accepted_even_if_age_encrypted_partial_bytes(self):
        self.fail_dump = True
        with self.assertRaises(module.BackupError):
            self.create()
        self.assertEqual(('start', 'api'), self.events[-1])
        self.assertFalse(list((self.work / 'backups').iterdir()))

    def test_concurrent_backup_is_rejected_before_stopping_api(self):
        with module.deployment_lock(self.deployment):
            with self.assertRaises(module.BackupError):
                self.create()
        self.assertFalse(self.events)
        self.create()  # The lock is released, including across the failure above.

    def test_previously_stopped_api_is_not_started(self):
        self.running = False
        self.create()
        self.assertFalse(any(event[0] in ('start', 'stop') for event in self.events))

    def test_corrupt_ciphertext_with_updated_public_hash_still_fails_age(self):
        backup = self.create()
        file = backup / 'database.dump.age'
        content = bytearray(file.read_bytes())
        content[-8] ^= 1
        file.write_bytes(content)
        manifest = json.loads((backup / 'manifest.json').read_text())
        manifest['databaseSha256'] = manifest['files']['database.dump.age'] = module.digest(file)
        (backup / 'manifest.json').write_text(json.dumps(manifest))
        output = self.work / 'verified'
        with self.assertRaises(module.BackupError):
            module.verify(backup, self.identity, output)
        self.assertFalse(output.exists())
        self.assertFalse(list(self.work.glob('.verify-*')))

    def test_swapped_valid_dump_is_rejected_by_encrypted_state_binding(self):
        first = self.create()
        second = self.create()
        shutil.copyfile(second / 'database.dump.age', first / 'database.dump.age')
        manifest = json.loads((first / 'manifest.json').read_text())
        manifest['databaseSha256'] = manifest['files']['database.dump.age'] = module.digest(first / 'database.dump.age')
        (first / 'manifest.json').write_text(json.dumps(manifest))
        with self.assertRaises(module.BackupError):
            module.verify(first, self.identity, self.work / 'verified')
        self.assertFalse((self.work / 'verified').exists())

    def test_existing_output_is_preserved(self):
        backup = self.create()
        output = self.work / 'verified'
        output.mkdir()
        (output / 'keep').write_text('keep')
        with self.assertRaises(module.BackupError):
            module.verify(backup, self.identity, output)
        self.assertEqual('keep', (output / 'keep').read_text())

    def test_tar_traversal_links_and_duplicate_names_fail_before_extraction(self):
        metadata = {'format': 1, 'id': 'id'}
        for name, link, duplicate in [('../escape', False, False), ('protection/link', True, False), ('.env', False, True)]:
            with self.subTest(name=name, link=link, duplicate=duplicate):
                memory = io.BytesIO()
                with tarfile.open(fileobj=memory, mode='w') as archive:
                    items = [('.env', b'env'), ('metadata.json', json.dumps(metadata).encode()), (name, b'value')]
                    for path, value in items:
                        item = tarfile.TarInfo(path)
                        item.size = len(value)
                        if link and path == name:
                            item.type, item.linkname, item.size = tarfile.SYMTYPE, '/etc/passwd', 0
                        archive.addfile(item, io.BytesIO(value))
                file = self.work / 'unsafe.tar'
                file.write_bytes(memory.getvalue())
                with self.assertRaises(module.BackupError):
                    module.unpack_state(file, self.work / 'unpacked', metadata)
                self.assertFalse((self.work / 'unpacked').exists())
                self.assertFalse((self.work / 'escape').exists())

    def test_public_env_permissions_fail_and_api_is_restarted(self):
        (self.deployment / '.env').chmod(0o644)
        with self.assertRaises(module.BackupError):
            self.create()
        self.assertEqual(('start', 'api'), self.events[-1])
        self.assertFalse(list((self.work / 'backups').iterdir()))

    def test_decryption_output_limit_is_enforced_without_publication(self):
        file = self.work / 'small.age'
        module.encrypt_bytes(b'x' * 4096, file, self.recipient)
        with self.assertRaises(module.BackupError):
            module.decrypt(file, self.work / 'limited', self.identity, 100)


if __name__ == '__main__':
    unittest.main()
