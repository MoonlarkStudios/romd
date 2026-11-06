using ErrorOr;

namespace Romd.Admin.Application.Ingestion.Preparation;

/// <summary>Captured input identity; the filename must already be a safe single path component.</summary>
public sealed record JobWorkspaceInput(Guid JobId, string FileName);

public interface IUploadWorkspacePreparation
{
    /// <summary>
    ///     Copies only bytes, without accepting a job. The owner must check request replay before
    ///     acquiring the lease and again before calling this method. The caller retains its stream.
    /// </summary>
    Task<ErrorOr<IPreparedJobWorkspace>> PrepareAsync(
        JobWorkspaceInput input, Stream source, IJobWorkspaceLease lease, CancellationToken ct = default);
}

public interface IReplaceDatWorkspacePreparation
{
    /// <summary>Copies reviewed bytes without saving a job. The caller retains its source stream.</summary>
    Task<IPreparedJobWorkspace> PrepareAsync(
        JobWorkspaceInput input, Stream source, IJobWorkspaceLease lease, CancellationToken ct = default);
}
