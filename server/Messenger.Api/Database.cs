using Microsoft.EntityFrameworkCore;

namespace Messenger.Api;

public sealed class Account
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public int Generation { get; set; } = 1;
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

public sealed class MessengerDb(DbContextOptions<MessengerDb> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<PreKey> PreKeys => Set<PreKey>();
    public DbSet<KeyClaim> KeyClaims => Set<KeyClaim>();
    public DbSet<Envelope> Envelopes => Set<Envelope>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Block> Blocks => Set<Block>();

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
    }
}
