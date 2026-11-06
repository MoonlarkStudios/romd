using Romd.Domain.Jobs;
namespace Romd.Admin.Application.Ingestion.Jobs;

public interface IMetadataRematerializationJobRepository : IJobRepository<MetadataRematerializationJob>
{
    /// <summary>Stages a new job in the required caller-owned transaction without flushing or committing.</summary>
    Task AddAsync(MetadataRematerializationJob job, CancellationToken ct = default);

    Task<MetadataRematerializationJob?> GetReusablePendingAsync(int platformId, CancellationToken ct = default);
    Task<Guid?> GetPendingIdAsync(int platformId, CancellationToken ct = default);
}

public interface IMetadataRematerializationTitles
{
    Task<int> CountAsync(int platformId, CancellationToken ct = default);
    Task<IReadOnlyList<int>> ReadPageAsync(int platformId, int afterTitleId, int limit, CancellationToken ct = default);
}
