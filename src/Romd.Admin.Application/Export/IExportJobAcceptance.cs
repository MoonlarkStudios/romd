using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Export;

/// <summary>Stages one export demand and its durable dispatch in the caller's acceptance transaction.</summary>
public interface IExportJobAcceptance
{
    /// <summary>Locks the global export acceptance key through commit, then checks for pending or active work.</summary>
    Task<bool> LockAndHasPendingStagedAsync(CancellationToken ct = default);

    /// <summary>Stages a new job without flushing or committing. Requires the caller's active transaction.</summary>
    Task AddStagedAsync(ExportJob job, CancellationToken ct = default);
}
