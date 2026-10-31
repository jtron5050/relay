using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Relay.Core.Persistence;

namespace Relay.Infrastructure.Persistence;

public sealed class DynamoRecordStore(IAmazonDynamoDB client, IOptions<DynamoOptions> options,
    IDataProtectionProvider protection, TimeProvider clock) : IRecordStore
{
    private readonly string table = options.Value.TableName;
    private readonly IDataProtector cursors = protection.CreateProtector("Relay.DynamoCursor.v1");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record CursorState(int Version, string Scope, DateTimeOffset ExpiresAt, Dictionary<string, string> Key);
    private sealed record Receipt(string Hash, SafeResult Result, DateTimeOffset? ExpiresAt);

    public async Task<StoredRecord?> GetAsync(RecordKey key, CancellationToken cancellationToken = default)
    {
        var response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = table, Key = Key(key), ConsistentRead = true
        }, cancellationToken);
        return response.Item is { Count: > 0 } ? Decode(response.Item) : null;
    }

    public async Task<IReadOnlyList<StoredRecord?>> ReadTransactionAsync(IReadOnlyList<RecordKey> keys,
        CancellationToken cancellationToken = default)
    {
        ValidateKeys(keys);
        var response = await client.TransactGetItemsAsync(new TransactGetItemsRequest
        {
            TransactItems = keys.Select(k => new TransactGetItem { Get = new Get { TableName = table, Key = Key(k) } }).ToList()
        }, cancellationToken);
        return response.Responses.Select(r => r.Item is { Count: > 0 } ? Decode(r.Item) : null).ToArray();
    }

    public async Task<RecordPage> QueryAsync(KeyedQuery query, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.ActorScope) || query.Limit is < 1 or > 100
            || query.Index is not (null or "GSI1" or "GSI2")) throw new ArgumentException("Invalid query.");
        ValidateKeyPart(query.Partition, 2048);
        var scope = CanonicalJson.HashText(JsonSerializer.Serialize(query, Json));
        Dictionary<string, AttributeValue>? start = null;
        if (cursor is not null)
        {
            try
            {
                if (cursor.Length > 16384) throw new ArgumentException();
                var state = JsonSerializer.Deserialize<CursorState>(cursors.Unprotect(cursor), Json)!;
                if (state.Version != 1 || state.Scope != scope) throw new ArgumentException();
                if (state.ExpiresAt <= clock.GetUtcNow()) throw new PersistenceConflictException("cursorExpired");
                start = state.Key.ToDictionary(p => p.Key, p => new AttributeValue { S = p.Value });
            }
            catch (PersistenceConflictException) { throw; }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or JsonException
                or ArgumentException or NullReferenceException)
            { throw new PersistenceConflictException("invalidCursor"); }
        }
        if (!string.IsNullOrEmpty(query.SortPrefix) && (query.SortFrom is not null || query.SortTo is not null))
            throw new ArgumentException("Use a sort prefix or a range, not both.");
        var prefix = query.Index ?? "";
        var names = new Dictionary<string, string> { ["#pk"] = prefix + "PK" };
        var values = new Dictionary<string, AttributeValue> { [":pk"] = new() { S = query.Partition } };
        var condition = "#pk = :pk";
        if (!string.IsNullOrEmpty(query.SortPrefix))
        {
            ValidateKeyPart(query.SortPrefix, 1024);
            names["#sk"] = prefix + "SK";
            values[":prefix"] = new() { S = query.SortPrefix };
            condition += " AND begins_with(#sk, :prefix)";
        }
        if (query.SortFrom is not null || query.SortTo is not null)
        {
            names["#sk"] = prefix + "SK";
            if (query.SortFrom is not null)
            {
                ValidateKeyPart(query.SortFrom, 1024);
                values[":from"] = new() { S = query.SortFrom };
            }
            if (query.SortTo is not null)
            {
                ValidateKeyPart(query.SortTo, 1024);
                values[":to"] = new() { S = query.SortTo };
            }
            condition += query.SortFrom is null ? " AND #sk <= :to"
                : query.SortTo is null ? " AND #sk >= :from" : " AND #sk BETWEEN :from AND :to";
        }
        // Exactly one service page: <=100 evaluated rows and DynamoDB's 1 MiB cap; no Scan/filter loop.
        var response = await client.QueryAsync(new QueryRequest
        {
            TableName = table, IndexName = query.Index, ConsistentRead = query.Index is null,
            KeyConditionExpression = condition, ExpressionAttributeNames = names, ExpressionAttributeValues = values,
            Limit = query.Limit, ScanIndexForward = !query.Descending, ExclusiveStartKey = start
        }, cancellationToken);
        string? next = null;
        if (response.LastEvaluatedKey is { Count: > 0 })
        {
            var state = new CursorState(1, scope, clock.GetUtcNow().AddMinutes(15),
                response.LastEvaluatedKey.ToDictionary(p => p.Key, p => p.Value.S));
            next = cursors.Protect(JsonSerializer.Serialize(state, Json));
        }
        // GSI projections intentionally exclude payloads: rehydrate by authoritative base key.
        var records = new List<StoredRecord>();
        foreach (var item in response.Items ?? [])
        {
            var record = query.Index is null ? Decode(item)
                : await GetAsync(new RecordKey(item["PK"].S, item["SK"].S), cancellationToken);
            if (record is not null) records.Add(record);
        }
        return new RecordPage(records, next);
    }

    public async Task WriteAsync(IReadOnlyList<WriteAction> actions, CancellationToken cancellationToken = default)
    {
        ValidateKeys(actions.Select(a => a.Key).ToArray());
        var items = new List<TransactWriteItem>();
        var totalBytes = 0;
        foreach (var action in actions)
        {
            switch (action)
            {
                case PutRecord put:
                    if (put.ExpectedRevision < 0 || put.ExpectedRevision == long.MaxValue
                        || put.Record.Revision != put.ExpectedRevision + 1) throw new ArgumentException("Revision must advance by one.");
                    var item = Encode(put.Record);
                    var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(item));
                    if (size >= 300 * 1024) throw new ArgumentException("Item exceeds the 300 KiB budget.");
                    totalBytes += size;
                    items.Add(new TransactWriteItem { Put = new Put
                    {
                        TableName = table, Item = item,
                        ConditionExpression = put.ExpectedRevision == 0 ? "attribute_not_exists(PK)" : "#r = :r",
                        ExpressionAttributeNames = put.ExpectedRevision == 0 ? null : new() { ["#r"] = "revision" },
                        ExpressionAttributeValues = put.ExpectedRevision == 0 ? null : new() { [":r"] = Number(put.ExpectedRevision) }
                    }});
                    break;
                case CheckRevision check:
                    if (check.ExpectedRevision < 0) throw new ArgumentException("Invalid revision.");
                    items.Add(new TransactWriteItem { ConditionCheck = new ConditionCheck
                    {
                        TableName = table, Key = Key(check.Key),
                        ConditionExpression = check.ExpectedRevision == 0 ? "attribute_not_exists(PK)" : "#r = :r",
                        ExpressionAttributeNames = check.ExpectedRevision == 0 ? null : new() { ["#r"] = "revision" },
                        ExpressionAttributeValues = check.ExpectedRevision == 0 ? null : new() { [":r"] = Number(check.ExpectedRevision) }
                    }});
                    totalBytes += 4096;
                    break;
                default: throw new ArgumentException("Unsupported action.");
            }
        }
        if (totalBytes >= 4 * 1024 * 1024) throw new ArgumentException("Transaction exceeds the 4 MiB budget.");
        try
        {
            await client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items }, cancellationToken);
        }
        catch (TransactionCanceledException e) when (e.CancellationReasons?.Any(r => r.Code == "ConditionalCheckFailed") == true)
        { throw new PersistenceConflictException("revisionConflict"); }
        catch (TransactionCanceledException e) when (e.CancellationReasons?.Any(r => r.Code == "TransactionConflict") == true)
        { throw new PersistenceConflictException("transactionConflict"); }
        catch (TransactionConflictException) { throw new PersistenceConflictException("transactionConflict"); }
    }

    public async Task<SafeResult> ExecuteOnceAsync(IdempotencyRequest request, SafeResult result,
        IReadOnlyList<WriteAction> actions, CancellationToken cancellationToken = default)
    {
        foreach (var part in new[] { request.Actor, request.Operation, request.Key }) ValidateKeyPart(part, 1024);
        if (result.Status is < 200 or > 299 || string.IsNullOrWhiteSpace(result.ResourceReference)
            || result.ResourceReference.Length > 256 || result.ResourceReference.Contains("://")
            || result.ResourceReference.Contains('?')) throw new ArgumentException("Only safe successful result references may be stored.");
        var key = new RecordKey($"IDEMPOTENCY#{CanonicalJson.HashText(request.Actor)}#{CanonicalJson.HashText(request.Operation)}#{CanonicalJson.HashText(request.Key)}", "META");
        var hash = "v1:" + CanonicalJson.Hash(JsonSerializer.SerializeToElement(new
        { input = request.CanonicalInput, permanent = request.Permanent }, Json));
        var previous = await GetAsync(key, cancellationToken);
        if (previous is not null) return Replay(previous, hash);
        var now = clock.GetUtcNow();
        var receipt = new Receipt(hash, result, request.Permanent ? null : now.AddHours(24));
        var record = new StoredRecord(key, "IdempotencyReceipt", 1, JsonSerializer.SerializeToElement(receipt, Json), now, now);
        // TTL is only cleanup; Replay checks logical expiry regardless of physical presence.
        try { await WriteAsync(actions.Append(new PutRecord(record, 0)).ToArray(), cancellationToken); }
        catch (PersistenceConflictException)
        {
            previous = await GetAsync(key, cancellationToken);
            if (previous is not null) return Replay(previous, hash);
            throw;
        }
        return result;
    }

    private SafeResult Replay(StoredRecord record, string hash)
    {
        var receipt = record.Data.Deserialize<Receipt>(Json) ?? throw new InvalidOperationException("Invalid receipt.");
        if (receipt.Hash != hash) throw new PersistenceConflictException("idempotencyConflict");
        if (receipt.ExpiresAt <= clock.GetUtcNow()) throw new PersistenceConflictException("idempotencyExpired");
        if (receipt.Result.SecretIssued) throw new PersistenceConflictException("secretAlreadyIssued", receipt.Result.ResourceReference);
        return receipt.Result;
    }

    private static Dictionary<string, AttributeValue> Encode(StoredRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.EntityType) || record.CreatedAt.Offset != TimeSpan.Zero
            || record.UpdatedAt.Offset != TimeSpan.Zero || record.UpdatedAt < record.CreatedAt)
            throw new ArgumentException("Invalid record metadata.");
        var item = Key(record.Key);
        item["entityType"] = new() { S = record.EntityType };
        item["schemaVersion"] = Number(1);
        item["revision"] = Number(record.Revision);
        item["createdAt"] = new() { S = record.CreatedAt.ToString("O", CultureInfo.InvariantCulture) };
        item["updatedAt"] = new() { S = record.UpdatedAt.ToString("O", CultureInfo.InvariantCulture) };
        item["payload"] = new() { S = record.Data.GetRawText() };
        AddIndex(item, "GSI1", record.Gsi1Partition, record.Gsi1Sort);
        AddIndex(item, "GSI2", record.Gsi2Partition, record.Gsi2Sort);
        if (record.EntityType == "IdempotencyReceipt")
        {
            var receipt = record.Data.Deserialize<Receipt>(Json)!;
            if (receipt.ExpiresAt is { } expires) item["expiresAt"] = Number(expires.ToUnixTimeSeconds());
        }
        return item;
    }

    private static StoredRecord Decode(Dictionary<string, AttributeValue> item)
    {
        if (item["schemaVersion"].N != "1") throw new InvalidOperationException("Unsupported record schema.");
        return new StoredRecord(new(item["PK"].S, item["SK"].S), item["entityType"].S,
            long.Parse(item["revision"].N, CultureInfo.InvariantCulture), JsonSerializer.Deserialize<JsonElement>(item["payload"].S),
            DateTimeOffset.Parse(item["createdAt"].S, CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(item["updatedAt"].S, CultureInfo.InvariantCulture),
            item.GetValueOrDefault("GSI1PK")?.S, item.GetValueOrDefault("GSI1SK")?.S,
            item.GetValueOrDefault("GSI2PK")?.S, item.GetValueOrDefault("GSI2SK")?.S);
    }

    private static void AddIndex(Dictionary<string, AttributeValue> item, string index, string? partition, string? sort)
    {
        if (partition is null && sort is null) return;
        ValidateKeyPart(partition!, 2048); ValidateKeyPart(sort!, 1024);
        item[index + "PK"] = new() { S = partition }; item[index + "SK"] = new() { S = sort };
    }
    private static AttributeValue Number(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    private static Dictionary<string, AttributeValue> Key(RecordKey key)
    {
        ValidateKeyPart(key.Partition, 2048); ValidateKeyPart(key.Sort, 1024);
        return new() { ["PK"] = new() { S = key.Partition }, ["SK"] = new() { S = key.Sort } };
    }
    private static void ValidateKeyPart(string part, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(part) || Encoding.UTF8.GetByteCount(part) > maxBytes)
            throw new ArgumentException("Invalid key component.");
    }
    private static void ValidateKeys(IReadOnlyList<RecordKey> keys)
    {
        if (keys.Count is < 1 or > 100 || keys.Distinct().Count() != keys.Count)
            throw new ArgumentException("Transactions require 1–100 distinct keys.");
        foreach (var key in keys) _ = Key(key);
    }
}
