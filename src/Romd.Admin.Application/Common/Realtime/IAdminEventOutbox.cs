namespace Romd.Admin.Application.Common.Realtime;

/// <summary>
///     Application-owned enqueue port for admin realtime event intents
///     (docs/decisions/admin-use-case-transaction-event-boundaries.md,
///     "Domain Events Are Transactional Outbox Intents").
///     Enqueue-only: an implementation records the event intent in the caller's
///     current unit of work without committing. The caller owns the commit — an
///     enqueued intent becomes durable together with the business mutation at
///     the caller's next SaveChanges/transaction commit, and is discarded if
///     that commit never happens or rolls back.
/// </summary>
public interface IAdminEventOutbox
{
    /// <summary>
    ///     Records a payload-less admin event intent in the current unit of work.
    /// </summary>
    Task EnqueueAsync(string eventType, CancellationToken ct = default);

    /// <summary>
    ///     Records an admin event intent with a payload in the current unit of work.
    /// </summary>
    Task EnqueueAsync<TPayload>(string eventType, TPayload payload, CancellationToken ct = default);
}
