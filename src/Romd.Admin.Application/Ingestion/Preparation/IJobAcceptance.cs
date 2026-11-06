using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Preparation;

public interface IUploadJobAcceptance
{
    /// <summary>Requires the caller's transaction; stages only, without flushing or committing.</summary>
    Task AddStagedAsync(UploadJob job, CancellationToken ct = default);
}

public interface IReplaceDatJobAcceptance
{
    /// <summary>Requires the caller's transaction; stages only, without flushing or committing.</summary>
    Task AddStagedAsync(ReplaceDatJob job, CancellationToken ct = default);
}
