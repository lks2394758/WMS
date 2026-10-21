using Wms.Domain.Common;

namespace Wms.Persistence.Abstractions;

/// <summary>Append-only audit trail entry. <see cref="ActorId"/> is null when the actor is unknown (e.g. failed login).</summary>
public sealed record AuditEntry(
    Guid Id,
    DateTimeOffset OccurredAt,
    UserId? ActorId,
    string Action,
    string EntityType,
    string? EntityId,
    string? Details = null);

/// <summary>
/// The stored outcome of a successful inventory write, keyed by user and Idempotency-Key (guide §8.1).
/// <see cref="RequestHash"/> covers the operation and the request body; <see cref="ResponseJson"/> is replayed verbatim.
/// </summary>
public sealed record IdempotencyRecord(
    UserId UserId,
    Guid Key,
    string Operation,
    string RequestHash,
    string ResponseJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>Audit entries written outside an inventory transaction (authentication events).</summary>
public interface IAuditLog
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken);
}
