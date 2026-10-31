using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Relay.Core.Persistence;
using Relay.Infrastructure.Persistence;
using Xunit;

namespace Relay.Infrastructure.Tests;

public sealed class DynamoFactAttribute : FactAttribute
{
    public DynamoFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RELAY_DYNAMODB_ENDPOINT") is null)
            Skip = "Set RELAY_DYNAMODB_ENDPOINT to an isolated DynamoDB Local endpoint; see docs/persistence.md.";
    }
}

public sealed class DynamoFixture : IAsyncLifetime
{
    public AmazonDynamoDBClient Client { get; private set; } = null!;
    public string Table { get; } = "relay-tests-" + Guid.NewGuid().ToString("N");
    public MutableClock Clock { get; } = new();
    public DynamoRecordStore Store { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("RELAY_DYNAMODB_ENDPOINT");
        if (endpoint is null) return;
        var uri = new Uri(endpoint);
        if (!uri.IsLoopback || uri.Scheme != "http") throw new InvalidOperationException("Tests require loopback DynamoDB Local.");
        Client = new(new BasicAWSCredentials("local", "local"), new AmazonDynamoDBConfig { ServiceURL = endpoint });
        await Client.CreateTableAsync(new CreateTableRequest
        {
            TableName = Table, BillingMode = BillingMode.PAY_PER_REQUEST,
            AttributeDefinitions = [new("PK", ScalarAttributeType.S), new("SK", ScalarAttributeType.S),
                new("GSI1PK", ScalarAttributeType.S), new("GSI1SK", ScalarAttributeType.S)],
            KeySchema = [new("PK", KeyType.HASH), new("SK", KeyType.RANGE)],
            GlobalSecondaryIndexes = [new() { IndexName = "GSI1", KeySchema = [new("GSI1PK", KeyType.HASH),
                new("GSI1SK", KeyType.RANGE)], Projection = new() { ProjectionType = ProjectionType.KEYS_ONLY } }]
        });
        Store = new(Client, Options.Create(new DynamoOptions { TableName = Table }),
            new EphemeralDataProtectionProvider(), Clock);
    }
    public async Task DisposeAsync()
    {
        if (Client is null) return;
        await Client.DeleteTableAsync(Table);
        Client.Dispose();
    }
}

public sealed class MutableClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class PersistenceTests(DynamoFixture fixture) : IClassFixture<DynamoFixture>
{
    private static JsonElement Data(string json = "{}") => JsonDocument.Parse(json).RootElement.Clone();
    private static StoredRecord Record(string pk, string sk = "META", long revision = 1) =>
        new(new(pk, sk), "Test", revision, Data("{\"name\":\"example\"}"), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    private static string Id() => Guid.NewGuid().ToString("N");
    private static async Task<string?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (PersistenceConflictException e) { return e.Code; }
    }

    [Fact]
    public void CanonicalHashIgnoresPropertyOrderButPreservesArraysAndRejectsDuplicates()
    {
        Assert.Equal(CanonicalJson.Hash(Data("{\"a\":1,\"b\":2}")), CanonicalJson.Hash(Data("{\"b\":2,\"a\":1}")));
        Assert.NotEqual(CanonicalJson.Hash(Data("[1,2]")), CanonicalJson.Hash(Data("[2,1]")));
        Assert.Throws<ArgumentException>(() => CanonicalJson.Hash(Data("{\"a\":1,\"a\":2}")));
        Assert.NotEqual(PhysicalKeys.Component("a#b"), PhysicalKeys.Component("a%23b"));
        Assert.True(string.CompareOrdinal(PhysicalKeys.Number(9), PhysicalKeys.Number(10)) < 0);
        Assert.InRange(PhysicalKeys.Shard("computer", 32), 0, 31);
    }

