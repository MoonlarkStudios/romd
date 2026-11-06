namespace Romd.Hosting.Vfs;

/// <summary>Reserved, bounded metadata capacity so long content transfers cannot starve listings.</summary>
public sealed class ConsumerVfsMetadataAdmission
{
    private readonly ConsumerVfsAdmission admission;

    public ConsumerVfsMetadataAdmission(ConsumerVfsOptions options)
    {
        admission = new(new ConsumerVfsOptions
        {
            MaximumConcurrentRequests = options.MaximumConcurrentMetadataRequests,
            MaximumConcurrentRequestsPerUser = 1,
            MaximumConcurrentRequestsPerConnection = 1,
            MaximumQueuedRequests = options.MaximumQueuedRequests,
            MaximumQueuedRequestsPerUser = options.MaximumQueuedRequestsPerUser,
            MaximumQueuedRequestsPerConnection = options.MaximumQueuedRequestsPerConnection,
            MaximumQueueWaitMilliseconds = options.MaximumQueueWaitMilliseconds
        }, "metadata");
    }

    public Task<IDisposable?> EnterAsync(Guid userId, Guid? connectionId, CancellationToken ct) =>
        admission.EnterAsync(userId, connectionId, ct);
}
