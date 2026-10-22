#!/usr/bin/env python3
"""Compile the real JVM payload builder and check it against Claude's server fixture.

No Gradle, Android SDK, external packages, network or real credentials required.
This verifies JCA/DER interoperability, not Android Keystore or HTTP authentication.
"""
import base64
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile


def main():
    root = Path(__file__).resolve().parents[1]
    fixture = json.loads((root / "docs/fixtures/device-proof-v1.json").read_text())
    value = fixture["input"]
    source = root / "mobile/network/src/main/java/ru/kniterp/messenger/network/DeviceProofPayload.java"
    auth_hash = base64.urlsafe_b64encode(hashlib.sha256(base64.b64decode(value["authKeySpkiBase64"])).digest()).decode().rstrip("=")
    account = "a1b2c3d4-0000-4000-8000-001122334455"
    token = "b1c2d3e4-0000-4000-8000-001122334455"
    refresh_payload = fixture["payload"].replace("op:activate\n", "op:refresh\n").replace(
        "phone:" + value["phone"] + "\naccount:-\ntoken:-\n",
        f"phone:-\naccount:{account}\ntoken:{token}\n")
    assert "authkey:" + auth_hash + "\n" in refresh_payload
    constants = {
        "PHONE": value["phone"], "CHALLENGE": value["challengeId"], "DEVICE": value["deviceId"],
        "NONCE": value["nonceBase64"], "EXPIRY": value["expiresAt"], "IDENTITY": value["identityKeyBase64"],
        "AUTH": value["authKeySpkiBase64"], "PAYLOAD": fixture["payload"], "HASH": fixture["payloadSha256Hex"],
        "SIGNATURE": fixture["signatureDerBase64"], "TEST_PRIVATE": fixture["testOnlyPrivateKeyPkcs8Base64"],
        "ACCOUNT": account, "TOKEN": token, "REFRESH": refresh_payload,
    }
    declarations = "\n".join(f"static final String {name} = {json.dumps(text)};" for name, text in constants.items())
    harness = r'''
import ru.kniterp.messenger.network.DeviceProofPayload;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.spec.*;
import java.time.Instant;
import java.util.*;

public class DeviceProofContractCheck {
    CONSTANTS
    static int checks;
    static byte[] bytes(String value) { return Base64.getDecoder().decode(value); }
    static UUID id(String value) { return UUID.fromString(value); }
    static byte[] changed(byte[] value) { byte[] result = value.clone(); result[result.length - 1] ^= 1; return result; }
    static void check(boolean pass, String reason) { if (!pass) throw new AssertionError(reason); checks++; }
    static void rejects(Runnable action) {
        try { action.run(); throw new AssertionError("Malformed input accepted"); }
        catch (IllegalArgumentException expected) { checks++; }
    }
    static byte[] activation(UUID challenge, byte[] nonce, Instant expires, String phone, UUID device,
            int registration, byte[] identity, byte[] auth) {
        return DeviceProofPayload.activation(challenge, nonce, expires, phone, device, registration, identity, auth);
    }
    static boolean verifies(byte[] payload, byte[] signature) throws Exception {
        Signature verifier = Signature.getInstance("SHA256withECDSA");
        verifier.initVerify(KeyFactory.getInstance("EC").generatePublic(new X509EncodedKeySpec(bytes(AUTH))));
        verifier.update(payload);
        return verifier.verify(signature);
    }
    public static void main(String[] args) throws Exception {
        UUID challenge = id(CHALLENGE), device = id(DEVICE);
        Instant expires = Instant.parse(EXPIRY);
        byte[] nonce = bytes(NONCE), identity = bytes(IDENTITY), auth = bytes(AUTH);
        byte[] payload = activation(challenge, nonce, expires, PHONE, device, REGISTRATION, identity, auth);
        check(Arrays.equals(payload, PAYLOAD.getBytes(StandardCharsets.US_ASCII)), "Server fixture payload differs");
        check(HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(payload)).equals(HASH), "Server fixture digest differs");
        check(verifies(payload, bytes(SIGNATURE)), "OpenSSL/server fixture signature does not verify in JCA");
        Signature signer = Signature.getInstance("SHA256withECDSA");
        signer.initSign(KeyFactory.getInstance("EC").generatePrivate(new PKCS8EncodedKeySpec(bytes(TEST_PRIVATE))));
        signer.update(payload);
        check(verifies(payload, signer.sign()), "JCA DER signature cannot be verified");
        check(Arrays.equals(payload, activation(challenge, nonce, expires.plusNanos(999999999), PHONE,
                device, REGISTRATION, identity, auth)), "Expiry must truncate to Unix seconds");
        check(Arrays.equals(payload, activation(id(CHALLENGE.toUpperCase(Locale.ROOT)), nonce, expires, PHONE,
                id(DEVICE.toUpperCase(Locale.ROOT)), REGISTRATION, identity, auth)), "UUID canonicalization differs");
        byte[] refresh = DeviceProofPayload.refresh(challenge, nonce, expires, id(ACCOUNT), id(TOKEN),
                device, REGISTRATION, identity, auth);
        check(Arrays.equals(refresh, REFRESH.getBytes(StandardCharsets.US_ASCII)), "Refresh payload differs");
        byte[][] mutations = {
            activation(UUID.randomUUID(), nonce, expires, PHONE, device, REGISTRATION, identity, auth),
            activation(challenge, changed(nonce), expires, PHONE, device, REGISTRATION, identity, auth),
            activation(challenge, nonce, expires.plusSeconds(1), PHONE, device, REGISTRATION, identity, auth),
            activation(challenge, nonce, expires, "+77012345678", device, REGISTRATION, identity, auth),
            activation(challenge, nonce, expires, PHONE, UUID.randomUUID(), REGISTRATION, identity, auth),
            activation(challenge, nonce, expires, PHONE, device, REGISTRATION + 1, identity, auth),
            activation(challenge, nonce, expires, PHONE, device, REGISTRATION, changed(identity), auth),
            activation(challenge, nonce, expires, PHONE, device, REGISTRATION, identity, changed(auth)),
            refresh
        };
        for (byte[] mutation : mutations) check(!verifies(mutation, bytes(SIGNATURE)), "Proof did not bind a field");
        check(!Arrays.equals(refresh, DeviceProofPayload.refresh(challenge, nonce, expires, UUID.randomUUID(),
                id(TOKEN), device, REGISTRATION, identity, auth)), "Refresh does not bind account");
        check(!Arrays.equals(refresh, DeviceProofPayload.refresh(challenge, nonce, expires, id(ACCOUNT),
                UUID.randomUUID(), device, REGISTRATION, identity, auth)), "Refresh does not bind token");
        for (String badPhone : new String[] {"79001234567", "+7 9001234567", "+0123", "+1\nop:refresh", "+1234567890123456"})
            rejects(() -> activation(challenge, nonce, expires, badPhone, device, REGISTRATION, identity, auth));
        rejects(() -> activation(challenge, new byte[31], expires, PHONE, device, REGISTRATION, identity, auth));
        rejects(() -> activation(challenge, nonce, expires, PHONE, device, 0, identity, auth));
        rejects(() -> activation(challenge, nonce, expires, PHONE, device, 16384, identity, auth));
        rejects(() -> activation(challenge, nonce, expires, PHONE, device, REGISTRATION, new byte[33], auth));
        rejects(() -> activation(challenge, nonce, expires, PHONE, device, REGISTRATION, new byte[32], auth));
        rejects(() -> activation(challenge, nonce, expires, PHONE, device, REGISTRATION, identity, new byte[257]));
        rejects(() -> activation(challenge, nonce, expires, PHONE, device, REGISTRATION, identity, new byte[0]));
        rejects(() -> activation(new UUID(0, 0), nonce, expires, PHONE, device, REGISTRATION, identity, auth));
        rejects(() -> activation(challenge, nonce, expires, PHONE, new UUID(0, 0), REGISTRATION, identity, auth));
        rejects(() -> activation(challenge, nonce, Instant.ofEpochSecond(-1), PHONE, device, REGISTRATION, identity, auth));
        System.out.println("Device proof JVM contract: " + checks + " checks passed");
    }
}
'''.replace("CONSTANTS", declarations).replace("REGISTRATION", str(value["registrationId"]))
    java = shutil.which("java")
    if java is None:
        raise SystemExit("A JDK with the jdk.compiler module is required")
    with tempfile.TemporaryDirectory(prefix="kniterp-device-proof-") as temporary:
        temp = Path(temporary)
        test = temp / "DeviceProofContractCheck.java"
        test.write_text(harness)
        subprocess.run([java, "com.sun.tools.javac.Main", "--release", "17", "-d", str(temp), str(source), str(test)], check=True)
        subprocess.run([java, "-cp", str(temp), "DeviceProofContractCheck"], check=True)


if __name__ == "__main__":
    main()
