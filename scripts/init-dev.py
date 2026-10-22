#!/usr/bin/env python3
"""Create local-only random development credentials. Never print their values."""
import base64
import os
from pathlib import Path
import secrets

root = Path(__file__).resolve().parents[1]
path = root / '.env'
password = secrets.token_urlsafe(32)
content = '\n'.join([
    'ASPNETCORE_ENVIRONMENT=Development',
    'ASPNETCORE_URLS=http://127.0.0.1:5080',
    'POSTGRES_PASSWORD=' + password,
    'ConnectionStrings__Messenger=Host=db;Port=5432;Database=messenger;Username=messenger;Password=' + password,
    'Auth__SigningKey=' + base64.b64encode(secrets.token_bytes(48)).decode(),
    'Dev__ProvisioningKey=' + secrets.token_urlsafe(48),
    'Dev__AutoMigrate=true',
    'Auth__PhoneIndexKey=' + base64.b64encode(secrets.token_bytes(32)).decode(),
    'Auth__SmsCodeKey=' + base64.b64encode(secrets.token_bytes(32)).decode(),
    'Auth__PinPepper=' + base64.b64encode(secrets.token_bytes(32)).decode(),
    # Development never sends paid SMS. SMS.ru is configured only on a separately approved stand.
    'Auth__Sms__Provider=Fake',
]) + '\n'
try:
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
except FileExistsError:
    raise SystemExit('.env already exists. Existing credentials were preserved.')
with os.fdopen(fd, 'w', encoding='utf-8') as stream:
    stream.write(content)
print('Created local .env with random credentials. Keep this file private.')
