using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Api;

public sealed class Transport(MessengerDb db, TimeProvider clock)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static void Require(bool condition, string code = "INVALID_REQUEST", int status = 400)
    {
        if (!condition) throw new ApiFault(status, code);
    }

    // All mutations affecting delivery take these locks in the same order, including device revocation.
    private async Task LockDevices(Guid[] ids, CancellationToken ct)
    {
        foreach (var id in ids.Distinct().OrderBy(x => x))
            await db.Devices.FromSqlInterpolated($"SELECT * FROM devices WHERE \"Id\" = {id} FOR UPDATE")
                .AsNoTracking().ToListAsync(ct);
    }

    public async Task Publish(Actor actor, PublishBundleRequest request, CancellationToken ct)
    {
        Require(request.SignedPreKeyId > 0 && request.SignedPreKeyId <= 0xffffff && request.SignedPreKey is { Length: 33 } && request.Signature is { Length: 64 });
        Require(request.PreKeys is { Length: > 0 and <= 100 } && request.PqPreKeys is { Length: > 0 and <= 100 });
        Require(request.PreKeys.All(k => k.Id is > 0 and <= 0xffffff && k.PublicKey is { Length: 33 }));
        Require(request.PqPreKeys.All(k => k.Id is > 0 and <= 0xffffff && k.PublicKey is { Length: >= 1000 and <= 4096 } && k.Signature is { Length: 64 }));
        Require(request.PreKeys.Select(x => x.Id).Distinct().Count() == request.PreKeys.Length);
        Require(request.PqPreKeys.Select(x => x.Id).Distinct().Count() == request.PqPreKeys.Length);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockDevices([actor.DeviceId], ct);
        Require(await Access.IsActive(db, actor, ct), "DEVICE_REVOKED", 401);
        var device = await db.Devices.SingleAsync(x => x.Id == actor.DeviceId, ct);
        // Stage 0 immutable signed key. Rotation gets its own versioned contract in stage 1.
        if (device.SignedPreKeyId is not null)
            Require(device.SignedPreKeyId == request.SignedPreKeyId && device.SignedPreKey!.SequenceEqual(request.SignedPreKey) && device.Signature!.SequenceEqual(request.Signature), "KEY_ROTATION_NOT_AVAILABLE", 409);
        device.SignedPreKeyId = request.SignedPreKeyId;
        device.SignedPreKey = request.SignedPreKey;
        device.Signature = request.Signature;
        foreach (var entry in request.PreKeys.Select(x => new PreKey { DeviceId = device.Id, Kind = 0, Id = x.Id, PublicKey = x.PublicKey })
            .Concat(request.PqPreKeys.Select(x => new PreKey { DeviceId = device.Id, Kind = 1, Id = x.Id, PublicKey = x.PublicKey, Signature = x.Signature })))
        {
            var existing = await db.PreKeys.FindAsync([entry.DeviceId, entry.Kind, entry.Id], ct);
            if (existing is null) db.PreKeys.Add(entry);
            else Require(existing.PublicKey.SequenceEqual(entry.PublicKey) &&
                (existing.Signature ?? []).SequenceEqual(entry.Signature ?? []), "PREKEY_ID_REUSE", 409);
            // Reuploading a consumed key must never make it available again.
        }
        Require(await db.PreKeys.CountAsync(x => x.DeviceId == device.Id, ct) + db.ChangeTracker.Entries<PreKey>().Count(x => x.State == EntityState.Added) <= 2000, "KEY_STORAGE_LIMIT", 409);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<ClaimedBundle> Claim(Actor actor, ClaimRequest request, CancellationToken ct)
    {
        Require(request.AccountId != Guid.Empty && request.ClaimId != Guid.Empty);
        var targetId = await db.Devices.Where(x => x.AccountId == request.AccountId && x.Active).Select(x => x.Id).SingleOrDefaultAsync(ct);
        Require(targetId != Guid.Empty, "NOT_FOUND", 404);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockDevices([actor.DeviceId, targetId], ct);
        Require(await Access.IsActive(db, actor, ct), "DEVICE_REVOKED", 401);
        Require(!await db.Blocks.AnyAsync(x => (x.OwnerId == actor.AccountId && x.TargetId == request.AccountId) || (x.OwnerId == request.AccountId && x.TargetId == actor.AccountId), ct), "NOT_FOUND", 404);
        var device = await db.Devices.SingleAsync(x => x.Id == targetId, ct);
        Require(device.Active && device.SignedPreKeyId.HasValue, "KEYS_UNAVAILABLE", 409);
        var prior = await db.KeyClaims.FindAsync([actor.DeviceId, request.ClaimId], ct);
        if (prior is not null)
        {
            Require(prior.RecipientDeviceId == targetId && prior.ExpiresAt > clock.GetUtcNow(), "CLAIM_CONFLICT", 409);
            return JsonSerializer.Deserialize<ClaimedBundle>(prior.ResponseJson, Json)!;
        }
        var pre = await db.PreKeys.Where(x => x.DeviceId == targetId && x.Kind == 0 && !x.Claimed).OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        var pq = await db.PreKeys.Where(x => x.DeviceId == targetId && x.Kind == 1 && !x.Claimed).OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        Require(pre is not null && pq is not null, "PREKEYS_EXHAUSTED", 409);
        pre!.Claimed = pq!.Claimed = true;
        var bundle = new ClaimedBundle(device.AccountId, device.Id, device.Generation, 1, device.RegistrationId,
            device.IdentityKey, device.SignedPreKeyId!.Value, device.SignedPreKey!, device.Signature!,
            new(pre.Id, pre.PublicKey), new(pq.Id, pq.PublicKey, pq.Signature!));
        db.KeyClaims.Add(new() { CallerDeviceId = actor.DeviceId, ClaimId = request.ClaimId, RecipientDeviceId = targetId,
            ResponseJson = JsonSerializer.Serialize(bundle, Json), ExpiresAt = clock.GetUtcNow().AddDays(1) });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return bundle;
    }

    public async Task<AcceptedMessage> Send(Actor actor, SendRequest request, CancellationToken ct)
    {
        Require(request.ClientMessageId != Guid.Empty && request.RecipientDeviceId != Guid.Empty && request.RecipientAccountId != Guid.Empty);
        Require(request.MessageType is 2 or 3 && request.Ciphertext is { Length: > 0 and <= 65536 });
        var digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Json));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockDevices([actor.DeviceId, request.RecipientDeviceId], ct);
        Require(await Access.IsActive(db, actor, ct), "DEVICE_REVOKED", 401);
        var prior = await db.Envelopes.SingleOrDefaultAsync(x => x.SenderDeviceId == actor.DeviceId && x.ClientMessageId == request.ClientMessageId, ct);
        if (prior is not null)
        {
            Require(CryptographicOperations.FixedTimeEquals(prior.RequestHash, digest), "IDEMPOTENCY_CONFLICT", 409);
            return new(prior.ClientMessageId, prior.Sequence, prior.AcceptedAt, prior.ExpiresAt);
        }
        Require(await db.Devices.AnyAsync(x => x.Id == request.RecipientDeviceId && x.AccountId == request.RecipientAccountId &&
            x.Generation == request.RecipientGeneration && x.Active, ct), "RECIPIENT_DEVICE_CHANGED", 409);
        Require(!await db.Blocks.AnyAsync(x => (x.OwnerId == actor.AccountId && x.TargetId == request.RecipientAccountId) ||
            (x.OwnerId == request.RecipientAccountId && x.TargetId == actor.AccountId), ct), "DELIVERY_UNAVAILABLE", 403);
        var now = clock.GetUtcNow();
        // PostgreSQL timestamps have microsecond precision. Persist and return the same value
        // on the first acceptance and on an idempotent retry loaded from the database.
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        var queue = db.Envelopes.Where(x => x.RecipientDeviceId == request.RecipientDeviceId && !x.Acknowledged && x.ExpiresAt > now && x.Ciphertext != null);
        Require(await queue.CountAsync(ct) < 10000 && (await queue.SumAsync(x => (long?)x.Ciphertext!.Length, ct) ?? 0) + request.Ciphertext.Length <= 100L * 1024 * 1024, "QUOTA_EXCEEDED", 409);
        var message = new Envelope { ClientMessageId = request.ClientMessageId, SenderAccountId = actor.AccountId,
            SenderDeviceId = actor.DeviceId, SenderGeneration = actor.Generation, RecipientAccountId = request.RecipientAccountId,
            RecipientDeviceId = request.RecipientDeviceId, RecipientGeneration = request.RecipientGeneration,
            Ciphertext = request.Ciphertext, MessageType = request.MessageType, RequestHash = digest,
            AcceptedAt = now, ExpiresAt = now.AddDays(30), RetainUntil = now.AddDays(60) };
        db.Envelopes.Add(message);
        db.Notifications.Add(new() { DeviceId = request.RecipientDeviceId, NextAttemptAt = now, ExpiresAt = message.ExpiresAt });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(message.ClientMessageId, message.Sequence, message.AcceptedAt, message.ExpiresAt);
    }

    public async Task<InboxItem[]> Inbox(Actor actor, long after, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var batch = await db.Envelopes.AsNoTracking().Where(x => x.RecipientDeviceId == actor.DeviceId &&
            x.RecipientGeneration == actor.Generation && x.Sequence > after && !x.Acknowledged && x.Ciphertext != null && x.ExpiresAt > now)
            .OrderBy(x => x.Sequence).Take(100).ToArrayAsync(ct);
        List<InboxItem> items = [];
        var bytes = 0;
        foreach (var x in batch)
        {
            bytes += x.Ciphertext!.Length;
            if (bytes > 700 * 1024) break; // Base64 + metadata stays below 1 MiB.
            items.Add(new(x.Sequence, x.ClientMessageId, x.SenderAccountId, x.SenderDeviceId, x.SenderGeneration,
                x.MessageType, x.Ciphertext, x.AcceptedAt, x.ExpiresAt));
        }
        return items.ToArray();
    }

    public async Task Ack(Actor actor, AckRequest request, CancellationToken ct)
    {
        Require(request.Sequences is { Length: > 0 and <= 100 } && request.Sequences.All(x => x > 0));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockDevices([actor.DeviceId], ct);
        Require(await Access.IsActive(db, actor, ct), "DEVICE_REVOKED", 401);
        var ids = request.Sequences.Distinct().ToArray();
        var rows = await db.Envelopes.Where(x => ids.Contains(x.Sequence) && x.RecipientDeviceId == actor.DeviceId && x.RecipientGeneration == actor.Generation).ToArrayAsync(ct);
        Require(rows.Length == ids.Length, "NOT_FOUND", 404);
        foreach (var row in rows) { row.Ciphertext = null; row.Acknowledged = true; }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
