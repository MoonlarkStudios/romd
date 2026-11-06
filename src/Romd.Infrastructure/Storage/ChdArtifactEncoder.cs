using ErrorOr;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;

namespace Romd.Infrastructure.Storage;

/// <summary>Actual codec receipt. Independent inspection and publication are separate responsibilities.</summary>
public sealed record ChdEncodedArtifact(ITempFile Artifact, TempFileArtifactMeasurement Measurement, string RecipeVersion, string EncoderBuild, RomChdEncoderBuild NativeEncoder);

/// <summary>Supervised DVD extraction and encoding. It establishes bytes and codec identity, never media eligibility or ownership.</summary>
public sealed class ChdArtifactEncoder
{
    private readonly IMediaWorkerSupervisor _supervisor;
    private readonly MediaProcessingOptions _limits;
    private readonly ChdmanCandidate _tool;
    private readonly RomChdEncoderBuild _encoder;
    public ChdArtifactEncoder(IMediaWorkerSupervisor supervisor, MediaProcessingOptions limits, ChdmanCandidate tool)
    {
        _supervisor = supervisor;
        _limits = limits.ValidatedCopy();
        _tool = tool;
        _encoder = ChdBuildEvidence.Encoder(tool.ExpectedIdentity);
    }
    public async Task<ErrorOr<ITempFile>> ExtractAsync(ITempFile source, IWritableTempFile output, RomLogicalIdentity logical, CancellationToken ct = default)
    {
        if (source.FileSize is < 124 or > ChdDvdLimits.MaximumArtifactBytes || source.FileSize > _limits.MaxArtifactBytes ||
            logical.Size is < 2048 or > ChdDvdLimits.MaximumLogicalBytes || logical.Size % 2048 != 0 || logical.Size > _limits.MaxLogicalBytes ||
            output.MaximumBytes < logical.Size || output.MaximumBytes > _limits.MaxArtifactBytes)
            return MediaInspectionErrors.Failed(MediaWorkerFailure.LimitExceeded);
        if (source.FileSha256.IsEmpty || !Path.IsPathFullyQualified(source.Path) || !Path.IsPathFullyQualified(output.Path) || SamePath(source.Path, output.Path))
            return MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol);
        if (MediaProcessingRuntime.Current is not { } lease || lease.Released)
            return RomMediaVerificationErrors.Custody;
        bool stopped = lease.ProcessesStopped;
        if (!stopped)
        { try { Quarantine(source, output); } finally { lease.Retain(); } return RomMediaVerificationErrors.Custody; }
        try
        {
            ct.ThrowIfCancellationRequested();
            stopped = false;
            var result = await _supervisor.ExtractChdDvdAsync(source, output, _tool, logical.Size, ct);
            stopped = result.ProcessTreeStopped;
            if (!stopped)
                return RomMediaVerificationErrors.Custody;
            ct.ThrowIfCancellationRequested();
            if (!result.IsSuccess)
                return MediaInspectionErrors.Failed(result.Failure ?? MediaWorkerFailure.Protocol);
            if (!TryMeasureExtraction(result, source, logical, output, out var measured))
                return MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol);
            var sealedResult = await output.SealAsync(measured, ct);
            if (sealedResult.IsError)
                return sealedResult.Errors;
            if (sealedResult.Value.FileSha256 != logical.Sha256 || sealedResult.Value.FileSize != logical.Size || sealedResult.Value.Path != output.Path)
                return MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol);
            return ErrorOrFactory.From(sealedResult.Value);
        }
        catch (MediaWorkerStoppedCancellationException) { stopped = true; throw; }
        catch (Exception) when (!stopped) { ct.ThrowIfCancellationRequested(); return RomMediaVerificationErrors.Custody; }
        finally
        {
            if (!stopped)
            {
                lease.Retain();
                try
                { source.Quarantine(); }
                finally { output.Quarantine(); }
            }
        }
    }
    private bool TryMeasureExtraction(MediaWorkerResult result, ITempFile source, RomLogicalIdentity proof, IWritableTempFile output,
        out TempFileArtifactMeasurement measurement)
    {
        measurement = null!;
        var reply = result.Reply!;
        if (!ValidIdentity(reply.Identity, result.Execution) || reply.Status != MediaWorkerStatus.Encoded ||
            reply.ChdExtraction is not { } facts || reply.ToolIdentity is not { } tool || !_tool.Matches(tool) ||
            reply.ToolExecution is not { } execution || execution.ProcessId <= 0 || execution.ProcessId == reply.Identity.ProcessId ||
            execution.ExitCode != 0 || execution.StandardOutputBytes is < 0 or > (256L << 10) ||
            execution.StandardErrorBytes < 0 || execution.StandardErrorBytes > (256L << 10) - execution.StandardOutputBytes ||
            reply.Inspection is not null || reply.IsoInspection is not null || reply.Encoding is not null ||
            reply.NativeIdentity is not null || reply.ChdInspection is not null || reply.ChdRejection is not null ||
            reply.ChdEncoding is not null || reply.ChdEncodingRejection is not null || reply.Ps2Inspection is not null ||
            facts.SourceSha256 != source.FileSha256.ToString() || facts.SourceBytes != source.FileSize ||
            facts.LogicalBytes != proof.Size || facts.LogicalBytes > output.MaximumBytes ||
            facts.LogicalSha256 != proof.Sha256.ToString() || facts.Sha1 != proof.Sha1.ToString() ||
            facts.Md5 != proof.Md5.ToString() || facts.Crc32 != proof.Crc32.ToString() ||
            facts.Profile != ChdDvdEncodingService.ExtractionProfile || facts.PriorityKind != "unix-nice" || facts.PriorityValue is < 10 or > 19 ||
            !Hash(facts.LogicalSha256, 64) || !Hash(facts.Sha1, 40) || !Hash(facts.Md5, 32) ||
            facts.Crc32 is not { Length: 8 } crcText || !crcText.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !Sha256.TryParse(facts.LogicalSha256, out var sha256) || sha256.IsEmpty ||
            !Md5.TryParse(facts.Md5, out var md5) || md5.IsEmpty || !Sha1.TryParse(facts.Sha1, out var sha1) || sha1.IsEmpty ||
            !Crc32.TryParse(crcText, out var crc32) || crc32.IsEmpty)
            return false;
        measurement = new(sha256, md5, sha1, facts.LogicalBytes);
        return true;
    }

    private static bool SamePath(string first, string second) => string.Equals(first, second,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);


    public async Task<ErrorOr<ChdEncodedArtifact>> EncodeAsync(ITempFile source, IWritableTempFile output,
        long logicalBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);
        ct.ThrowIfCancellationRequested();
        if (logicalBytes < 2048 || logicalBytes > Math.Min(_limits.MaxLogicalBytes, ChdDvdLimits.MaximumLogicalBytes) || logicalBytes % 2048 != 0 ||
            source.FileSize != logicalBytes || source.FileSize > _limits.MaxArtifactBytes ||
            output.MaximumBytes is < 124 || output.MaximumBytes > Math.Min(_limits.MaxArtifactBytes, ChdDvdLimits.MaximumArtifactBytes))
            return MediaInspectionErrors.Failed(MediaWorkerFailure.LimitExceeded);
        if (source.FileSha256.IsEmpty || !Path.IsPathFullyQualified(source.Path) || !Path.IsPathFullyQualified(output.Path) ||
            string.Equals(source.Path, output.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol);
        if (MediaProcessingRuntime.Current is not { } lease || lease.Released)
            return RomMediaVerificationErrors.Custody;
        if (!lease.ProcessesStopped)
        {
            try
            { Quarantine(source, output); }
            catch (Exception) { }
            finally { lease.Retain(); }
            return MediaInspectionErrors.Failed(MediaWorkerFailure.Containment);
        }
        bool stopped = false;
        try
        {
            var result = await _supervisor.EncodeChdAsync(source, output, _tool, ct);
            stopped = result.ProcessTreeStopped;
            if (!stopped)
            {
                Quarantine(source, output);
                return MediaInspectionErrors.Failed(MediaWorkerFailure.Containment);
            }
            ct.ThrowIfCancellationRequested();
            if (!result.IsSuccess)
                return MediaInspectionErrors.Failed(result.Failure ?? MediaWorkerFailure.Protocol);
            if (!TryMeasure(result, source, output, logicalBytes, out var measurement))
                return MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol);
            var sealedOutput = await output.SealAsync(measurement, ct);
            if (sealedOutput.IsError)
                return sealedOutput.Errors;
            var artifact = sealedOutput.Value;
            if (artifact.FileSha256 != measurement.Sha256 || artifact.FileSize != measurement.Size || artifact.Path != output.Path)
                return MediaInspectionErrors.Failed(MediaWorkerFailure.Protocol);
            return new ChdEncodedArtifact(artifact, measurement, result.Reply!.ChdEncoding!.RecipeVersion,
                _encoder.BuildLabel, _encoder);
        }
        catch (MediaWorkerStoppedCancellationException)
        {
            stopped = true;
            throw;
        }
        catch (Exception) when (!stopped)
        {
            try
            { Quarantine(source, output); }
            catch (Exception) { }
            ct.ThrowIfCancellationRequested();
            return MediaInspectionErrors.Failed(MediaWorkerFailure.Containment);
        }
        finally { if (!stopped) lease.Retain(); }
    }

    private bool TryMeasure(MediaWorkerResult result, ITempFile source, IWritableTempFile output, long logicalBytes,
        out TempFileArtifactMeasurement measurement)
    {
        measurement = null!;
        var reply = result.Reply!;
        if (!ValidIdentity(reply.Identity, result.Execution, _limits.ChdEncodeCpuTimeoutSeconds) || reply.Status != MediaWorkerStatus.Encoded ||
            reply.ChdEncoding is not { } facts || reply.ToolIdentity is not { } tool || !_tool.Matches(tool) ||
            reply.ToolExecution is not { } execution || execution.ProcessId <= 0 || execution.ProcessId == reply.Identity.ProcessId ||
            execution.ExitCode != 0 || execution.StandardOutputBytes is < 0 or > (256L << 10) ||
            execution.StandardErrorBytes < 0 || execution.StandardErrorBytes > (256L << 10) - execution.StandardOutputBytes ||
            reply.Inspection is not null || reply.IsoInspection is not null || reply.Encoding is not null ||
            reply.NativeIdentity is not null || reply.ChdInspection is not null || reply.ChdRejection is not null ||
            reply.ChdEncodingRejection is not null || reply.Ps2Inspection is not null || reply.ChdExtraction is not null ||
            facts.SourceSha256 != source.FileSha256.ToString() || facts.SourceBytes != source.FileSize || facts.LogicalBytes != logicalBytes ||
            facts.ArtifactBytes < 124 || facts.ArtifactBytes > output.MaximumBytes ||
            facts.Header is not { } header || header.ArtifactBytes != facts.ArtifactBytes || header.LogicalBytes != (ulong)logicalBytes ||
            !ChdDvdCanonicalRecipe.Matches(header) ||
            facts.RecipeVersion != ChdDvdCanonicalRecipe.Id || facts.EncodingProfile != ChdDvdCanonicalRecipe.EncoderProfile ||
            facts.PriorityKind != "unix-nice" || facts.PriorityValue is < 10 or > 19 ||
            !Hash(facts.ArtifactSha256, 64) || !Hash(facts.ArtifactMd5, 32) || !Hash(facts.ArtifactSha1, 40) ||
            !Sha256.TryParse(facts.ArtifactSha256, out var sha256) || sha256.IsEmpty ||
            !Md5.TryParse(facts.ArtifactMd5, out var md5) || md5.IsEmpty || !Sha1.TryParse(facts.ArtifactSha1, out var sha1) || sha1.IsEmpty)
            return false;
        measurement = new(sha256, md5, sha1, facts.ArtifactBytes);
        return true;
    }

    private bool ValidIdentity(MediaWorkerIdentity? identity, MediaWorkerExecution? execution, int? cpuSeconds = null)
    {
        const string workerPrefix = "romd-media-worker/mvid:";
        return identity is not null && execution is not null && execution.ProcessId > 0 && execution.ExitCode == 0 &&
            execution.ProcessId == identity.ProcessId && execution.StandardOutputBytes is > 0 and <= MediaWorkerProtocol.MaximumOutputBytes &&
            execution.StandardErrorBytes is >= 0 and <= 16384 &&
            (OperatingSystem.IsWindows() || identity.SessionId == identity.ProcessId) &&
            identity.DecoderBuild == MediaInspectionService.DecoderBuild && identity.WorkerBuild is { } worker &&
            worker.StartsWith(workerPrefix, StringComparison.Ordinal) && Guid.TryParseExact(worker[workerPrefix.Length..], "D", out var mvid) && mvid != Guid.Empty &&
            identity.GcHeapLimitBytes == _limits.MemoryBytes && identity.GcAvailableBytes > 0 && identity.GcAvailableBytes <= _limits.MemoryBytes &&
            identity.GcRegionRangeBytes >= 0 && (OperatingSystem.IsLinux()
                ? identity.LinuxLimits == new MediaWorkerLimits(_limits.LinuxAddressSpaceBytes, _limits.LinuxDataBytes, cpuSeconds ?? _limits.CpuTimeoutSeconds, 0)
                : identity.LinuxLimits is null);
    }

    private static bool Hash(string? value, int length) => value is not null && value.Length == length &&
        value.Any(character => character != '0') && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Quarantine(ITempFile source, IWritableTempFile output)
    {
        try
        { source.Quarantine(); }
        finally { output.Quarantine(); }
    }
}
