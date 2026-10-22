#!/usr/bin/env python3
"""Issue an operator-assisted synthetic phone session. Never prints credentials."""
import argparse
import base64
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import urllib.parse
import uuid


def canonical_id(value):
    if str(uuid.UUID(value)) != value or uuid.UUID(value).int == 0:
        raise ValueError("Expected a canonical nonzero UUID")
    return value


def validate(origin, pins, request):
    url = urllib.parse.urlsplit(origin)
    if (url.scheme != "https" or not url.hostname or url.username is not None or url.password is not None
            or url.path not in ("", "/") or url.query or url.fragment or any(ord(c) <= 32 for c in origin)):
        raise ValueError("Expected an HTTPS origin without credentials, path, query or fragment")
    _ = url.port
    if not isinstance(pins, list) or not 2 <= len(pins) <= 5 or len(set(pins)) != len(pins):
        raise ValueError("Supply distinct current and backup leaf SPKI pins")
    for pin in pins:
        if not isinstance(pin, str) or not pin.startswith("sha256/"):
            raise ValueError("Invalid SPKI pin")
        raw = base64.b64decode(pin[7:], validate=True)
        if len(raw) != 32 or base64.b64encode(raw).decode() != pin[7:]:
            raise ValueError("Invalid SPKI pin")
    if set(request) != {"displayName", "deviceId", "registrationId", "identityKey"}:
        raise ValueError("Unexpected enrollment fields")
    name = request["displayName"]
    if not isinstance(name, str) or not name.strip() or len(name) > 64 or any(ord(c) < 32 or 127 <= ord(c) <= 159 for c in name):
        raise ValueError("Invalid test name")
    canonical_id(request["deviceId"])
    if type(request["registrationId"]) is not int or not 1 <= request["registrationId"] <= 16383:
        raise ValueError("Invalid registration ID")
    key = base64.b64decode(request["identityKey"], validate=True)
    if len(key) != 33 or key[0] != 5 or base64.b64encode(key).decode() != request["identityKey"]:
        raise ValueError("Invalid public identity key")
    return origin.rstrip("/")


def read_json(path):
    data = Path(path).read_bytes()
    if len(data) > 16384:
        raise ValueError("Input exceeds 16 KiB")
    return json.loads(data)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--origin", required=True)
    parser.add_argument("--pins-file", required=True, help="JSON array: current and backup leaf SHA-256 SPKI pins")
    parser.add_argument("--request", required=True, help="Public enrollment JSON copied from the phone")
    parser.add_argument("--account-id", help="Renew the SAME phone and account; does not replace keys")
    parser.add_argument("--output", default=".secrets/phone.session.json")
    args = parser.parse_args()
    request, pins = read_json(args.request), read_json(args.pins_file)
    origin = validate(args.origin, pins, request)
    secret = os.environ.get("Dev__ProvisioningKey") or os.environ.get("DEV_PROVISIONING_KEY", "")
    if not re.fullmatch(r"[A-Za-z0-9_-]{40,256}", secret):
        raise ValueError("Set the operator provisioning secret in the environment")
    path = "/api/v1/dev/accounts"
    if args.account_id:
        path += "/" + canonical_id(args.account_id) + "/session"
    output = Path(args.output).resolve()
    if output.exists():
        raise ValueError("Refusing to overwrite an existing session file; choose a new --output")
    output.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    # Secret header travels over stdin, not the process argument list. No redirects,
    # no curlrc, no insecure TLS, no verbose output. Curl checks CA + hostname + leaf pin.
    curl_config = 'header = "X-Dev-Provisioning-Token: ' + secret + '"\n'
    with tempfile.TemporaryDirectory(prefix="kniterp-provision-") as directory:
        body, response = Path(directory) / "request.json", Path(directory) / "response.json"
        body.write_text(json.dumps(request, ensure_ascii=False), encoding="utf-8")
        body.chmod(0o600)
        command = ["curl", "--disable", "--config", "-", "--silent", "--show-error", "--fail",
                   "--proto", "=https", "--tlsv1.2", "--connect-timeout", "10", "--max-time", "40",
                   "--max-redirs", "0", "--max-filesize", "1048576", "--pinnedpubkey",
                   ";".join(pin.replace("sha256/", "sha256//", 1) for pin in pins),
                   "--header", "Content-Type: application/json", "--data-binary", "@" + str(body),
                   "--output", str(response), origin + path]
        result = subprocess.run(command, input=curl_config.encode(), stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=45)
        if result.returncode:
            raise RuntimeError("Session not issued: check TLS, pins, operator key and device binding. No credential file written.")
        data = response.read_bytes()
        if len(data) > 1048576:
            raise ValueError("Response exceeds limit")
        session = json.loads(data)
        canonical_id(session["accountId"])
        if args.account_id and session["accountId"] != args.account_id:
            raise ValueError("Account mismatch")
        if session["deviceId"] != request["deviceId"]:
            raise ValueError("Device mismatch")
        token = session["accessToken"]
        if not isinstance(token, str) or not re.fullmatch(r"[A-Za-z0-9_.-]{32,8192}", token):
            raise ValueError("Invalid access token")
        value = {"server": {"origin": origin, "pins": pins}, "accessToken": token}
        descriptor = os.open(output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            json.dump(value, handle, ensure_ascii=False)
            handle.write("\n")
    print("Saved an individual 15-minute session. Transfer the secret file only to its requesting phone.")
    print("Account ID:", session["accountId"])


if __name__ == "__main__":
    try:
        main()
    except (ValueError, TypeError, KeyError, OSError, RuntimeError, subprocess.TimeoutExpired) as error:
        # Never reflect server bodies, tokens, input JSON or curl diagnostics.
        raise SystemExit("Provisioning failed (" + type(error).__name__ + "). Check inputs and the private operator environment.") from None
