using System.Text.Json;
using System.Globalization;
using Relay.Core.Audit;
using Relay.Core.Persistence;
using Relay.Infrastructure.Persistence;

namespace Relay.Infrastructure.Audit;

public sealed class AdministrativeMutations(IRecordStore store, TimeProvider clock) : IAdministrativeMutations
{
    public async Task<SafeResult> ExecuteAsync(AuditContext context, IdempotencyRequest request,
        Func<CancellationToken, Task<AuditedMutation>> prepare, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.ActorId) || request.Actor != context.ActorId
            || !Guid.TryParseExact(context.CorrelationId, "D", out _))
            throw new ArgumentException("Invalid audit context.");
        var replay = await store.ReplayAsync(request, cancellationToken);
        if (replay is not null) return replay;
        AuditedMutation mutation;
        try { mutation = await prepare(cancellationToken); }
        catch (PersistenceConflictException)
        {
            // Another identical request may have committed between lookup and validation.
            replay = await store.ReplayAsync(request, cancellationToken);
            if (replay is not null) return replay;
            throw;
        }
        var description = mutation.Audit;
        if (mutation.Actions.Count == 0 || string.IsNullOrWhiteSpace(description.Summary)
            || description.Summary.Length > 1024 || description.BeforeRevision is < 1
            || description.AfterRevision is < 1)
            throw new ArgumentException("Invalid audit description.");
        foreach (var value in new[] { description.Action, description.TargetType, description.TargetId })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(c => c < 33 || c > 126))
                throw new ArgumentException("Invalid audit identifier.");
        var now = clock.GetUtcNow();
        var id = Guid.NewGuid().ToString("N");
        var audit = new AuditEvent(id, "staff", context.ActorId, description.Action, description.TargetType,
            description.TargetId, now, context.CorrelationId, description.BeforeRevision, description.AfterRevision, description.Summary);
        var shard = PhysicalKeys.Shard(id, 16);
        var sort = $"{PhysicalKeys.Timestamp(now)}#{id}";
        var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var month = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var record = new StoredRecord(new($"AUDIT#{day}#{shard}", sort), "AuditEvent", 1,
            JsonSerializer.SerializeToElement(audit, new JsonSerializerOptions(JsonSerializerDefaults.Web)), now, now,
            $"AUDIT-TARGET#{PhysicalKeys.Component(audit.TargetType)}#{PhysicalKeys.Component(audit.TargetId)}#{month}#{shard}", sort);
        return await store.ExecuteOnceAsync(request, mutation.Result,
            mutation.Actions.Append(new PutRecord(record, 0)).ToArray(), cancellationToken);
    }
}
