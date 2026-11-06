using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Catalog;

public sealed record OriginalDiscDescriptorSource(string RelativePath, int FileId, Sha256 Sha256, long Size);

public interface IOriginalDiscDescriptorRepository
{
    /// <summary>Stages one descriptor and its audit without flushing. The caller owns the transaction and commit.</summary>
    Task RecordStagedAsync(Guid jobId, string relativePath, string entryPoint, int fileId, OriginalCueDescriptor parsed, CancellationToken ct);
    Task<IReadOnlyList<OriginalDiscDescriptorSource>> ReadUnattachedMatchedCuesAsync(Guid jobId, CancellationToken ct);
    /// <summary>Rechecks exact backing under its file lock, then stages the matched descriptor without flushing.</summary>
    Task RecordMatchedStagedAsync(Guid jobId, OriginalDiscDescriptorSource source, OriginalCueDescriptor parsed, CancellationToken ct);
    /// <summary>Stages successful journal bindings and returns existing layout IDs. The worker flushes before staging demands.</summary>
    Task<IReadOnlyList<int>> ReconcileStagedAsync(Guid jobId, CancellationToken ct);
}