    [Fact]
    public void ConfigurationRejectsUnsafeEndpointAndResolvesAdapter()
    {
        using var valid = new ServiceCollection().AddRelayPersistence(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DynamoDb:TableName"] = "test",
                ["DynamoDb:LocalEndpoint"] = "http://localhost:8000" }).Build()).BuildServiceProvider();
        Assert.IsType<DynamoRecordStore>(valid.GetRequiredService<IRecordStore>());
        using var invalid = new ServiceCollection().AddRelayPersistence(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DynamoDb:TableName"] = "test",
                ["DynamoDb:LocalEndpoint"] = "http://remote.example:8000" }).Build()).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => invalid.GetRequiredService<IRecordStore>());
    }

    [DynamoFact]
    public async Task OneMiBServiceBoundaryReturnsContinuationWithoutFillingPage()
    {
        var pk = Id();
        var payload = JsonSerializer.SerializeToElement(new string('x', 240 * 1024));
        await fixture.Store.WriteAsync(Enumerable.Range(0, 6).Select(i => (WriteAction)new PutRecord(
            Record(pk, $"ROW#{i:D2}") with { Data = payload }, 0)).ToArray());
        var query = new KeyedQuery("actor", pk, "ROW#", 100);
        var first = await fixture.Store.QueryAsync(query);
        Assert.InRange(first.Items.Count, 1, 5);
        Assert.NotNull(first.NextCursor);
        var second = await fixture.Store.QueryAsync(query, first.NextCursor);
        Assert.Equal(6, first.Items.Count + second.Items.Count);
        Assert.Null(second.NextCursor);
        var oversized = Enumerable.Range(0, 20).Select(i => (WriteAction)new PutRecord(
            Record(Id()) with { Data = payload }, 0)).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.WriteAsync(oversized));
        Assert.Null(await fixture.Store.GetAsync(oversized[0].Key));
    }

    [DynamoFact]
    public async Task ConcurrentRevisionWritesHaveExactlyOneWinner()
    {
        var record = Record(Id());
        await fixture.Store.WriteAsync([new PutRecord(record, 0)]);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Capture(() =>
            fixture.Store.WriteAsync([new PutRecord(record with { Revision = 2 }, 1)]))));
        Assert.Single(outcomes, x => x is null);
        Assert.All(outcomes.Where(x => x is not null), x => Assert.Contains(x, new[] { "revisionConflict", "transactionConflict" }));
        Assert.Equal(2, (await fixture.Store.GetAsync(record.Key))!.Revision);
    }

    [DynamoFact]
    public async Task FailedReservationRollsBackEntireTransactionAndReadSetPreservesAbsence()
    {
        var reservation = Record(Id()); var other = Record(Id());
        await fixture.Store.WriteAsync([new PutRecord(reservation, 0)]);
        Assert.Equal("revisionConflict", await Capture(() => fixture.Store.WriteAsync([
            new PutRecord(other, 0), new PutRecord(reservation, 0)])));
        var snapshot = await fixture.Store.ReadTransactionAsync([reservation.Key, other.Key]);
        Assert.NotNull(snapshot[0]); Assert.Null(snapshot[1]);
        await fixture.Store.WriteAsync([new CheckRevision(reservation.Key, 1), new PutRecord(other, 0)]);
    }

    [DynamoFact]
    public async Task PaginationTraversesKeysAndRejectsTamperedChangedAndExpiredCursors()
    {
        var pk = Id();
        await fixture.Store.WriteAsync(Enumerable.Range(0, 7).Select(i => (WriteAction)new PutRecord(Record(pk, $"ROW#{i:D2}"), 0)).ToArray());
        var query = new KeyedQuery("staff-1", pk, "ROW#", 2);
        var page = await fixture.Store.QueryAsync(query);
        Assert.Equal(2, page.Items.Count); Assert.NotNull(page.NextCursor);
        Assert.Equal("invalidCursor", await Capture(async () => { await fixture.Store.QueryAsync(query with { ActorScope = "staff-2" }, page.NextCursor); }));
        Assert.Equal("invalidCursor", await Capture(async () => { await fixture.Store.QueryAsync(query with { Limit = 3 }, page.NextCursor); }));
        Assert.Equal("invalidCursor", await Capture(async () => { await fixture.Store.QueryAsync(query, "broken" + page.NextCursor); }));
        var keys = page.Items.Select(r => r.Key).ToList(); var cursor = page.NextCursor;
        while (cursor is not null)
        {
            var next = await fixture.Store.QueryAsync(query, cursor);
            keys.AddRange(next.Items.Select(r => r.Key)); cursor = next.NextCursor;
        }
        Assert.Equal(7, keys.Count); Assert.Equal(7, keys.Distinct().Count());
        var range = await fixture.Store.QueryAsync(new("staff-1", pk, "", 10,
            Descending: true, SortFrom: "ROW#02", SortTo: "ROW#04"));
        Assert.Equal(new[] { "ROW#04", "ROW#03", "ROW#02" }, range.Items.Select(r => r.Key.Sort));
        var now = fixture.Clock.Now;
        try
        {
            fixture.Clock.Now = now.AddMinutes(16);
            Assert.Equal("cursorExpired", await Capture(async () => { await fixture.Store.QueryAsync(query, page.NextCursor); }));
        }
        finally { fixture.Clock.Now = now; }
    }

    [DynamoFact]
    public async Task SparseIndexPageHydratesKeysOnlyProjection()
    {
        var group = Id(); var record = Record(Id()) with { Gsi1Partition = group, Gsi1Sort = "CREATED#1" };
        await fixture.Store.WriteAsync([new PutRecord(record, 0)]);
        var page = await fixture.Store.QueryAsync(new("staff", group, "CREATED#", Index: "GSI1"));
        Assert.Equal(record.Key, Assert.Single(page.Items).Key);
        Assert.Equal("example", page.Items[0].Data.GetProperty("name").GetString());
    }

    [DynamoFact]
    public async Task IdempotencyIsAtomicScopedAndBoundToInput()
    {
        var request = new IdempotencyRequest("actor", "POST /resources/" + Id(), "retry-key", Data("{\"revision\":1}"));
        var result = new SafeResult(201, Id()); var record = Record(Id());
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => fixture.Store.ExecuteOnceAsync(request, result, [new PutRecord(record, 0)])));
        Assert.All(outcomes, actual => Assert.Equal(result, actual));
        // Stale precondition is not checked again on replay.
        Assert.Equal(result, await fixture.Store.ExecuteOnceAsync(request, result, [new CheckRevision(record.Key, 999)]));
        Assert.Equal("idempotencyConflict", await Capture(async () => { await fixture.Store.ExecuteOnceAsync(request with { CanonicalInput = Data("{}") }, result, []); }));
        Assert.Equal(new SafeResult(200, "other"), await fixture.Store.ExecuteOnceAsync(request with { Actor = "other" }, new(200, "other"), []));
        var failed = request with { Key = "failed" };
        Assert.Equal("revisionConflict", await Capture(async () => { await fixture.Store.ExecuteOnceAsync(failed, result, [new CheckRevision(record.Key, 999)]); }));
        Assert.Equal(result, await fixture.Store.ExecuteOnceAsync(failed, result, []));
    }

    [DynamoFact]
    public async Task LogicalExpiryRejectsRetainedReceiptAndPermanentBindingsDoNotExpire()
    {
        var request = new IdempotencyRequest("actor", "POST /" + Id(), "key", Data());
        var result = new SafeResult(201, Id());
        await fixture.Store.ExecuteOnceAsync(request, result, []);
        var permanent = request with { Key = "permanent", Permanent = true };
        await fixture.Store.ExecuteOnceAsync(permanent, result, []);
        var now = fixture.Clock.Now;
        try
        {
            fixture.Clock.Now = now.AddDays(2);
            Assert.Equal("idempotencyExpired", await Capture(async () => { await fixture.Store.ExecuteOnceAsync(request, result, []); }));
            Assert.Equal(result, await fixture.Store.ExecuteOnceAsync(permanent, result, []));
        }
        finally { fixture.Clock.Now = now; }
    }

    [DynamoFact]
    public async Task SecretReplayNeverReissuesOrStoresTheSecret()
    {
        var request = new IdempotencyRequest("actor", "POST /tokens/" + Id(), "key", Data());
        var result = new SafeResult(201, "token-safe-id", SecretIssued: true);
        await fixture.Store.ExecuteOnceAsync(request, result, []);
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => fixture.Store.ExecuteOnceAsync(request, result, []));
        Assert.Equal("secretAlreadyIssued", error.Code);
        Assert.Equal(result.ResourceReference, error.ResourceReference);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.ExecuteOnceAsync(request, new(200, "https://signed.example/?secret=value"), []));
    }

    [DynamoFact]
    public async Task BudgetsAndDuplicateKeysAreRejectedBeforeCommit()
    {
        var record = Record(Id());
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.WriteAsync([new PutRecord(record, 0), new CheckRevision(record.Key, 0)]));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.WriteAsync([new PutRecord(record with { Revision = 3 }, 0)]));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.WriteAsync(Enumerable.Range(0, 101).Select(i => (WriteAction)new PutRecord(Record(Id()), 0)).ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.WriteAsync([new PutRecord(record with { Data = JsonSerializer.SerializeToElement(new string('x', 310 * 1024)) }, 0)]));
        Assert.Null(await fixture.Store.GetAsync(record.Key));
    }
}
