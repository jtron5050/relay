using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Relay.Core.Audit;
using Relay.Core.Persistence;
using Relay.Infrastructure.Audit;
using Relay.Infrastructure.Persistence;
using Xunit;

namespace Relay.Infrastructure.Tests;

public class AuditTests(DynamoFixture fixture) : IClassFixture<DynamoFixture>
{
    [DynamoFact]
    public async Task ConcurrentCommitBeforeRevisionRejectionReplaysReceipt()
    {
        var id = Guid.NewGuid().ToString("N"); var now = fixture.Clock.Now;
        var coordinator = new AdministrativeMutations(fixture.Store, fixture.Clock);
        var context = new AuditContext("staff", Guid.NewGuid().ToString());
        var request = new IdempotencyRequest("staff", "POST /v1/test/" + id, id, JsonSerializer.SerializeToElement(new { }));
        var result = await coordinator.ExecuteAsync(context, request, async ct =>
        {
            await coordinator.ExecuteAsync(context, request, _ => Task.FromResult(new AuditedMutation(new(201, id),
                [new PutRecord(new(new(id, "META"), "Test", 1, JsonSerializer.SerializeToElement(new { }), now, now), 0)],
                new("test.create", "test", id, null, 1, "Created test record."))), ct);
            throw new PersistenceConflictException("revisionConflict");
        });
        Assert.Equal(id, result.ResourceReference);
    }

    [DynamoFact]
    public async Task HistoryTraversesAllShardsAndHonorsExclusiveEndAcrossMonths()
    {
        var target = Guid.NewGuid().ToString("N");
        var from = new DateTimeOffset(2026, 1, 31, 23, 59, 59, TimeSpan.Zero);
        var to = from.AddSeconds(2);
        var expected = new HashSet<string>();
        var writes = new List<WriteAction>();
        foreach (var time in new[] { from.AddTicks(-1), from, from.AddSeconds(1), to })
        for (var shard = 0; shard < 16; shard++)
        {
            string id;
            do { id = Guid.NewGuid().ToString("N"); } while (PhysicalKeys.Shard(id, 16) != shard);
            var audit = new AuditEvent(id, "staff", "staff", "test", "test", target, time,
                Guid.NewGuid().ToString(), null, 1, "Safe summary.");
            var sort = PhysicalKeys.Timestamp(time) + "#" + id;
            writes.Add(new PutRecord(new(new($"AUDIT#{time:yyyy-MM-dd}#{shard}", sort), "AuditEvent", 1,
                JsonSerializer.SerializeToElement(audit, new JsonSerializerOptions(JsonSerializerDefaults.Web)), time, time,
                $"AUDIT-TARGET#test#{target}#{time:yyyy-MM}#{shard}", sort), 0));
            if (time >= from && time < to) expected.Add(id);
        }
        await fixture.Store.WriteAsync(writes);
        var reader = new AuditHistory(fixture.Store, new EphemeralDataProtectionProvider(), fixture.Clock);
        foreach (var targeted in new[] { false, true })
        {
            var found = new List<string>(); string? cursor = null;
            do
            {
                var page = await reader.ListAsync(new("staff", from, to, 1, targeted ? "test" : null, targeted ? target : null), cursor);
                Assert.InRange(page.Items.Count, 0, 1);
                found.AddRange(page.Items.Where(e => e.TargetId == target).Select(e => e.Id));
                cursor = page.NextCursor;
                Assert.True(cursor is null || cursor.Length <= 4096);
            } while (cursor is not null);
            Assert.Equal(expected.Count, found.Count);
            Assert.True(expected.SetEquals(found));
        }
        var stored = await fixture.Client.GetItemAsync(new Amazon.DynamoDBv2.Model.GetItemRequest
        {
            TableName = fixture.Table,
            Key = new() { ["PK"] = new() { S = writes[0].Key.Partition }, ["SK"] = new() { S = writes[0].Key.Sort } }
        });
        Assert.False(stored.Item.ContainsKey("expiresAt"));
    }

