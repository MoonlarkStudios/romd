using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media.Inspection;
using Romd.Media.Nintendo;
using Romd.Media.PlayStation;
using Romd.Media.Rvz;

namespace Romd.Infrastructure.Storage;

public sealed record RvzInspection(RomVerification Verification, string EncodingProfile, RomContainerFacts.Rvz Container, RomLogicalInspection Inspection,
    RomIsoDiscKind? DiscKind = null);
public sealed record IsoInspection(RomVerification Verification, RomLogicalInspection Inspection, RomIsoEligibility Eligibility);

/// <summary>Shared inspection and encoding admission; only complete inspection results issue evidence.</summary>
public sealed class RvzVerifier : IDisposable
{
    public const string Profile = "rvz-disc-supervised/v1";
    public const string IsoProfile = "nintendo-iso-supervised/v1";
    private readonly MediaProcessingOptions _options;
    private readonly MediaProcessingRuntime _runtime;
    private int _disposed;
    private readonly IMediaWorkerSupervisor _supervisor;
    /// <summary>Test-local compatibility constructor. Production supplies the shared runtime explicitly.</summary>
    public RvzVerifier(MediaProcessingOptions options) : this(options, new MediaWorkerSupervisor(options)) { }
    public RvzVerifier(MediaProcessingOptions options, IMediaWorkerSupervisor supervisor)
        : this(options, supervisor, supervisor is MediaWorkerSupervisor local ? local.Runtime : MediaProcessingRuntime.TestLocal(options)) { }
    public RvzVerifier(MediaProcessingOptions options, IMediaWorkerSupervisor supervisor, MediaProcessingRuntime runtime)
    {
        _options = options.ValidatedCopy();
        _runtime = runtime;
        _supervisor = supervisor;
    }
    public async Task<ErrorOr<RvzInspection>> VerifyAsync(ITempFile source, CancellationToken ct = default)
    {
        using var admission = await AcquireAsync(ct);
        return await admission.VerifyAsync(source, ct);
    }
    public async Task<ErrorOr<IsoInspection>> InspectIsoAsync(ITempFile source, CancellationToken ct = default)
    {
        using var admission = await AcquireAsync(ct);
        return await admission.InspectIsoAsync(source, ct);
    }
    internal async Task<Admission> AcquireAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();
        var borrowed = MediaProcessingRuntime.Current;
        return new(this, borrowed ?? await _runtime.AcquireAsync(ct: ct), borrowed is null);
    }
    internal sealed class Admission(RvzVerifier owner, MediaProcessingRuntime.MediaProcessingReservation lease, bool ownsLease) : IDisposable
    {
        private int _state;
        public Task<ErrorOr<RvzInspection>> VerifyAsync(ITempFile source, CancellationToken ct) =>
            ExecuteAsync(() => owner.RunAsync(source, ct), () => source.Quarantine(), ct);
        public Task<ErrorOr<IsoInspection>> InspectIsoAsync(ITempFile source, CancellationToken ct) =>
            ExecuteAsync(() => owner.RunIsoAsync(source, ct), () => source.Quarantine(), ct);
        public Task<ErrorOr<RomOriginalMediaIdentification>> IdentifyOriginalIsoAsync(ITempFile source, CancellationToken ct) =>
            ExecuteAsync(() => owner.RunOriginalIsoAsync(source, ct), () => source.Quarantine(), ct);
        public Task<ErrorOr<MediaEncoding>> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes, RomIsoDiscKind discKind, CancellationToken ct) =>
            ExecuteAsync(() => owner.RunEncodeAsync(source, output, logicalBytes, discKind, ct), () =>
            {
                try
                { source.Quarantine(); }
                finally { output.Quarantine(); }
            }, ct);
        private async Task<ErrorOr<T>> ExecuteAsync<T>(Func<Task<(ErrorOr<T> Result, bool ProcessTreeStopped)>> run,
            Action quarantine, CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                throw new InvalidOperationException("An inspection reservation can be used only once.");
            bool stopped = false;
            using var active = lease.Activate();
            try
            {
                if (ct.IsCancellationRequested)
                {
                    stopped = true; // The supervised operation has not been invoked.
                    ct.ThrowIfCancellationRequested();
                }
                var outcome = await run();
                stopped = outcome.ProcessTreeStopped;
                ct.ThrowIfCancellationRequested();
                return outcome.Result;
            }
            catch (MediaWorkerStoppedCancellationException)
            {
                stopped = true;
                throw;
            }
            catch (Exception) when (!stopped)
            {
                // A throw without a stop certificate cannot free media or staging capacity.
                // Quarantine persistence failure also remains a permanent containment failure.
                try
                { quarantine(); }
                catch (Exception) { }
                ct.ThrowIfCancellationRequested();
                return MediaInspectionErrors.Failed(MediaWorkerFailure.Containment);
            }
            finally
            {
                Interlocked.Exchange(ref _state, 3);
                if (!stopped)
                    lease.Retain();
                else if (ownsLease)
                    lease.Dispose();
            }
        }
        public void Dispose()
        {
            while (true)
            {
                int state = Volatile.Read(ref _state);
                if (state is 2 or 3)
                    return;
                if (Interlocked.CompareExchange(ref _state, state == 0 ? 3 : 2, state) != state)
                    continue;
                if (state == 0 && ownsLease)
                    lease.Dispose();
                return;
            }
        }
    }
    private async Task<(ErrorOr<RvzInspection> Result, bool ProcessTreeStopped)> RunAsync(ITempFile source, CancellationToken ct)
    {
        if (source.FileSize < 4 || source.FileSize > _options.MaxArtifactBytes)
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.LimitExceeded), true);
        var result = await _supervisor.InspectAsync(source, ct);
        if (!result.ProcessTreeStopped)
            source.Quarantine();
        if (!result.IsSuccess)
            return (MediaInspectionErrors.Failed(result.Failure ?? MediaWorkerFailure.Protocol), result.ProcessTreeStopped);
        var facts = result.Reply!.Inspection!;
        if (facts.RvzParameters is not { } captured)
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol), result.ProcessTreeStopped);
        RomContainerFacts.Rvz container;
        try
        {
            container = new(captured.Version, captured.DiscType, captured.Compression, captured.CompressionLevel, captured.ChunkSize,
                captured.HasWiiPartitions, facts.LogicalBytes);
        }
        catch (ArgumentException)
        {
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol), result.ProcessTreeStopped);
        }
        var proof = new RomVerification(source.FileSha256, source.FileSize, RomRepresentationEncoding.Rvz,
            Sha256.Parse(facts.LogicalSha256), facts.LogicalBytes, Sha1.Parse(facts.Sha1), Md5.Parse(facts.Md5), Crc32.Parse(facts.Crc32),
            Profile, result.Reply.Identity.DecoderBuild, result.Reply.Identity.WorkerBuild, DateTimeOffset.UtcNow,
            capturedEncodingProfile: facts.EncodingProfile);
        return (new RvzInspection(proof, facts.EncodingProfile, container, RomLogicalInspectionMapper.Map(proof, facts, result.Reply.Identity.DecoderBuild), RvzDiscFamily.For(facts.DiscKind)?.DiscKind), result.ProcessTreeStopped);
    }
    private async Task<(ErrorOr<IsoInspection> Result, bool ProcessTreeStopped)> RunIsoAsync(ITempFile source, CancellationToken ct)
    {
        // Preserve the existing Nintendo-only inspection contract for its legacy callers.
        var outcome = await RunOriginalIsoAsync(source, ct, nintendoOnly: true);
        if (outcome.Result.IsError)
            return (outcome.Result.Errors, outcome.ProcessTreeStopped);
        if (outcome.Result.Value.Eligibility is not { } eligibility)
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol), outcome.ProcessTreeStopped);
        return (new IsoInspection(outcome.Result.Value.Verification, outcome.Result.Value.Inspection, eligibility), outcome.ProcessTreeStopped);
    }
    private async Task<(ErrorOr<RomOriginalMediaIdentification> Result, bool ProcessTreeStopped)> RunOriginalIsoAsync(
        ITempFile source, CancellationToken ct, bool nintendoOnly = false)
    {
        if (source.FileSize < 0 || source.FileSize > _options.MaxArtifactBytes)
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.LimitExceeded), true);
        var result = await _supervisor.InspectIsoAsync(source, ct);
        if (!result.ProcessTreeStopped)
            source.Quarantine();
        if (!result.IsSuccess)
            return (MediaInspectionErrors.Failed(result.Failure ?? MediaWorkerFailure.Protocol), result.ProcessTreeStopped);
        if (result.Reply!.Inspection is not { } facts || result.Reply.IsoInspection is not { } iso)
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol), result.ProcessTreeStopped);
        var observed = DateTimeOffset.UtcNow;
        // The legacy Nintendo-only contract carries neither PS2 boot nor PSP layout observations.
        var measured = nintendoOnly ? facts with { Psp = null } : facts;
        var ps2 = nintendoOnly ? null : result.Reply.Ps2Inspection;
        var proof = new RomVerification(source.FileSha256, source.FileSize, RomRepresentationEncoding.Identity,
            Sha256.Parse(facts.LogicalSha256), facts.LogicalBytes, Sha1.Parse(facts.Sha1), Md5.Parse(facts.Md5), Crc32.Parse(facts.Crc32),
            measured.Psp is not null ? RomPspLayoutFacts.Profile : ps2 is null ? IsoProfile : RomPs2BootObservation.MeasurementProfile,
            result.Reply.Identity.DecoderBuild, result.Reply.Identity.WorkerBuild, observed);
        if (ps2 is not null)
        {
            RomPs2BootObservation? boot = null;
            if (ps2.Status == Ps2DvdIdentificationStatus.Identified && ps2.Facts is { } identified)
                boot = new(proof, new(identified.LogicalBytes, identified.VolumeBlocks, identified.PrimaryDescriptorBlock,
                    identified.RootDirectoryBlock, identified.RootDirectoryBytes, identified.SystemCnfBlock, identified.SystemCnfBytes,
                    identified.BootExecutableBlock, identified.BootExecutableBytes, identified.ObservedBoot2Value, identified.BootPath,
                    identified.BootSerial, identified.EntryPoint, identified.ProgramHeaderCount, ps2.Profile, proof.DecoderBuild));
            return (new RomOriginalMediaIdentification(proof, RomLogicalInspectionMapper.Map(proof, measured, result.Reply.Identity.DecoderBuild, boot?.Boot), null, boot), result.ProcessTreeStopped);
        }
        var layout = iso.Layout;
        RomIsoDiscKind? kind = RvzDiscFamily.For(layout?.Kind)?.DiscKind;
        var eligibility = new RomIsoEligibility(proof.ContentSha256, proof.ContentSize, "nintendo-iso", iso.Profile,
            result.Reply.Identity.DecoderBuild, iso.Status == NintendoIsoInspectionStatus.Eligible
                ? RomIsoEligibilityStatus.Supported : RomIsoEligibilityStatus.Unsupported,
            kind, layout?.WiiPartitionCount ?? 0, layout?.StandardKeyPartitions ?? 0, layout?.KoreanKeyPartitions ?? 0, observed);
        return (new RomOriginalMediaIdentification(proof, RomLogicalInspectionMapper.Map(proof, measured, result.Reply.Identity.DecoderBuild), eligibility), result.ProcessTreeStopped);
    }
    private async Task<(ErrorOr<MediaEncoding> Result, bool ProcessTreeStopped)> RunEncodeAsync(ITempFile source,
        IWritableTempFile output, long logicalBytes, RomIsoDiscKind discKind, CancellationToken ct)
    {
        if (RvzDiscFamily.For(discKind) is not { } family)
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.Unsupported), true);
        var result = await _supervisor.EncodeAsync(source, output, logicalBytes, discKind, ct);
        if (!result.ProcessTreeStopped)
        { source.Quarantine(); output.Quarantine(); }
        if (!result.IsSuccess)
            return (MediaInspectionErrors.Failed(result.Failure ?? MediaWorkerFailure.Protocol), result.ProcessTreeStopped);
        if (result.Reply!.Status != MediaWorkerStatus.Encoded || result.Reply.Encoding is not { } facts ||
            result.Reply.Inspection is not null || result.Reply.IsoInspection is not null ||
            facts.SourceSha256 != source.FileSha256.ToString() || facts.SourceBytes != source.FileSize || facts.LogicalBytes != logicalBytes ||
            facts.EncoderBuild != family.Codec.EncoderBuild ||
            facts.RecipeVersion != RvzCanonicalRecipe.Id || facts.EncodingProfile != Romd.Media.Inspection.MediaWorkerProtocol.CanonicalEncodingProfile ||
            facts.ArtifactBytes <= 0 || facts.ArtifactBytes > output.MaximumBytes ||
            !Sha256.TryParse(facts.ArtifactSha256, out var sha256) || sha256.IsEmpty ||
            !Md5.TryParse(facts.ArtifactMd5, out var md5) || md5.IsEmpty || !Sha1.TryParse(facts.ArtifactSha1, out var sha1) || sha1.IsEmpty)
            return (MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol), result.ProcessTreeStopped);
        return (facts, result.ProcessTreeStopped);
    }
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

internal static class MediaInspectionErrors
{
    public static Error Failed(MediaWorkerFailure failure) => Error.Validation("Rom.Media." + failure, failure switch
    {
        MediaWorkerFailure.Startup => "The dedicated media worker is unavailable or invalid.",
        MediaWorkerFailure.Malformed => "The media is malformed or its complete output could not be verified.",
        MediaWorkerFailure.Unsupported => "The media operation or format is unsupported.",
        MediaWorkerFailure.LimitExceeded => "The media exceeds the inspection limits.",
        MediaWorkerFailure.WallDeadline => "Media inspection exceeded this file's wall-clock deadline.",
        MediaWorkerFailure.CpuDeadline => "Media inspection exceeded this file's CPU deadline.",
        MediaWorkerFailure.Crash => "The media child exited unsuccessfully.",
        MediaWorkerFailure.OutputLimit => "The media child exceeded its output or diagnostic limit.",
        MediaWorkerFailure.IoFailure => "The media child could not read the staged artifact.",
        MediaWorkerFailure.Containment => "Media child containment could not be confirmed; inspection is unavailable.",
        MediaWorkerFailure.SourceCustody => "The temporary input or output's durable child custody could not be established or released.",
        _ => "The media child returned invalid or inconsistent output."
    });
}
