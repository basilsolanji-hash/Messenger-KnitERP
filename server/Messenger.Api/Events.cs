using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Api;

[Authorize]
public sealed class EventsHub(ConnectionRegistry registry, MessengerDb db, TimeProvider clock) : Hub
{
    public static string Group(Guid deviceId) => "device:" + deviceId;
    public override async Task OnConnectedAsync()
    {
        var actor = Access.Current(Context.User!);
        registry.Add(Context.ConnectionId, actor.DeviceId, Access.SessionId(Context.User!), Context.Abort);
        if (!await Access.IsSessionActive(db, Context.User!, clock.GetUtcNow(), Context.ConnectionAborted))
        {
            registry.Remove(Context.ConnectionId);
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(actor.DeviceId));
        await base.OnConnectedAsync();
    }
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
    // No method sends message bodies through the hub. REST inbox is authoritative.
}

public sealed class QueueWorker(IServiceScopeFactory scopes, IHubContext<EventsHub> hub,
    TimeProvider clock, ConnectionRegistry registry, ILogger<QueueWorker> logger) : BackgroundService
{
    private DateTimeOffset nextCleanup;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await Tick(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception) { logger.LogWarning("Queue worker failed; durable events will be retried."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    public async Task Tick(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MessengerDb>();
        var now = clock.GetUtcNow();
        var lease = Guid.NewGuid();
        // One atomic statement: multiple workers cannot acquire the same event lease.
        var entries = await db.Notifications.FromSqlInterpolated($"""
            WITH picked AS (
              SELECT "Id" FROM notifications
              WHERE "NextAttemptAt" <= {now} AND "ExpiresAt" > {now}
                AND ("LeaseUntil" IS NULL OR "LeaseUntil" < {now})
              ORDER BY "Id" LIMIT 100 FOR UPDATE SKIP LOCKED
            )
            UPDATE notifications n SET "LeaseId" = {lease}, "LeaseUntil" = {now.AddSeconds(30)},
              "Attempts" = n."Attempts" + 1
            FROM picked p WHERE n."Id" = p."Id" RETURNING n.*
            """).AsNoTracking().ToListAsync(ct);
        foreach (var entry in entries)
        {
            try
            {
                if (await db.Devices.AnyAsync(x => x.Id == entry.DeviceId && x.Active, ct))
                    await hub.Clients.Group(EventsHub.Group(entry.DeviceId)).SendAsync("QueueChanged", cancellationToken: ct);
                await db.Notifications.Where(x => x.Id == entry.Id && x.LeaseId == lease).ExecuteDeleteAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                var delay = Math.Min(300, 1 << Math.Min(entry.Attempts, 8));
                await db.Notifications.Where(x => x.Id == entry.Id && x.LeaseId == lease)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.LeaseId, (Guid?)null)
                        .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                        .SetProperty(x => x.NextAttemptAt, now.AddSeconds(delay + Random.Shared.NextDouble())), ct);
            }
        }
        await db.Envelopes.Where(x => x.Ciphertext != null && x.ExpiresAt <= now)
            .ExecuteUpdateAsync(x => x.SetProperty(e => e.Ciphertext, (byte[]?)null), ct);
        await db.Envelopes.Where(x => x.RetainUntil <= now).ExecuteDeleteAsync(ct);
        await db.KeyClaims.Where(x => x.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        await db.Notifications.Where(x => x.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        await CloseRevoked(db, now, ct);
        if (now >= nextCleanup)
        {
            await AuthService.Cleanup(db, now, ct);
            nextCleanup = now.AddMinutes(1);
        }
    }

    // Every instance runs this for its own SignalR connections, so a replacement or logout handled by another
    // instance still closes the old phone's socket within about a second.
    private async Task CloseRevoked(MessengerDb db, DateTimeOffset now, CancellationToken ct)
    {
        var (devices, sessions) = registry.Snapshot();
        if (devices.Length == 0) return;
        var inactive = await db.Devices.Where(x => devices.Contains(x.Id) && !x.Active).Select(x => x.Id).ToArrayAsync(ct);
        foreach (var id in inactive) registry.Revoke(id);
        if (sessions.Length == 0) return;
        var live = await db.RefreshFamilies.Where(x => sessions.Contains(x.Id) && x.RevokedAt == null && x.AbsoluteExpiresAt > now)
            .Select(x => x.Id).ToArrayAsync(ct);
        foreach (var id in sessions.Except(live)) registry.RevokeSession(id);
    }
}