    [DynamoFact]
    public async Task MutationReplayAndConflictPreserveOneAuditAndState()
    {
        var id = Guid.NewGuid().ToString("N");
        var now = fixture.Clock.GetUtcNow();
        var coordinator = new AdministrativeMutations(fixture.Store, fixture.Clock);
        var context = new AuditContext("staff", Guid.NewGuid().ToString());
        var request = new IdempotencyRequest("staff", "PUT /v1/test/" + id, id, JsonSerializer.SerializeToElement(new { revision = 1 }));
        var calls = 0;
        Task<AuditedMutation> Prepare(CancellationToken _)
        {
            calls++;
            return Task.FromResult(new AuditedMutation(new(200, id),
                [new PutRecord(new(new(id, "META"), "Test", 1, JsonSerializer.SerializeToElement(new { value = 1 }), now, now), 0)],
                new("test.update", "test", id, null, 1, "Created test record.")));
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => coordinator.ExecuteAsync(context, request, Prepare)));
        Assert.All(results, r => Assert.Equal(id, r.ResourceReference));
        var before = calls;
        await coordinator.ExecuteAsync(context, request, _ => throw new InvalidOperationException("Stale revision must not be evaluated."));
        Assert.Equal(before, calls);
        var reader = new AuditHistory(fixture.Store, new EphemeralDataProtectionProvider(), fixture.Clock);
        var events = new List<AuditEvent>();
        string? cursor = null;
        do
        {
            var page = await reader.ListAsync(new("staff", now.AddMinutes(-1), now.AddMinutes(1), TargetType: "test", TargetId: id), cursor);
            events.AddRange(page.Items); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(context.CorrelationId, Assert.Single(events).CorrelationId);
        var conflict = await Assert.ThrowsAsync<PersistenceConflictException>(() => coordinator.ExecuteAsync(context,
            request with { CanonicalInput = JsonSerializer.SerializeToElement(new { revision = 2 }) }, Prepare));
        Assert.Equal("idempotencyConflict", conflict.Code);
    }

    [DynamoFact]
    public async Task RejectedTransactionDoesNotWriteAuditOrReceipt()
    {
        var id = Guid.NewGuid().ToString("N"); var now = fixture.Clock.GetUtcNow();
        var coordinator = new AdministrativeMutations(fixture.Store, fixture.Clock);
        var request = new IdempotencyRequest("staff", "POST /v1/test/" + id, id, JsonSerializer.SerializeToElement(new { }));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => coordinator.ExecuteAsync(new("staff", Guid.NewGuid().ToString()), request,
            _ => Task.FromResult(new AuditedMutation(new(201, id), [new CheckRevision(new(id, "META"), 1)],
                new("test.create", "test", id, null, 1, "Created test record.")))));
        Assert.Null(await fixture.Store.ReplayAsync(request));
        var reader = new AuditHistory(fixture.Store, new EphemeralDataProtectionProvider(), fixture.Clock);
        string? cursor = null;
        do
        {
            var page = await reader.ListAsync(new("staff", now.AddSeconds(-1), now.AddSeconds(1), TargetType: "test", TargetId: id), cursor);
            Assert.Empty(page.Items); cursor = page.NextCursor;
        } while (cursor is not null);
    }

    [DynamoFact]
    public async Task CursorBindsActorQueryAndExpiry()
    {
        var reader = new AuditHistory(fixture.Store, new EphemeralDataProtectionProvider(), fixture.Clock);
        var now = fixture.Clock.Now;
        var query = new AuditQuery("staff", now.AddDays(-2), now.AddDays(-1));
        var page = await reader.ListAsync(query);
        Assert.NotNull(page.NextCursor);
        Assert.Equal("invalidCursor", (await Assert.ThrowsAsync<PersistenceConflictException>(() => reader.ListAsync(query with { ActorId = "other" }, page.NextCursor))).Code);
        Assert.Equal("invalidCursor", (await Assert.ThrowsAsync<PersistenceConflictException>(() => reader.ListAsync(query, "tampered"))).Code);
        try
        {
            fixture.Clock.Now = now.AddMinutes(16);
            Assert.Equal("cursorExpired", (await Assert.ThrowsAsync<PersistenceConflictException>(() => reader.ListAsync(query, page.NextCursor))).Code);
        }
        finally { fixture.Clock.Now = now; }
    }
}
