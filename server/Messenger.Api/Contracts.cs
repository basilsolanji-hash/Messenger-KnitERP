using System.Text.Json.Serialization;

namespace Messenger.Api;

public sealed record ProvisionRequest(string DisplayName, Guid DeviceId, int RegistrationId, byte[] IdentityKey);
public sealed record SessionResponse(Guid AccountId, Guid DeviceId, int Generation, string AccessToken, DateTimeOffset ExpiresAt);
public sealed record PublicPreKey(int Id, byte[] PublicKey);
public sealed record PublicPqPreKey(int Id, byte[] PublicKey, byte[] Signature);
public sealed record PublishBundleRequest(int SignedPreKeyId, byte[] SignedPreKey, byte[] Signature,
    PublicPreKey[] PreKeys, PublicPqPreKey[] PqPreKeys);
public sealed record ClaimRequest(Guid AccountId, Guid ClaimId);
public sealed record ClaimedBundle(Guid AccountId, Guid DeviceId, int Generation, int SignalDeviceId,
    int RegistrationId, byte[] IdentityKey, int SignedPreKeyId, byte[] SignedPreKey, byte[] Signature,
    PublicPreKey PreKey, PublicPqPreKey PqPreKey);
public sealed record SendRequest(Guid ClientMessageId, Guid RecipientAccountId, Guid RecipientDeviceId,
    int RecipientGeneration, int MessageType, byte[] Ciphertext);
public sealed record AcceptedMessage(Guid ClientMessageId, long Sequence, DateTimeOffset AcceptedAt, DateTimeOffset ExpiresAt);
public sealed record InboxItem(long Sequence, Guid ClientMessageId, Guid SenderAccountId, Guid SenderDeviceId,
    int SenderGeneration, int MessageType, byte[] Ciphertext, DateTimeOffset AcceptedAt, DateTimeOffset ExpiresAt);
public sealed record InboxPage(InboxItem[] Items, string? NextCursor);
public sealed record AckRequest(long[] Sequences);
public sealed record BlockRequest(Guid AccountId);
public sealed record Actor(Guid AccountId, Guid DeviceId, int Generation);

public sealed class ApiFault(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public TimeSpan? RetryAfter { get; init; }
    public int? AttemptsRemaining { get; init; }
}
