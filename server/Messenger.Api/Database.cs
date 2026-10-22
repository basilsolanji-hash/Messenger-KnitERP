using Microsoft.EntityFrameworkCore;

namespace Messenger.Api;

public sealed class Account
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public int Generation { get; set; } = 1;
    public byte[]? PhoneIndex { get; set; } // HMAC of E.164 with a server secret. The number itself is not stored.
    public string? PinHash { get; set; }
    public int PinFailures { get; set; }
    public DateTimeOffset? PinLockedUntil { get; set; }
}
public sealed class Device
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public int Generation { get; set; }
    public bool Active { get; set; } = true;
    public int RegistrationId { get; set; }
    public byte[] IdentityKey { get; set; } = [];
    public int? SignedPreKeyId { get; set; }
    public byte[]? SignedPreKey { get; set; }
    public byte[]? Signature { get; set; }
    public byte[]? AuthKey { get; set; } // P-256 SubjectPublicKeyInfo of the device proof key. Null for synthetic dev devices.
}
public sealed class PreKey
{
    public Guid DeviceId { get; set; }
    public int Id { get; set; }
    public int Kind { get; set; } // 0 ECC, 1 PQ
    public byte[] PublicKey { get; set; } = [];
    public byte[]? Signature { get; set; }
    public bool Claimed { get; set; }
}
public sealed class KeyClaim
{
    public Guid CallerDeviceId { get; set; }
    public Guid ClaimId { get; set; }
    public Guid RecipientDeviceId { get; set; }
    public string ResponseJson { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}
public sealed class Envelope
{
    public long Sequence { get; set; }
    public Guid ClientMessageId { get; set; }
    public Guid SenderAccountId { get; set; }
    public Guid SenderDeviceId { get; set; }
    public int SenderGeneration { get; set; }
    public Guid RecipientAccountId { get; set; }
    public Guid RecipientDeviceId { get; set; }
    public int RecipientGeneration { get; set; }
    public int MessageType { get; set; }
    public byte[]? Ciphertext { get; set; }
    public byte[] RequestHash { get; set; } = [];
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset RetainUntil { get; set; }
    public bool Acknowledged { get; set; }
}
public sealed class Notification
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempts { get; set; }
}
public sealed class Block
{
    public Guid OwnerId { get; set; }
    public Guid TargetId { get; set; }
}

