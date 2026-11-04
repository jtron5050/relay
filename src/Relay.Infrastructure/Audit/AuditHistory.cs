using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Relay.Core.Audit;
using Relay.Core.Persistence;
using Relay.Infrastructure.Persistence;

namespace Relay.Infrastructure.Audit;

public sealed class AuditHistory(IRecordStore store, IDataProtectionProvider protection, TimeProvider clock) : IAuditHistory
{
    private readonly IDataProtector protector = protection.CreateProtector("Relay.AuditCursor.v1");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Position(int Version, string Scope, DateTimeOffset ExpiresAt,
        DateTimeOffset Bucket, int Shard, string? Inner);

    public async Task<AuditPage> ListAsync(AuditQuery query, string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.ActorId) || query.From.Offset != TimeSpan.Zero || query.To.Offset != TimeSpan.Zero
            || query.From >= query.To || query.To - query.From > TimeSpan.FromDays(31) || query.Limit is < 1 or > 100
            || (query.TargetType is null) != (query.TargetId is null)) throw new ArgumentException("Invalid audit query.");
        var scope = CanonicalJson.Hash(JsonSerializer.SerializeToElement(query, Json));
        var targeted = query.TargetType is not null;
        var bucket = new DateTimeOffset(query.From.Year, query.From.Month, targeted ? 1 : query.From.Day, 0, 0, 0, TimeSpan.Zero);
        var position = new Position(1, scope, clock.GetUtcNow().AddMinutes(15), bucket, 0, null);
        if (cursor is not null)
        {
            try
            {
                if (cursor.Length is < 1 or > 4096) throw new ArgumentException();
                position = JsonSerializer.Deserialize<Position>(protector.Unprotect(cursor), Json)!;
                if (position.Version != 1 || position.Scope != scope || position.Shard is < 0 or > 15) throw new ArgumentException();
                if (position.ExpiresAt <= clock.GetUtcNow()) throw new PersistenceConflictException("cursorExpired");
            }
            catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException or NullReferenceException)
            { throw new PersistenceConflictException("invalidCursor"); }
        }
        var items = new List<AuditEvent>();
        for (var work = 0; work < 4 && position.Bucket < query.To; work++)
        {
            var bucketText = position.Bucket.ToString(targeted ? "yyyy-MM" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var partition = targeted
                ? $"AUDIT-TARGET#{PhysicalKeys.Component(query.TargetType!)}#{PhysicalKeys.Component(query.TargetId!)}#{bucketText}#{position.Shard}"
                : $"AUDIT#{bucketText}#{position.Shard}";
            var page = await store.QueryAsync(new(scope, partition, "", query.Limit, targeted ? "GSI1" : null,
                SortFrom: PhysicalKeys.Timestamp(query.From) + "#", SortTo: PhysicalKeys.Timestamp(query.To)), position.Inner, cancellationToken);
            items.AddRange(page.Items.Select(r => r.Data.Deserialize<AuditEvent>(Json)!));
            if (page.NextCursor is not null) position = position with { Inner = page.NextCursor };
            else if (position.Shard < 15) position = position with { Shard = position.Shard + 1, Inner = null };
            else position = position with { Bucket = targeted ? position.Bucket.AddMonths(1) : position.Bucket.AddDays(1), Shard = 0, Inner = null };
            if (page.EvaluatedCount > 0 || page.Items.Count > 0 || page.NextCursor is not null) break;
        }
        string? next = position.Bucket >= query.To ? null : protector.Protect(JsonSerializer.Serialize(position, Json));
        if (next?.Length > 4096) throw new InvalidOperationException("Cursor exceeds budget.");
        return new(items, next);
    }
}
