using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;
using Romd.Media.Chd;
using Romd.Media.Inspection;

namespace Romd.Infrastructure.Storage;

public enum MediaWorkerFailure { Startup, Protocol, OutputLimit, Crash, WallDeadline, CpuDeadline, Malformed, Unsupported, LimitExceeded, IoFailure, Containment, SourceCustody }
public enum MediaWorkerDiagnosticKind { None, ChildErrorOutput, RuntimeMemoryFailure, RuntimeStartupFailure }
public sealed record MediaWorkerExecution(int ProcessId, int? ExitCode, long StandardOutputBytes,
    long StandardErrorBytes, MediaWorkerDiagnosticKind DiagnosticKind);
public sealed record MediaWorkerResult(MediaWorkerReply? Reply, MediaWorkerFailure? Failure, MediaWorkerExecution? Execution = null,
    bool ProcessTreeStopped = true)
{
    public bool IsSuccess => Failure is null && Reply is not null && ProcessTreeStopped;
    public static MediaWorkerResult Failed(MediaWorkerFailure failure) => new(null, failure);
}

public interface IMediaWorkerSupervisor
{
    bool IsAvailable { get; }
    Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default);
    Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default);
    Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default);
    /// <summary>Old supervisors refuse CHD without launching a child or issuing evidence.</summary>
    Task<MediaWorkerResult> InspectChdAsync(ITempFile source, CancellationToken ct = default) =>
        Task.FromResult(MediaWorkerResult.Failed(MediaWorkerFailure.Unsupported));
    /// <summary>Measures CD track projections separately from the singleton DVD import contract.</summary>
    Task<MediaWorkerResult> InspectChdCdAsync(ITempFile source, CancellationToken ct = default) =>
        Task.FromResult(MediaWorkerResult.Failed(MediaWorkerFailure.Unsupported));
    Task<MediaWorkerResult> EncodeChdCdAsync(ITempFile request, IReadOnlyList<ITempFile> originalsAndToolCue,
        IWritableTempFile output, ChdmanCandidate candidate, CancellationToken ct = default) =>
        Task.FromResult(MediaWorkerResult.Failed(MediaWorkerFailure.Unsupported));
    Task<MediaWorkerResult> VerifyChdCdAsync(ITempFile artifact, ITempFile request,
        IReadOnlyList<ITempFile> originalsAndToolCue, CancellationToken ct = default) =>
        Task.FromResult(MediaWorkerResult.Failed(MediaWorkerFailure.Unsupported));
    Task<MediaWorkerResult> ReverifyChdCdAsync(ITempFile artifact, ITempFile request,
        IReadOnlyList<ITempFile> storedFiles, CancellationToken ct = default) =>
        Task.FromResult(MediaWorkerResult.Failed(MediaWorkerFailure.Unsupported));
    /// <summary>Old supervisors refuse CHD encoding without launching a child or issuing artifact facts.</summary>
    Task<MediaWorkerResult> EncodeChdAsync(ITempFile source, IWritableTempFile output, ChdmanCandidate candidate, CancellationToken ct = default) =>
        Task.FromResult(MediaWorkerResult.Failed(MediaWorkerFailure.Unsupported));
    /// <summary>Old supervisors refuse DVD extraction without launching or issuing measured bytes.</summary>
    Task<MediaWorkerResult> ExtractChdDvdAsync(ITempFile source, IWritableTempFile output, ChdmanCandidate candidate,
        long logicalBytes, CancellationToken ct = default) => Task.FromResult(MediaWorkerResult.Failed(MediaWorkerFailure.Unsupported));
    Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes, RomIsoDiscKind discKind, CancellationToken ct = default);
}
