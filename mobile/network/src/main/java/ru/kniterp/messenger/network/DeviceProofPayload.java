package ru.kniterp.messenger.network;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.time.Instant;
import java.util.Base64;
import java.util.Objects;
import java.util.UUID;

/** Canonical bytes for server AuthCrypto.DeviceProof. Contains only public values.
 * Signing is delegated to Android Keystore; Signal private keys are never used here.
 */
public final class DeviceProofPayload {
    private DeviceProofPayload() { }

    public static byte[] activation(UUID challengeId, byte[] nonce, Instant expiresAt,
            String phone, UUID deviceId, int registrationId, byte[] identityKey, byte[] authKeySpki) {
        if (phone == null || !phone.matches("\\+[1-9][0-9]{1,14}"))
            throw new IllegalArgumentException("Expected normalized E.164 phone");
        return payload("activate", challengeId, nonce, expiresAt, phone, "-", "-",
                deviceId, registrationId, identityKey, authKeySpki);
    }

    public static byte[] refresh(UUID challengeId, byte[] nonce, Instant expiresAt,
            UUID accountId, UUID refreshTokenId, UUID deviceId, int registrationId,
            byte[] identityKey, byte[] authKeySpki) {
        return payload("refresh", challengeId, nonce, expiresAt, "-", id(accountId), id(refreshTokenId),
                deviceId, registrationId, identityKey, authKeySpki);
    }

    private static byte[] payload(String operation, UUID challengeId, byte[] nonce, Instant expiresAt,
            String phone, String account, String token, UUID deviceId, int registrationId,
            byte[] identityKey, byte[] authKeySpki) {
        Objects.requireNonNull(expiresAt, "expiresAt");
        if (nonce == null || nonce.length != 32) throw new IllegalArgumentException("Expected 32-byte nonce");
        if (identityKey == null || identityKey.length != 33 || identityKey[0] != 5)
            throw new IllegalArgumentException("Expected serialized Signal identity public key");
        if (registrationId < 1 || registrationId > 0x3fff)
            throw new IllegalArgumentException("Invalid Signal registration ID");
        if (authKeySpki == null || authKeySpki.length == 0 || authKeySpki.length > 256)
            throw new IllegalArgumentException("Invalid auth public key size");
        if (expiresAt.getEpochSecond() < 0) throw new IllegalArgumentException("Invalid expiry");
        String value = "kniterp-device-proof-v1\n"
                + "op:" + operation + "\n"
                + "challenge:" + id(challengeId) + "\n"
                + "nonce:" + b64url(nonce) + "\n"
                + "expires:" + expiresAt.getEpochSecond() + "\n"
                + "phone:" + phone + "\n"
                + "account:" + account + "\n"
                + "token:" + token + "\n"
                + "device:" + id(deviceId) + "\n"
                + "registration:" + registrationId + "\n"
                + "identity:" + b64url(identityKey) + "\n"
                + "authkey:" + b64url(sha256(authKeySpki)) + "\n";
        return value.getBytes(StandardCharsets.US_ASCII);
    }

    private static String id(UUID value) {
        Objects.requireNonNull(value, "UUID");
        if (value.getMostSignificantBits() == 0 && value.getLeastSignificantBits() == 0)
            throw new IllegalArgumentException("Expected nonzero UUID");
        return value.toString();
    }

    private static String b64url(byte[] value) {
        return Base64.getUrlEncoder().withoutPadding().encodeToString(value);
    }

    private static byte[] sha256(byte[] value) {
        try { return MessageDigest.getInstance("SHA-256").digest(value); }
        catch (NoSuchAlgorithmException impossible) { throw new AssertionError(impossible); }
    }
}
