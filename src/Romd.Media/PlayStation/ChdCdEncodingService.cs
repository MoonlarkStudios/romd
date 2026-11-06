using System.Text.Json;
using Romd.Media.Inspection;
using Romd.Media.Processes;

namespace Romd.Media.PlayStation;

/// <summary>Fixed PS1 recipe selected on native Linux x64. Geometry alone is not platform admission.</summary>
public static class ChdCdEncodingRecipe
{
    public const string Id = "chd-ps1-canonical/v1";
    public const uint HunkBytes = 39168;
    public const uint Codec = 0x63647a73; // cdzs
    public const uint Codec1 = 0x6364666c; // cdfl
    public static string[] Arguments(string cue, string output) => ["createcd", "-i", cue, "-o", output, "-hs", "39168", "-c", "cdzs,cdfl", "-np", "1"];
    public static bool Matches(ChdHeaderFacts header) => header.UnitBytes == 2448 && header.HunkBytes == HunkBytes &&
        header.LogicalBytes is > 0 and <= (ulong)ChdCdLimits.MaximumLogicalBytes && header.LogicalBytes % 2448 == 0 &&
        header.HunkCount == header.LogicalBytes / HunkBytes + (header.LogicalBytes % HunkBytes == 0 ? 0UL : 1UL) &&
        header.Codec0 == Codec && header.Codec1 == Codec1 && header.Codec2 == 0 && header.Codec3 == 0;
}

public sealed record ChdCdEncodingOutcome(MediaWorkerStatus Status, ChdCdEncodedArtifact? Artifact = null,
    ChdmanToolIdentity? Tool = null, ChdDvdToolDiagnostics? Diagnostics = null, ChdCdJobFailure? Failure = null);

/// <summary>Pinned chdman under the shared sealed-image runner and inherited Linux limits. Only artifact facts; verification is a separate child.</summary>
public static class ChdCdEncodingService
{
    public static async Task<ChdCdEncodingOutcome> EncodeAsync(string requestPath, string outputPath,
        long requestBytes, string requestSha256, ChdmanCandidate candidate, long artifactLimit, long logicalLimit,
        long outputLimit, string priorityKind, int priorityValue, int admissionDescriptor, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (artifactLimit is < 124 or > (16L << 30) || logicalLimit is < 2448 or > (16L << 30) ||
                outputLimit is < 124 or > ChdCdLimits.MaximumArtifactBytes || outputLimit > artifactLimit ||
                !ChdCdJson.Absolute(outputPath) || outputPath == requestPath || ChdToolPaths.Linked(outputPath))
                return Reject(MediaWorkerStatus.Malformed, ChdCdJobCause.InvalidInput);
            using var sources = await ChdCdHeldSources.OpenAsync(requestPath, requestBytes, requestSha256, ct);
            if (sources.Request.ToolCuePath == outputPath || sources.Request.Files.Any(f => f.Path == outputPath) ||
                (sources.Request.Files.Where(f => f.Role == "bin").Sum(f => f.Bytes / 2352) + sources.Cue.Files.Sum(f => f.Tracks.Count) * 3L) * 2448 > logicalLimit)
                return Reject(MediaWorkerStatus.LimitExceeded, ChdCdJobCause.InvalidInput);
            try
            {
                using var existing = NativeFileLease.OpenRegularRead(outputPath);
                return Reject(MediaWorkerStatus.Malformed, ChdCdJobCause.OutputRejected);
            }
            catch (IOException error) when (error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }) { }
            if (!OperatingSystem.IsLinux())
                return Reject(MediaWorkerStatus.Unsupported, ChdCdJobCause.UnsupportedHost);
            if (admissionDescriptor < 0 || priorityKind != "unix-nice" || priorityValue is < 10 or > 19)
                return Reject(MediaWorkerStatus.IoFailure, ChdCdJobCause.ToolRejected, ChdDvdEncodingCause.ToolPriorityMismatch);
            var ps1 = sources.IdentifyRequiredBoot(ct);
            var identity = candidate.Verify(ct);
            var expectation = ChdmanToolRunner.CurrentExpectation();
            if (expectation.Nice != priorityValue)
                return Reject(MediaWorkerStatus.IoFailure, ChdCdJobCause.ToolRejected, ChdDvdEncodingCause.ToolPriorityMismatch);
            var tool = await ChdmanToolRunner.RunAsync(candidate, ChdCdEncodingRecipe.Arguments(sources.Request.ToolCuePath, outputPath),
                outputLimit, admissionDescriptor, expectation, ct);
            if (tool.Rejection is { } rejection)
                return new(tool.Status, Tool: identity, Diagnostics: tool.Diagnostics,
                Failure: new(ChdCdJobCause.ToolRejected, rejection.Cause));
            await sources.AssertUnchangedAsync(ct);
            using var output = new ChdCdRawFile(outputPath);
            if (output.Length is < 124 || output.Length > outputLimit)
                return new(MediaWorkerStatus.LimitExceeded, Tool: identity, Diagnostics: tool.Diagnostics,
                Failure: new(ChdCdJobCause.OutputRejected));
            byte[] header = new byte[124];
            int count = output.ReadAt(0, header);
            var preflight = ChdHeaderPreflight.Inspect(header.AsSpan(0, count), output.Length,
                ChdCdLimits.Preflight with { MaximumArtifactBytes = outputLimit, MaximumLogicalBytes = (ulong)Math.Min(logicalLimit, ChdCdLimits.MaximumLogicalBytes) });
            if (preflight is not ChdHeaderPreflightResult.Accepted accepted || !ChdCdEncodingRecipe.Matches(accepted.Facts))
                return new(MediaWorkerStatus.Malformed, Tool: identity, Diagnostics: tool.Diagnostics, Failure: new(ChdCdJobCause.OutputRejected));
            var measured = await ChdCdHashing.MeasureAsync(output, ct);
            return new(MediaWorkerStatus.Encoded, new(requestSha256, requestBytes, measured, accepted.Facts, ChdCdEncodingRecipe.Id, priorityKind, priorityValue, ps1), identity, tool.Diagnostics);
        }
        catch (ChdCdSourceException error) { return Reject(error.Cause == ChdCdJobCause.UnsupportedMedia ? MediaWorkerStatus.Unsupported : MediaWorkerStatus.Malformed, error.Cause); }
        catch (ChdmanCandidateException) { return Reject(MediaWorkerStatus.IoFailure, ChdCdJobCause.CandidateRejected); }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidDataException or System.Text.DecoderFallbackException)
        { return Reject(MediaWorkerStatus.Malformed, ChdCdJobCause.InvalidInput); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException)
        { return Reject(MediaWorkerStatus.IoFailure, ChdCdJobCause.IoFailure); }
    }
    private static ChdCdEncodingOutcome Reject(MediaWorkerStatus status, ChdCdJobCause cause, ChdDvdEncodingCause? tool = null) => new(status, Failure: new(cause, tool));
}
