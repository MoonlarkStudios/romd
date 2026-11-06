using ErrorOr;
using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Admin.Application.Storage.Files;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;

namespace Romd.Infrastructure.Storage;

/// <summary>Unbound full CD observations under supervised stopped custody. Catalog lookup and descriptor generation happen later.</summary>
public sealed class ChdCdImportInspector
{
    private readonly MediaProcessingOptions _options;
    private readonly IMediaWorkerSupervisor _supervisor;
    private readonly MediaProcessingRuntime _runtime;
    private readonly ChdNativeCandidate _native;
    private readonly TimeProvider _clock;
    public ChdCdImportInspector(MediaProcessingOptions options, IMediaWorkerSupervisor supervisor,
        MediaProcessingRuntime runtime, ChdNativeCandidate native, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(clock);
        this._options = options.ValidatedCopy();
        this._supervisor = supervisor;
        this._runtime = runtime;
        this._native = native;
        this._clock = clock;
    }
    internal async Task<MediaInspectionAdmission<RomCdInspection>> AcquireAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var borrowed = MediaProcessingRuntime.Current;
        return new(RunAsync, borrowed ?? await _runtime.AcquireAsync(ct: ct), borrowed is null);
    }
    public async Task<ErrorOr<RomCdInspection>> InspectAsync(ITempFile source, CancellationToken ct = default)
    {
        using var admission = await AcquireAsync(ct);
        return await admission.VerifyAsync(source, ct);
    }
    private async Task<(ErrorOr<RomCdInspection> Result, bool ProcessTreeStopped)> RunAsync(ITempFile source, CancellationToken ct)
    {
        if (source.FileSize < 124 || source.FileSize > Math.Min(_options.MaxArtifactBytes, ChdCdLimits.MaximumArtifactBytes))
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.LimitExceeded), true);
        if (source.FileSha256.IsEmpty || source.DatHashes is not { } hashes || hashes.BytesRead != source.FileSize)
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol), true);
        var result = await _supervisor.InspectChdCdAsync(source, ct);
        if (!result.ProcessTreeStopped)
        {
            source.Quarantine();
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.Containment), false);
        }
        if (!result.IsSuccess)
            return (MediaInspectionErrors.Failed(result.Failure ?? MediaWorkerFailure.Protocol), true);
        var reply = result.Reply!;
        if (!MediaWorkerObservationIdentity.Matches(reply.Identity, result.Execution, _options) || reply.Status != MediaWorkerStatus.Inspected ||
            reply.NativeIdentity is not { } observedNative || !_native.Matches(observedNative) || reply.CdInspection is not { } inspection ||
            inspection.Header.ArtifactBytes != source.FileSize || inspection.ArtifactSha256 != source.FileSha256.ToString() ||
            inspection.Header.LogicalBytes > (ulong)Math.Min(_options.MaxLogicalBytes, ChdCdLimits.MaximumLogicalBytes) ||
            inspection.Header.HunkBytes > ChdCdLimits.MaximumHunkBytes || reply.Inspection is not null || reply.ChdInspection is not null ||
            reply.Encoding is not null || reply.CdEncoding is not null || reply.CdVerification is not null || reply.ToolIdentity is not null)
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol), true);
        try
        { return (RomCdMeasurementMapper.MapInspection(source, inspection, observedNative, reply.Identity.WorkerBuild, _clock.GetUtcNow()), true); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or FormatException or OverflowException)
        { return (MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol), true); }
    }
}
