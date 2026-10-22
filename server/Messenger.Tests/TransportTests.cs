using Messenger.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Xunit;

namespace Messenger.Tests;

// These tests require PostgreSQL. Missing TEST_DATABASE is a failure, never a silent skip.
[CollectionDefinition("PostgreSQL", DisableParallelization = true)]
public sealed class DatabaseCollection { }

[Collection("PostgreSQL")]
public sealed class TransportTests : IAsyncLifetime
{
    private string schema = "";
    private DbContextOptions<MessengerDb> options = null!;
    private DbContextOptions<MessengerDb> adminOptions = null!;
    private Actor alice = null!;
    private Actor bob = null!;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_DATABASE")
            ?? throw new InvalidOperationException("Set TEST_DATABASE to an isolated PostgreSQL test database.");
        schema = "test_" + Guid.NewGuid().ToString("N");
        adminOptions = new DbContextOptionsBuilder<MessengerDb>().UseNpgsql(connection).Options;
        await using var admin = new MessengerDb(adminOptions);
        await SchemaCommand(admin, false);
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connection) { SearchPath = schema };
        options = new DbContextOptionsBuilder<MessengerDb>().UseNpgsql(builder.ConnectionString,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .AddInterceptors(new SchemaInterceptor(schema)).Options;
        await using var db = new MessengerDb(options);
        await db.Database.MigrateAsync();
        alice = AddAccount(db); bob = AddAccount(db);
        await db.SaveChangesAsync();
    }

    private static Actor AddAccount(MessengerDb db)
    {
        var a = new Account { Id = Guid.NewGuid(), DisplayName = "Synthetic test account" };
        var d = new Device { Id = Guid.NewGuid(), AccountId = a.Id, Generation = 1, RegistrationId = 1, IdentityKey = new byte[33] };
        db.Accounts.Add(a); db.Devices.Add(d);
        return new(a.Id, d.Id, 1);
    }

    public async Task DisposeAsync()
    {
        if (schema.Length == 0) return;
        await using var admin = new MessengerDb(adminOptions);
        await SchemaCommand(admin, true);
    }

    private async Task SchemaCommand(MessengerDb db, bool drop)
    {
        // SQL parameters cannot represent identifiers. Quote the generated schema with the provider.
        var identifier = new Npgsql.NpgsqlCommandBuilder().QuoteIdentifier(schema);
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = drop ? "DROP SCHEMA IF EXISTS " + identifier + " CASCADE" : "CREATE SCHEMA " + identifier;
        await command.ExecuteNonQueryAsync();
        await db.Database.CloseConnectionAsync();
    }

    private SendRequest Message(Guid? id = null) => new(id ?? Guid.NewGuid(), bob.AccountId, bob.DeviceId, 1, 3,
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(256));

    private sealed class SchemaInterceptor(string schema) : DbConnectionInterceptor
    {
        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SET search_path TO " + new Npgsql.NpgsqlCommandBuilder().QuoteIdentifier(schema);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task RetryDoesNotDuplicateAndConflictingBodyIsRejected()
    {
        var r = Message();
        await using var db = new MessengerDb(options);
        var transport = new Transport(db, TimeProvider.System);
        var first = await transport.Send(alice, r, default);
        var again = await transport.Send(alice, r, default);
        Assert.Equal(first, again);
        var fault = await Assert.ThrowsAsync<ApiFault>(() => transport.Send(alice,
            r with { Ciphertext = new byte[32] }, default));
        Assert.Equal("IDEMPOTENCY_CONFLICT", fault.Code);
        Assert.Equal(1, await db.Envelopes.CountAsync());
        Assert.Equal(1, await db.Notifications.CountAsync());
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task DeliveryPersistsAcrossContextRestartAndAckErasesCiphertext()
    {
        long sequence;
        await using (var db = new MessengerDb(options))
            sequence = (await new Transport(db, TimeProvider.System).Send(alice, Message(), default)).Sequence;
        await using var reopened = new MessengerDb(options);
        var t = new Transport(reopened, TimeProvider.System);
        Assert.Single(await t.Inbox(bob, 0, default));
        Assert.Empty(await t.Inbox(alice, 0, default));
        await Assert.ThrowsAsync<ApiFault>(() => t.Ack(alice, new([sequence]), default));
        await t.Ack(bob, new([sequence]), default);
        await t.Ack(bob, new([sequence]), default);
        Assert.Empty(await t.Inbox(bob, 0, default));
        Assert.Null((await reopened.Envelopes.SingleAsync()).Ciphertext);
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task RevokedSenderAndStaleRecipientCannotSend()
    {
        await using var db = new MessengerDb(options);
        var t = new Transport(db, TimeProvider.System);
        var stale = await Assert.ThrowsAsync<ApiFault>(() => t.Send(alice, Message() with { RecipientGeneration = 9 }, default));
        Assert.Equal("RECIPIENT_DEVICE_CHANGED", stale.Code);
        await db.Devices.Where(x => x.Id == alice.DeviceId).ExecuteUpdateAsync(x => x.SetProperty(d => d.Active, false));
        var revoked = await Assert.ThrowsAsync<ApiFault>(() => t.Send(alice, Message(), default));
        Assert.Equal("DEVICE_REVOKED", revoked.Code);
        Assert.Empty(await db.Envelopes.ToArrayAsync());
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task BlockPreventsNewMessages()
    {
        await using var db = new MessengerDb(options);
        db.Blocks.Add(new() { OwnerId = bob.AccountId, TargetId = alice.AccountId });
        await db.SaveChangesAsync();
        var fault = await Assert.ThrowsAsync<ApiFault>(() => new Transport(db, TimeProvider.System).Send(alice, Message(), default));
        Assert.Equal("DELIVERY_UNAVAILABLE", fault.Code);
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task ConcurrentRetryCreatesExactlyOneEnvelope()
    {
        var message = Message();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = new MessengerDb(options);
            return await new Transport(db, TimeProvider.System).Send(alice, message, default);
        }));
        Assert.Single(results.Select(x => x.Sequence).Distinct());
        await using var verify = new MessengerDb(options);
        Assert.Equal(1, await verify.Envelopes.CountAsync());
    }

    [Fact, Trait("Category", "PostgreSQL")]
    public async Task PrekeyClaimsAreIdempotentAndNeverReactivatedByUpload()
    {
        await using var db = new MessengerDb(options);
        var t = new Transport(db, TimeProvider.System);
        var upload = new PublishBundleRequest(1, new byte[33], new byte[64],
            Enumerable.Range(1, 8).Select(i => new PublicPreKey(i, new byte[33])).ToArray(),
            Enumerable.Range(1, 8).Select(i => new PublicPqPreKey(i, new byte[1569], new byte[64])).ToArray());
        await t.Publish(bob, upload, default);
        var request = new ClaimRequest(bob.AccountId, Guid.NewGuid());
        var first = await t.Claim(alice, request, default);
        var repeat = await t.Claim(alice, request, default);
        Assert.Equal(first.PreKey.Id, repeat.PreKey.Id);
        await t.Publish(bob, upload, default);
        var next = await t.Claim(alice, request with { ClaimId = Guid.NewGuid() }, default);
        Assert.NotEqual(first.PreKey.Id, next.PreKey.Id);
        Assert.NotEqual(first.PqPreKey.Id, next.PqPreKey.Id);
    }
}
