namespace Romd.Infrastructure.Realtime;

/// <summary>
///     Dispatch-side operations on the admin realtime outbox: claiming,
///     completion marking, and cleanup. Enqueueing event intents is the
///     application-owned <see cref="Romd.Admin.Application.Common.Realtime.IAdminEventOutbox" />
///     port, which enlists in the caller's unit of work instead of committing.
/// </summary>
public interface IAdminRealtimeOutbox
{
    Task<IReadOnlyList<AdminRealtimeOutboxMessage>> ClaimPendingAsync(
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task MarkProcessedAsync(int id, CancellationToken ct = default);

    Task MarkFailedAsync(int id, string error, TimeSpan retryDelay, CancellationToken ct = default);

    Task<int> DeleteProcessedOlderThanAsync(TimeSpan olderThan, CancellationToken ct = default);
}
