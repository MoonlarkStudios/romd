using ErrorOr;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;

namespace Romd.Infrastructure.Storage;

/// <summary>One exact CHD measurement and its container proof; boot layout is a separate logical observation.</summary>
public sealed record ChdVerifiedInspection
{
    internal ChdVerifiedInspection(RomVerification verification, RomContainerFacts.Chd container, RomLogicalInspection inspection, RomChdIntegrity integrity, RomPs2BootLayoutFacts? ps2Boot)
    {
        if (!integrity.MatchesMeasurement(verification) || ps2Boot is not null && ps2Boot.LogicalBytes != verification.ContentSize)
            throw new ArgumentException("CHD inspection evidence must bind the same measured artifact and logical content.");
        Verification = verification;
        Container = container;
        Inspection = inspection;
        Integrity = integrity;
        Ps2Boot = ps2Boot;
    }

    public RomVerification Verification { get; }
    public RomContainerFacts.Chd Container { get; }
    public RomLogicalInspection Inspection { get; }
    public RomChdIntegrity Integrity { get; }
    public RomPs2BootLayoutFacts? Ps2Boot { get; }
}

/// <summary>Supervised full CHD measurement. This does not approve serving, DVD eligibility or a canonical recipe.</summary>
/// <remarks>IMediaWorkerSupervisor is a trusted seam. The concrete MediaWorkerSupervisor authenticates the
/// packaged worker MVID, actual child identity and stopped custody; this verifier's label-shape checks do not.</remarks>
public sealed class ChdVerifier : IDisposable
{
    public const string Profile = "chd-disc-supervised/v1";
    private const string _inspectionProfile = "chd-import/v1";
    private readonly MediaProcessingOptions _options;
    private readonly IMediaWorkerSupervisor _supervisor;
    private readonly MediaProcessingRuntime _runtime;
    private readonly ChdNativeCandidate _native;
    private readonly RomChdDecoderBuild _decoder;
    private int _disposed;

