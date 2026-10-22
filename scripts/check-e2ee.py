#!/usr/bin/env python3
"""Run the JVM libsignal clients against a disposable loopback ASP.NET/PostgreSQL server.

Requires TEST_DATABASE pointing to a dedicated test PostgreSQL and a built Release API.
No SMS, production credentials, public listeners or private key export are involved.
"""
import base64
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.request


def main():
    root = Path(__file__).resolve().parents[1]
    database = os.environ.get("TEST_DATABASE", "")
    entries = dict(part.strip().lower().split("=", 1) for part in database.split(";") if "=" in part)
    if entries.get("host") not in {"127.0.0.1", "localhost"}:
        raise SystemExit("TEST_DATABASE must point to a dedicated loopback PostgreSQL test database.")
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    url = f"http://127.0.0.1:{port}"
    with tempfile.TemporaryDirectory(prefix="kniterp-e2ee-") as directory:
        env = os.environ.copy()
        env.update({"ASPNETCORE_ENVIRONMENT": "Development", "ASPNETCORE_URLS": url,
                    "ConnectionStrings__Messenger": database, "Dev__AutoMigrate": "true",
                    "Dev__ProvisioningKey": secrets.token_urlsafe(48),
                    "Auth__SigningKey": base64.b64encode(secrets.token_bytes(32)).decode(),
                    "Dev__DataProtectionPath": str(Path(directory) / "protection")})
        output = Path(directory) / "server.log"
        with output.open("wb") as log:
            server = subprocess.Popen(["dotnet", str(root / "server/Messenger.Api/bin/Release/net10.0/Messenger.Api.dll")],
                                      cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT)
            try:
                opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
                deadline = time.monotonic() + 60
                while True:
                    if server.poll() is not None:
                        raise RuntimeError("Development API stopped before becoming ready.")
                    try:
                        with opener.open(url + "/health/ready", timeout=1) as response:
                            if response.status == 200:
                                break
                    except OSError:
                        if time.monotonic() >= deadline:
                            raise RuntimeError("Development API did not become ready within 60 seconds.") from None
                        time.sleep(0.25)
                test_env = os.environ.copy()
                test_env.update({"MESSENGER_TEST_URL": url, "DEV_PROVISIONING_KEY": env["Dev__ProvisioningKey"]})
                wrapper = "gradlew.bat" if os.name == "nt" else "./gradlew"
                subprocess.run([wrapper, ":core:serverSmoke", ":network:serverSmoke", "--no-daemon"], cwd=root / "mobile", env=test_env, check=True)
                print("PASS: libsignal -> HTTP -> PostgreSQL -> offline inbox -> decrypt -> ACK -> encrypted reply")
            finally:
                server.terminate()
                try:
                    server.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    server.kill()
                    server.wait()


if __name__ == "__main__":
    main()
