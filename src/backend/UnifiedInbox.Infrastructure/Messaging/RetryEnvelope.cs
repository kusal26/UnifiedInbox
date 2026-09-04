namespace UnifiedInbox.Infrastructure.Messaging;

/// <summary>
/// Durable description of one scheduled broker retry. The envelope is published to the retry
/// exchange (into the TTL bucket for <see cref="Attempt"/>); after the bucket TTL it is
/// dead-lettered back to the worker queue, where the consumer only runs the operation once the
/// row's persisted schedule (<c>NextAttemptAt</c>/<c>AvailableAt</c>) is due. <see cref="NotBefore"/>
/// is informational; the database row is the source of truth for "when".
/// </summary>
public sealed record RetryEnvelope(Guid TenantId, Guid EntityId, string Operation, int Attempt, DateTimeOffset NotBefore);
