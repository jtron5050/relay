using Relay.Core.Persistence;

namespace Relay.Core.Audit;

public sealed record AuditEvent(string Id, string ActorKind, string ActorId, string Action,
    string TargetType, string TargetId, DateTimeOffset OccurredAt, string CorrelationId,
    long? BeforeRevision, long? AfterRevision, string Summary);

// Summary must be developer-defined text, never request values or exception messages.
public sealed record AuditDescription(string Action, string TargetType, string TargetId,
    long? BeforeRevision, long? AfterRevision, string Summary);
public sealed record AuditContext(string ActorId, string CorrelationId);
public sealed record AuditedMutation(SafeResult Result, IReadOnlyList<WriteAction> Actions, AuditDescription Audit);
public sealed record AuditQuery(string ActorId, DateTimeOffset From, DateTimeOffset To,
    int Limit = 50, string? TargetType = null, string? TargetId = null);
public sealed record AuditPage(IReadOnlyList<AuditEvent> Items, string? NextCursor);

public interface IAdministrativeMutations
{
    // The host must authenticate and authorize before calling, including on replay.
    Task<SafeResult> ExecuteAsync(AuditContext context, IdempotencyRequest request,
        Func<CancellationToken, Task<AuditedMutation>> prepare, CancellationToken cancellationToken = default);
}

public interface IAuditHistory
{
    Task<AuditPage> ListAsync(AuditQuery query, string? cursor = null, CancellationToken cancellationToken = default);
}