    /// <summary>The candidate supplies frozen provenance only; this verifier never loads native code in its host.
    /// The supplied supervisor must authenticate the packaged child and certify its process-tree stop.</summary>
    public ChdVerifier(MediaProcessingOptions options, IMediaWorkerSupervisor supervisor,
        MediaProcessingRuntime runtime, ChdNativeCandidate native)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(native);
        _options = options.ValidatedCopy();
        _supervisor = supervisor;
        _runtime = runtime;
        _native = native;
        _decoder = ChdBuildEvidence.Decoder(native.ExpectedIdentity);
    }

    public async Task<ErrorOr<ChdVerifiedInspection>> VerifyAsync(ITempFile source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var admission = await AcquireAsync(ct);
        return await admission.VerifyAsync(source, ct);
    }

    internal async Task<MediaInspectionAdmission<ChdVerifiedInspection>> AcquireAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();
        var borrowed = MediaProcessingRuntime.Current;
        return new(RunAsync, borrowed ?? await _runtime.AcquireAsync(ct: ct), borrowed is null);
    }

    private async Task<(ErrorOr<ChdVerifiedInspection> Result, bool ProcessTreeStopped)> RunAsync(ITempFile source, CancellationToken ct)
    {
        if (source.FileSize < 124 || source.FileSize > Math.Min(_options.MaxArtifactBytes, ChdDvdLimits.MaximumArtifactBytes))
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.LimitExceeded), true);
        if (source.FileSha256.IsEmpty)
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol), true);
        var result = await _supervisor.InspectChdAsync(source, ct);
        if (!result.ProcessTreeStopped)
        {
            source.Quarantine();
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.Containment), false);
        }
        if (!result.IsSuccess)
            return (MediaInspectionErrors.Failed(result.Failure ?? MediaWorkerFailure.Protocol), true);
        try
        { return (Map(source, result), true); }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        {
            // Conversion failures occur after affirmative stop proof and must not retain otherwise safe capacity.
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol), true);
        }
    }

    private ErrorOr<ChdVerifiedInspection> Map(ITempFile source, MediaWorkerResult result)
    {
        var reply = result.Reply!;
        if (!MediaWorkerObservationIdentity.Matches(reply.Identity, result.Execution, _options) || reply.Status != MediaWorkerStatus.Inspected ||
            reply.Inspection is not { } measurement || reply.ChdInspection is not { } inspection ||
            reply.NativeIdentity is not { } native || !_native.Matches(native) ||
            reply.IsoInspection is not null || reply.Encoding is not null || reply.ChdRejection is not null ||
            reply.ChdEncoding is not null || reply.ToolIdentity is not null || reply.ChdEncodingRejection is not null || reply.ToolExecution is not null ||
            measurement.ArtifactSha256 != source.FileSha256.ToString() || measurement.ArtifactBytes != source.FileSize ||
            measurement.EncodingProfile != _inspectionProfile || measurement.DiscKind is not null ||
            measurement.LogicalBytes < 2048 || measurement.LogicalBytes > Math.Min(_options.MaxLogicalBytes, ChdDvdLimits.MaximumLogicalBytes) ||
            inspection.Header is not { } header || header.ArtifactBytes != source.FileSize ||
            header.LogicalBytes != (ulong)measurement.LogicalBytes || inspection.DvdMetadata is not { } metadata ||
            !Hash(metadata.NormalizedSha256, 64) ||
            !Hash(measurement.LogicalSha256, 64) || !Hash(measurement.Sha1, 40) || !Hash(measurement.Md5, 32) || !Hex(measurement.Crc32, 8) ||
            !Hash(inspection.StoredRawSha1, 40) || !Hash(inspection.ComputedRawSha1, 40) ||
            !Hash(inspection.StoredOverallSha1, 40) || !Hash(inspection.ComputedOverallSha1, 40))
            return MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol);

        var marker = new RomChdDvdMetadataFacts(checked((int)metadata.EntryCount), metadata.Tag, metadata.Flags,
            metadata.PayloadBytes, metadata.PayloadByte, metadata.Profile, Sha256.Parse(metadata.NormalizedSha256));
        string captured = FormattableString.Invariant($"chd-observed/v1;version={inspection.Version};logical={header.LogicalBytes};hunk={header.HunkBytes};unit={header.UnitBytes};count={header.HunkCount};codecs={header.Codec0:x8},{header.Codec1:x8},{header.Codec2:x8},{header.Codec3:x8};parent=none;metadata={marker.Profile}:{marker.EntryCount}:{marker.Tag:x8}:{marker.Flags}:{marker.PayloadBytes}:{marker.PayloadByte}:{marker.Fingerprint}");
        // inspect-chd accepts only parent-free version5 headers before native admission; its header DTO carries the remaining actual scalars.
        var parameters = new RomChdContainerParameters(inspection.Version, measurement.LogicalBytes, header.HunkBytes,
            header.UnitBytes, header.HunkCount, header.Codec0, header.Codec1, header.Codec2, header.Codec3,
            hasParent: false, marker, captured);
        var observed = DateTimeOffset.UtcNow;
        var verification = new RomVerification(source.FileSha256, source.FileSize, RomRepresentationEncoding.Chd,
            Sha256.Parse(measurement.LogicalSha256), measurement.LogicalBytes, Sha1.Parse(measurement.Sha1),
            Md5.Parse(measurement.Md5), Crc32.Parse(measurement.Crc32), Profile, _decoder.BuildLabel,
            reply.Identity.WorkerBuild, observed, captured);
        if (!RomChdIntegrity.TryCreate(verification, parameters, Sha1.Parse(inspection.StoredRawSha1),
                Sha1.Parse(inspection.ComputedRawSha1), Sha1.Parse(inspection.StoredOverallSha1),
                Sha1.Parse(inspection.ComputedOverallSha1), _decoder, RomChdIntegrity.ProfileVersion, observed, out var integrity) ||
            !TryMapBoot(reply.Ps2Inspection, measurement.LogicalBytes, reply.Identity.DecoderBuild, out var boot))
            return MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol);
        var container = new RomContainerFacts.Chd(inspection.Version, measurement.LogicalBytes, header.HunkBytes, header.UnitBytes, header.HunkCount,
            [header.Codec0, header.Codec1, header.Codec2, header.Codec3], false, Sha1.Parse(inspection.StoredRawSha1), Sha1.Parse(inspection.StoredOverallSha1),
            [new RomChdMetadataEntry(metadata.Tag, metadata.Flags, [metadata.PayloadByte])]);
        return new ChdVerifiedInspection(verification, container, RomLogicalInspectionMapper.Map(verification, measurement, reply.Identity.DecoderBuild, boot), integrity, boot);
    }

    private static bool TryMapBoot(MediaPs2Inspection? ps2, long logicalBytes, string inspectorBuild, out RomPs2BootLayoutFacts? boot)
    {
        boot = null;
        if (ps2 is null)
            return true;
        if (ps2.Profile != RomPs2BootLayoutFacts.ProfileVersion || !Enum.IsDefined(ps2.Status))
            return false;
        if (ps2.Status != Ps2DvdIdentificationStatus.Identified)
            return ps2.Facts is null;
        if (ps2.Facts is not { } facts || facts.LogicalBytes != logicalBytes)
            return false;
        boot = new(facts.LogicalBytes, facts.VolumeBlocks, facts.PrimaryDescriptorBlock, facts.RootDirectoryBlock,
            facts.RootDirectoryBytes, facts.SystemCnfBlock, facts.SystemCnfBytes, facts.BootExecutableBlock,
            facts.BootExecutableBytes, facts.ObservedBoot2Value, facts.BootPath, facts.BootSerial, facts.EntryPoint,
            facts.ProgramHeaderCount, ps2.Profile, inspectorBuild);
        return true;
    }

    private static bool Hex(string? value, int length) => value is not null && value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool Hash(string? value, int length) => Hex(value, length) && value!.Any(character => character != '0');

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
