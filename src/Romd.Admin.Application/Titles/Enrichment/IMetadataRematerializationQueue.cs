namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Worker-side durable queue protocol. Resolve in a dedicated scope, separate from title
///     mutations. Acknowledgement occurs only after successful work, and retries preserve the request.
/// </summary>
public interface IMetadataRematerializationQueue
{
    Task<IReadOnlyList<MetadataRematerializationRequest>> GetPendingAsync(
        DateTimeOffset now, int limit, CancellationToken ct = default);

    Task AcknowledgeAsync(Guid requestId, CancellationToken ct = default);

    Task RetryAsync(Guid requestId, DateTimeOffset retryAt, CancellationToken ct = default);
}
