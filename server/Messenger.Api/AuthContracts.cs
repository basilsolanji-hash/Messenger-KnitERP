namespace Messenger.Api;

// Public auth API v1. Field names are the wire contract in docs/API_AUTH.md.
public sealed record SmsChallengeRequest(Guid RequestId, string Phone, Guid DeviceId, int RegistrationId,
    byte[] IdentityKey, byte[] AuthKey);
public sealed record ChallengeResponse(Guid ChallengeId, byte[] Nonce, DateTimeOffset ExpiresAt,
    DateTimeOffset? ResendAfter, int CodeLength, int MaxAttempts);
public sealed record SmsVerifyRequest(Guid ChallengeId, string Phone, string Code, byte[] Signature);
public sealed record PinVerifyRequest(Guid ChallengeId, string Phone, string Pin, byte[] Signature);
public sealed record ActivationResponse(string Status, AuthSession? Session, bool RecoveryAvailable);
public sealed record AuthSession(Guid AccountId, Guid DeviceId, int Generation, string AccessToken,
    DateTimeOffset ExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt, bool NewAccount);
public sealed record RefreshChallengeRequest(Guid DeviceId);
public sealed record RefreshRequest(string RefreshToken, Guid ChallengeId, byte[] Signature);
public sealed record SetPinRequest(string Pin, string? CurrentPin);
public sealed record DisablePinRequest(string CurrentPin);
