using ErrorOr;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Application.Common.Configuration;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Import;

public sealed record ServerImportFile(string RelativePath, long Bytes, DateTime LastWriteUtc);

public sealed record ServerImportSelection(IReadOnlyList<ServerImportFile> Files);

public sealed record ServerImportScan(
    IReadOnlyList<ServerImportFile> Files, int WaitingFiles, string? Warning);

public enum ServerImportCheck
{
    Queued,
    NoNewFiles,
    WaitingForFiles,
    Failed
}

public sealed record ServerImportActivity(
    DateTimeOffset CheckedAt, ServerImportCheck Outcome, int WaitingFiles, string? Message,
    Guid? JobId, int LastQueuedFiles);

public sealed record ServerImportJob(Guid Id, UploadPhase Phase, bool IsTerminal, bool HasRejectedFiles = false)
{
    public bool CanRetry => HasRejectedFiles && Phase == UploadPhase.Completed ||
        Phase is UploadPhase.CompletedWithErrors or UploadPhase.Failed or UploadPhase.Cancelled;
}

public sealed record ServerImportView(
    string? Directory, int? IntervalMinutes, DateTimeOffset? NextCheckAt,
    ServerImportActivity? Activity, ServerImportJob? Job)
{
    public static ServerImportView Create(
        IServerImportOptions options, TimeProvider clock, ServerImportActivity? activity = null, ServerImportJob? job = null) =>
        new(options.ServerImportDirectory, options.ServerImportIntervalMinutes,
            ServerImportSchedule.NextCheck(clock.GetUtcNow(), options.ServerImportIntervalMinutes), activity, job);
}

public interface IServerImportSource
{
    ErrorOr<ServerImportScan> Scan(string directory, DateTimeOffset now, CancellationToken ct);
    ErrorOr<Success> ValidateCapacity(IReadOnlyList<ServerImportFile> files);
}

public interface IServerImportStore
{
    Task AcquireAcceptanceLockAsync(CancellationToken ct);
    Task<ServerImportActivity?> ReadActivityAsync(string directory, CancellationToken ct);
    Task<ServerImportJob?> ReadJobAsync(Guid? jobId, CancellationToken ct);
    Task<IReadOnlyList<ServerImportFile>> FindUnsubmittedFilesAsync(
        string directory, IReadOnlyList<ServerImportFile> ready, CancellationToken ct);
    Task<ServerImportSelection?> ReadSelectionAsync(Guid jobId, CancellationToken ct);
    Task StageActivityAsync(string directory, ServerImportActivity activity, CancellationToken ct);
    void StageSelection(Guid jobId, ServerImportSelection selection);
}
