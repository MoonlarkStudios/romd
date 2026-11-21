using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Import;

namespace Romd.Infrastructure.Jobs.Lifecycle;

/// <summary>Applies move cleanup after the runner durably persists its completion.</summary>
public sealed class MoveImportCompletionCleanup(ImportSourceCleanup cleanup) : IJobCompletionCleanup<UploadJob>
{
    public Task RunAfterCompletionPersistedAsync(UploadJob job, CancellationToken ct = default) =>
        cleanup.RunCompletedAsync(job, ct);
}
