using System.Text.Json;

namespace Relay.Core.Persistence;

public sealed class PersistenceConflictException(string code, string? resourceReference = null) : Exception(code)
{
    public string Code { get; } = code;
    public string? ResourceReference { get; } = resourceReference;
}

public sealed record RecordKey(string Partition, string Sort);
public sealed record StoredRecord(RecordKey Key, string EntityType, long Revision, JsonElement Data,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Gsi1Partition = null,
    string? Gsi1Sort = null, string? Gsi2Partition = null, string? Gsi2Sort = null);

// ExpectedRevision=0 reserves a new identity; existing identities are never deleted/recreated.
public abstract record WriteAction(RecordKey Key);
public sealed record PutRecord(StoredRecord Record, long ExpectedRevision) : WriteAction(Record.Key);
public sealed record CheckRevision(RecordKey RecordKey, long ExpectedRevision) : WriteAction(RecordKey);
public sealed record KeyedQuery(string ActorScope, string Partition, string SortPrefix,
    int Limit = 50, string? Index = null, bool Descending = false,
    string? SortFrom = null, string? SortTo = null);
public sealed record RecordPage(IReadOnlyList<StoredRecord> Items, string? NextCursor);

// Safe references only: callers must never pass secrets, signed URLs or sensitive diagnostics.
public sealed record SafeResult(int Status, string ResourceReference, bool SecretIssued = false);
public sealed record IdempotencyRequest(string Actor, string Operation, string Key,
    JsonElement CanonicalInput, bool Permanent = false);

public interface IRecordStore
{
    Task<StoredRecord?> GetAsync(RecordKey key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredRecord?>> ReadTransactionAsync(IReadOnlyList<RecordKey> keys,
        CancellationToken cancellationToken = default);
    Task<RecordPage> QueryAsync(KeyedQuery query, string? cursor = null,
        CancellationToken cancellationToken = default);
    Task WriteAsync(IReadOnlyList<WriteAction> actions, CancellationToken cancellationToken = default);
    // Auth/permissions must be revalidated by the caller even on replay. Includes the receipt atomically.
    Task<SafeResult> ExecuteOnceAsync(IdempotencyRequest request, SafeResult result,
        IReadOnlyList<WriteAction> actions, CancellationToken cancellationToken = default);
}
