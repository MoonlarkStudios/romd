using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Import;

namespace Romd.Infrastructure.Jobs.Lifecycle;

/// <summary>
///     Move-mode path imports delete source originals only after the runner durably persisted a
///     Completed phase. <see cref="ImportSourceCleanup" /> re-checks that gate and the per-item storage
///     proof; the orphaned-job sweep is the durable backstop for a crash after persistence.
/// </summary>
public sealed class MoveImportCompletionCleanup(ImportSourceCleanup cleanup) : IJobCompletionCleanup<UploadJob>
{
    public Task RunAfterCompletionPersistedAsync(UploadJob job, CancellationToken ct = default) =>
        job is { ImportMove: true, PhaseEnum: UploadPhase.Completed }
            ? cleanup.RunCompletedAsync(job, ct)
            : Task.CompletedTask;
}