public enum ChallengeOperation { Activate = 1, Refresh = 2 }
public enum ChallengeState { Sending = 0, Sent = 1, DeliveryUnknown = 2, ProviderRejected = 3, CodeVerified = 4, Completed = 5, Failed = 6, Superseded = 7 }
public sealed class AuthChallenge
{
    public Guid Id { get; set; }
    public Guid? RequestId { get; set; }
    public byte[]? RequestHash { get; set; }
    public ChallengeOperation Operation { get; set; }
    public ChallengeState State { get; set; }
    public byte[]? PhoneIndex { get; set; }
    public string? Region { get; set; }
    public byte[]? CodeVerifier { get; set; }
    public int Attempts { get; set; }
    public Guid DeviceId { get; set; }
    public int RegistrationId { get; set; }
    public byte[] IdentityKey { get; set; } = [];
    public byte[] AuthKey { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
public sealed class AuthCounter
{
    public string Key { get; set; } = "";
    public DateTimeOffset WindowEnd { get; set; }
    public long Count { get; set; }
}
public sealed class RefreshFamily
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AuthenticatedAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
}
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public byte[] Verifier { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public Guid? ConsumedByChallenge { get; set; }
    public Guid? ReplacedBy { get; set; }
}

public sealed class MessengerDb(DbContextOptions<MessengerDb> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<PreKey> PreKeys => Set<PreKey>();
    public DbSet<KeyClaim> KeyClaims => Set<KeyClaim>();
    public DbSet<Envelope> Envelopes => Set<Envelope>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Block> Blocks => Set<Block>();
    public DbSet<AuthChallenge> AuthChallenges => Set<AuthChallenge>();
    public DbSet<AuthCounter> AuthCounters => Set<AuthCounter>();
    public DbSet<RefreshFamily> RefreshFamilies => Set<RefreshFamily>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Account>().ToTable("accounts").HasKey(x => x.Id);
        m.Entity<Account>().Property(x => x.DisplayName).HasMaxLength(128);
        m.Entity<Device>().ToTable("devices").HasKey(x => x.Id);
        m.Entity<Device>().HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        m.Entity<Device>().HasIndex(x => x.AccountId).IsUnique().HasFilter("\"Active\" = TRUE");
        m.Entity<PreKey>().ToTable("prekeys").HasKey(x => new { x.DeviceId, x.Kind, x.Id });
        m.Entity<PreKey>().HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId);
        m.Entity<KeyClaim>().ToTable("key_claims").HasKey(x => new { x.CallerDeviceId, x.ClaimId });
        m.Entity<KeyClaim>().Property(x => x.ResponseJson).HasColumnType("jsonb");
        m.Entity<Envelope>().ToTable("envelopes").HasKey(x => x.Sequence);
        m.Entity<Envelope>().Property(x => x.Sequence).UseIdentityByDefaultColumn();
        m.Entity<Envelope>().HasIndex(x => new { x.SenderDeviceId, x.ClientMessageId }).IsUnique();
        m.Entity<Envelope>().HasIndex(x => new { x.RecipientDeviceId, x.Sequence });
        m.Entity<Envelope>().HasOne<Device>().WithMany().HasForeignKey(x => x.SenderDeviceId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<Envelope>().HasOne<Device>().WithMany().HasForeignKey(x => x.RecipientDeviceId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<Notification>().ToTable("notifications").HasKey(x => x.Id);
        m.Entity<Notification>().HasIndex(x => x.NextAttemptAt);
        m.Entity<Block>().ToTable("blocks").HasKey(x => new { x.OwnerId, x.TargetId });
        m.Entity<Account>().HasIndex(x => x.PhoneIndex).IsUnique().HasFilter("\"PhoneIndex\" IS NOT NULL");
        m.Entity<Account>().Property(x => x.PinHash).HasMaxLength(256);
        m.Entity<AuthChallenge>().ToTable("auth_challenges").HasKey(x => x.Id);
        m.Entity<AuthChallenge>().HasIndex(x => x.RequestId).IsUnique().HasFilter("\"RequestId\" IS NOT NULL");
        m.Entity<AuthChallenge>().HasIndex(x => new { x.PhoneIndex, x.CreatedAt });
        m.Entity<AuthChallenge>().HasIndex(x => new { x.State, x.CreatedAt });
        m.Entity<AuthChallenge>().HasIndex(x => x.ExpiresAt);
        m.Entity<AuthChallenge>().Property(x => x.Region).HasMaxLength(2);
        m.Entity<AuthCounter>().ToTable("auth_counters").HasKey(x => x.Key);
        m.Entity<AuthCounter>().Property(x => x.Key).HasMaxLength(160);
        m.Entity<AuthCounter>().HasIndex(x => x.WindowEnd);
        m.Entity<RefreshFamily>().ToTable("refresh_families").HasKey(x => x.Id);
        m.Entity<RefreshFamily>().HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId);
        m.Entity<RefreshFamily>().HasIndex(x => x.AccountId);
        m.Entity<RefreshFamily>().Property(x => x.RevokedReason).HasMaxLength(32);
        m.Entity<RefreshToken>().ToTable("refresh_tokens").HasKey(x => x.Id);
        m.Entity<RefreshToken>().HasOne<RefreshFamily>().WithMany().HasForeignKey(x => x.FamilyId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<RefreshToken>().HasIndex(x => x.FamilyId);
        m.Entity<RefreshToken>().HasIndex(x => x.ExpiresAt);
    }
}
