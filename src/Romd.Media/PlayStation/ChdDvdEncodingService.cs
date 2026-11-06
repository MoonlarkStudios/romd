using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Romd.Media.Inspection;
using Romd.Media.Processes;

namespace Romd.Media.PlayStation;

public enum ChdDvdEncodingStage { Source, ToolVerification, ToolLaunch, ToolImage, ToolExecution, Output }

public enum ChdDvdEncodingCause
{
    InvalidArguments, InvalidPath, OutputExists, UnsafePath, SourceIo, SourceLengthMismatch,
    SourceHashMismatch, SourceChanged, UnsupportedSource, LimitExceeded, LinuxRequired,
    CandidateRejected, LaunchFailure, ToolImageMismatch, ToolPriorityMismatch, DiagnosticLimitExceeded,
    ToolExitFailure, CpuBudgetExceeded, DrainFailure,
    OutputIo, OutputMissing, OutputLimitExceeded, InvalidOutputHeader, OutputLengthMismatch
}

public sealed record ChdDvdEncodingRejection(ChdDvdEncodingStage Stage, ChdDvdEncodingCause Cause,
    string Path, ChdRejection? CandidateRejection = null);

/// <summary>Raw encoding measurements only. Separate inspection must establish logical integrity.</summary>
public sealed record ChdDvdArtifactMetadata(string SourceSha256, long SourceBytes, long LogicalBytes,
    string ArtifactSha256, string ArtifactMd5, string ArtifactSha1, long ArtifactBytes,
    ChdHeaderFacts Header, string RecipeVersion, string EncodingProfile, string PriorityKind, int PriorityValue);

/// <summary>Byte-bounded tool diagnostics, separate from artifact metadata and the worker wire reply.</summary>
public sealed record ChdDvdToolDiagnostics(int ProcessId, int ExitCode, long StandardOutputBytes,
    long StandardErrorBytes, byte[] StandardOutputTail, byte[] StandardErrorTail, long CpuMicroseconds = 0);

public sealed record ChdDvdEncodingOutcome(MediaWorkerStatus Status, ChdDvdArtifactMetadata? Artifact = null,
    ChdmanToolIdentity? ToolIdentity = null, ChdDvdEncodingRejection? Rejection = null,
    ChdDvdToolDiagnostics? Diagnostics = null);

/// <summary>Measured extracted bytes only; source container integrity and eligibility are supplied independently.</summary>
public sealed record ChdDvdExtractionMetadata(string SourceSha256, long SourceBytes, string LogicalSha256,
    string Sha1, string Md5, string Crc32, long LogicalBytes, string Profile, string PriorityKind, int PriorityValue);

public sealed record ChdDvdExtractionOutcome(MediaWorkerStatus Status, ChdDvdExtractionMetadata? Extraction = null,
    ChdmanToolIdentity? ToolIdentity = null, ChdDvdEncodingRejection? Rejection = null,
    ChdDvdToolDiagnostics? Diagnostics = null);

/// <summary>Linux-only DVD-shaped raw input encoder for the fixed, unqualified canonical recipe candidate.</summary>
/// <remarks>
/// The caller owns immutable source/tool custody and a reserved absent output path. Partial output
/// remains caller-owned; only the outer supervisor establishes process-tree stop and disposal safety.
/// </remarks>
public static class ChdDvdEncodingService
{
    public const string ExtractionProfile = "chd-dvd-extract/v1";

    /// <summary>Fixed production recipe candidate; execution still produces raw facts without qualification or logical integrity.</summary>
    public static async Task<ChdDvdEncodingOutcome> EncodeAsync(string sourcePath, string reservedOutputPath,
        ChdmanCandidate candidate, long expectedSourceBytes, string expectedSourceSha256,
        long maxArtifactBytes, long maxLogicalBytes, long maxOutputBytes,
        string priorityKind, int priorityValue, int admissionDescriptor, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (candidate is null || maxArtifactBytes is < 4 or > (16L << 30) ||
            maxLogicalBytes is < 0x81 or > (16L << 30) || maxOutputBytes is < 124 or > (16L << 30) ||
            expectedSourceBytes <= 0 || expectedSourceBytes > (16L << 30) || !Sha256Text(expectedSourceSha256))
            return Reject(MediaWorkerStatus.LimitExceeded, ChdDvdEncodingStage.Source, ChdDvdEncodingCause.InvalidArguments, sourcePath);
        if (!Absolute(sourcePath) || !Absolute(reservedOutputPath) ||
            string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(reservedOutputPath), StringComparison.Ordinal))
            return Reject(MediaWorkerStatus.Malformed, ChdDvdEncodingStage.Source, ChdDvdEncodingCause.InvalidPath, sourcePath);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return Reject(MediaWorkerStatus.Unsupported, ChdDvdEncodingStage.ToolLaunch, ChdDvdEncodingCause.LinuxRequired, candidate.BinaryPath);
        ChdDvdEncodingStage activeStage = ChdDvdEncodingStage.Source;
        string activePath = sourcePath;
        ChdmanToolIdentity? identity = null;
        ChdDvdToolDiagnostics? diagnostics = null;
        try
        {
            if (ChdToolPaths.Linked(sourcePath))
                return Reject(MediaWorkerStatus.IoFailure, ChdDvdEncodingStage.Source, ChdDvdEncodingCause.UnsafePath, sourcePath);
            activeStage = ChdDvdEncodingStage.Output;
            activePath = reservedOutputPath;
            if (ChdToolPaths.Linked(reservedOutputPath))
                return Reject(MediaWorkerStatus.IoFailure, activeStage, ChdDvdEncodingCause.UnsafePath, activePath);
            try
            {
                using SafeFileHandle existing = NativeFileLease.OpenRegularRead(reservedOutputPath);
                return Reject(MediaWorkerStatus.IoFailure, activeStage, ChdDvdEncodingCause.OutputExists, activePath);
            }
            catch (IOException error) when (error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }) { }
            activeStage = ChdDvdEncodingStage.Source;
            activePath = sourcePath;
            using SafeFileHandle source = NativeFileLease.OpenRegularRead(sourcePath);
            long length = RandomAccess.GetLength(source);
            if (length != expectedSourceBytes)
                return Reject(MediaWorkerStatus.Malformed, ChdDvdEncodingStage.Source, ChdDvdEncodingCause.SourceLengthMismatch, sourcePath);
            if (length > maxArtifactBytes || length > maxLogicalBytes)
                return Reject(MediaWorkerStatus.LimitExceeded, ChdDvdEncodingStage.Source, ChdDvdEncodingCause.LimitExceeded, sourcePath);
            byte[] prefix = new byte[16];
            int prefixBytes = await ReadPrefixAsync(source, prefix, ct);
            if (length % 2048 != 0 || prefixBytes != prefix.Length || UnsupportedPrefix(prefix))
                return Reject(MediaWorkerStatus.Unsupported, ChdDvdEncodingStage.Source, ChdDvdEncodingCause.UnsupportedSource, sourcePath);
            string originalSha256 = await Sha256Async(source, length, ct);
            if (originalSha256 != expectedSourceSha256)
                return Reject(MediaWorkerStatus.Malformed, ChdDvdEncodingStage.Source, ChdDvdEncodingCause.SourceHashMismatch, sourcePath);
            // This route intentionally has no Mac/Windows launch fallback.
            if (!OperatingSystem.IsLinux())
                return Reject(MediaWorkerStatus.Unsupported, ChdDvdEncodingStage.ToolLaunch, ChdDvdEncodingCause.LinuxRequired, candidate.BinaryPath);
            if (admissionDescriptor < 0 || priorityKind != "unix-nice" || priorityValue is < 10 or > 19)
                return Reject(MediaWorkerStatus.IoFailure, ChdDvdEncodingStage.ToolLaunch, ChdDvdEncodingCause.ToolPriorityMismatch, candidate.BinaryPath);
            activeStage = ChdDvdEncodingStage.ToolVerification;
            activePath = candidate.BinaryPath;
            identity = candidate.Verify(ct);
            activeStage = ChdDvdEncodingStage.ToolLaunch;
            ChdmanProcessExpectation inherited = ChdmanToolRunner.CurrentExpectation();
            if (inherited.Nice != priorityValue)
                return Reject(MediaWorkerStatus.IoFailure, ChdDvdEncodingStage.ToolLaunch, ChdDvdEncodingCause.ToolPriorityMismatch, candidate.BinaryPath);
            activeStage = ChdDvdEncodingStage.ToolExecution;
            ChdmanToolRun run = await ChdmanToolRunner.RunAsync(candidate, ChdDvdCanonicalRecipe.ChdmanArguments(sourcePath, reservedOutputPath),
                maxOutputBytes, admissionDescriptor, inherited, ct);
            diagnostics = run.Diagnostics;
            if (run.Rejection is not null)
                return new(run.Status, ToolIdentity: identity, Rejection: run.Rejection, Diagnostics: diagnostics);
            // Caller custody is still required between observations; these checks do not remove TOCTOU.
            activeStage = ChdDvdEncodingStage.Source;
            activePath = sourcePath;
            try
            {
                if (RandomAccess.GetLength(source) != length || await Sha256Async(source, length, ct) != originalSha256 || ChdToolPaths.Linked(sourcePath))
                    return Changed();
                using SafeFileHandle named = NativeFileLease.OpenRegularRead(sourcePath);
                if (RandomAccess.GetLength(named) != length || await Sha256Async(named, length, ct) != originalSha256)
                    return Changed();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Changed(); }
            activeStage = ChdDvdEncodingStage.Output;
            activePath = reservedOutputPath;
            if (ChdToolPaths.Linked(reservedOutputPath))
                return Reject(MediaWorkerStatus.IoFailure, ChdDvdEncodingStage.Output, ChdDvdEncodingCause.UnsafePath, reservedOutputPath, identity, diagnostics);
            // Root-owned OpenRegularRead checks no-follow/nonblocking fstat S_IFREG on the held fd.
            // No regular-file guarantee is inferred from File.Exists or managed attributes.
            using SafeFileHandle output = NativeFileLease.OpenRegularRead(reservedOutputPath);
            long outputBytes = RandomAccess.GetLength(output);
            if (outputBytes > maxOutputBytes)
                return Reject(MediaWorkerStatus.LimitExceeded, ChdDvdEncodingStage.Output, ChdDvdEncodingCause.OutputLimitExceeded, reservedOutputPath, identity, diagnostics);
            byte[] header = new byte[124];
            int read = await ReadPrefixAsync(output, header, ct);
            var check = ChdHeaderPreflight.Inspect(header.AsSpan(0, read), outputBytes,
                new(maxOutputBytes, (ulong)maxLogicalBytes, ChdDvdCanonicalRecipe.HunkBytes, uint.MaxValue));
            if (check is not ChdHeaderPreflightResult.Accepted accepted || accepted.Facts.LogicalBytes != (ulong)length ||
                !ChdDvdCanonicalRecipe.Matches(accepted.Facts))
                return Reject(MediaWorkerStatus.Malformed, ChdDvdEncodingStage.Output, ChdDvdEncodingCause.InvalidOutputHeader, reservedOutputPath, identity, diagnostics);
            Hashes measured = await HashAsync(output, outputBytes, ct);
            return new(MediaWorkerStatus.Encoded, new(originalSha256, length, length, measured.Sha256, measured.Md5,
                measured.Sha1, outputBytes, accepted.Facts, ChdDvdCanonicalRecipe.Id, ChdDvdCanonicalRecipe.EncoderProfile, priorityKind, priorityValue), identity, Diagnostics: diagnostics);
            ChdDvdEncodingOutcome Changed() => Reject(MediaWorkerStatus.IoFailure, ChdDvdEncodingStage.Source,
                ChdDvdEncodingCause.SourceChanged, sourcePath, identity, diagnostics);
        }
        catch (ChdmanCandidateException error)
        { return new(error.Status, Rejection: new(ChdDvdEncodingStage.ToolVerification, ChdDvdEncodingCause.CandidateRejected, error.Rejection.Path, error.Rejection)); }
        catch (IOException error) when (activeStage == ChdDvdEncodingStage.Output &&
            error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 })
        { return Reject(MediaWorkerStatus.Malformed, activeStage, ChdDvdEncodingCause.OutputMissing, activePath, identity, diagnostics); }
        catch (InvalidDataException) { return Reject(MediaWorkerStatus.IoFailure, activeStage, ChdDvdEncodingCause.UnsafePath, activePath, identity, diagnostics); }
        catch (System.ComponentModel.Win32Exception)
        {
            return Reject(MediaWorkerStatus.IoFailure, ChdDvdEncodingStage.ToolLaunch,
            ChdDvdEncodingCause.LaunchFailure, candidate.BinaryPath, identity, diagnostics);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return Reject(MediaWorkerStatus.IoFailure, activeStage, activeStage switch
            {
                ChdDvdEncodingStage.Source => ChdDvdEncodingCause.SourceIo,
                ChdDvdEncodingStage.Output => ChdDvdEncodingCause.OutputIo,
                ChdDvdEncodingStage.ToolExecution => ChdDvdEncodingCause.DrainFailure,
                _ => ChdDvdEncodingCause.LaunchFailure
            },
            activePath, identity, diagnostics);
        }
    }

    /// <summary>Extract a previously independently verified DVD CHD into a caller-reserved ISO path.</summary>
    /// <remarks>No source integrity, original-media eligibility or equivalence is inferred from this operation.</remarks>
    public static async Task<ChdDvdExtractionOutcome> ExtractAsync(string sourcePath, string reservedOutputPath,
        ChdmanCandidate candidate, long expectedSourceBytes, string expectedSourceSha256, long expectedLogicalBytes,
        long maxArtifactBytes, long maxLogicalBytes, long maxOutputBytes,
        string priorityKind, int priorityValue, int admissionDescriptor, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (candidate is null || maxArtifactBytes is < 124 or > (16L << 30) ||
            maxLogicalBytes is < 2048 or > (16L << 30) || maxOutputBytes is < 2048 or > (16L << 30) ||
            expectedSourceBytes is < 124 or > ChdDvdLimits.MaximumArtifactBytes || !Sha256Text(expectedSourceSha256) ||
            expectedLogicalBytes is < 2048 or > ChdDvdLimits.MaximumLogicalBytes || expectedLogicalBytes % 2048 != 0)
            return RejectExtraction(MediaWorkerStatus.LimitExceeded, ChdDvdEncodingStage.Source, ChdDvdEncodingCause.InvalidArguments, sourcePath);
        if (!Absolute(sourcePath) || !Absolute(reservedOutputPath) ||
            string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(reservedOutputPath), StringComparison.Ordinal))
            return RejectExtraction(MediaWorkerStatus.Malformed, ChdDvdEncodingStage.Source, ChdDvdEncodingCause.InvalidPath, sourcePath);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return RejectExtraction(MediaWorkerStatus.Unsupported, ChdDvdEncodingStage.ToolLaunch, ChdDvdEncodingCause.LinuxRequired, candidate.BinaryPath);
        ChdDvdEncodingStage activeStage = ChdDvdEncodingStage.Source;
        string activePath = sourcePath;
        ChdmanToolIdentity? identity = null;
        ChdDvdToolDiagnostics? diagnostics = null;
        try
        {
            if (ChdToolPaths.Linked(sourcePath))
                return RejectExtraction(MediaWorkerStatus.IoFailure, activeStage, ChdDvdEncodingCause.UnsafePath, activePath);
            activeStage = ChdDvdEncodingStage.Output;
            activePath = reservedOutputPath;
            if (ChdToolPaths.Linked(reservedOutputPath))
                return RejectExtraction(MediaWorkerStatus.IoFailure, activeStage, ChdDvdEncodingCause.UnsafePath, activePath);
            try
            {
                using SafeFileHandle existing = NativeFileLease.OpenRegularRead(reservedOutputPath);
                return RejectExtraction(MediaWorkerStatus.IoFailure, activeStage, ChdDvdEncodingCause.OutputExists, activePath);
            }
            catch (IOException error) when (error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }) { }
            activeStage = ChdDvdEncodingStage.Source;
            activePath = sourcePath;
            using SafeFileHandle source = NativeFileLease.OpenRegularRead(sourcePath);
            long length = RandomAccess.GetLength(source);
            if (length != expectedSourceBytes)
                return RejectExtraction(MediaWorkerStatus.Malformed, activeStage, ChdDvdEncodingCause.SourceLengthMismatch, activePath);
            if (length > maxArtifactBytes || expectedLogicalBytes > maxLogicalBytes || expectedLogicalBytes > maxOutputBytes)
                return RejectExtraction(MediaWorkerStatus.LimitExceeded, activeStage, ChdDvdEncodingCause.LimitExceeded, activePath);
            string originalSha256 = await Sha256Async(source, length, ct);
            if (originalSha256 != expectedSourceSha256)
                return RejectExtraction(MediaWorkerStatus.Malformed, activeStage, ChdDvdEncodingCause.SourceHashMismatch, activePath);
            byte[] prefix = new byte[124];
            int prefixBytes = await ReadPrefixAsync(source, prefix, ct);
            var preflight = ChdHeaderPreflight.Inspect(prefix.AsSpan(0, prefixBytes), length,
                ChdDvdLimits.Preflight with
                {
                    MaximumArtifactBytes = Math.Min(maxArtifactBytes, ChdDvdLimits.MaximumArtifactBytes),
                    MaximumLogicalBytes = (ulong)Math.Min(maxLogicalBytes, ChdDvdLimits.MaximumLogicalBytes)
                });
            if (preflight is not ChdHeaderPreflightResult.Accepted accepted ||
                accepted.Facts.UnitBytes != ChdDvdLimits.UnitBytes || accepted.Facts.HunkBytes % ChdDvdLimits.UnitBytes != 0 ||
                accepted.Facts.LogicalBytes != (ulong)expectedLogicalBytes ||
                new[] { accepted.Facts.Codec0, accepted.Facts.Codec1, accepted.Facts.Codec2, accepted.Facts.Codec3 }
                    .Any(ChdDvdLimits.IsCdCodec))
                return RejectExtraction(MediaWorkerStatus.Unsupported, activeStage, ChdDvdEncodingCause.UnsupportedSource, activePath);
            if (!OperatingSystem.IsLinux())
                return RejectExtraction(MediaWorkerStatus.Unsupported, ChdDvdEncodingStage.ToolLaunch, ChdDvdEncodingCause.LinuxRequired, candidate.BinaryPath);
            if (admissionDescriptor < 0 || priorityKind != "unix-nice" || priorityValue is < 10 or > 19)
                return RejectExtraction(MediaWorkerStatus.IoFailure, ChdDvdEncodingStage.ToolLaunch, ChdDvdEncodingCause.ToolPriorityMismatch, candidate.BinaryPath);
            activeStage = ChdDvdEncodingStage.ToolVerification;
            activePath = candidate.BinaryPath;
            identity = candidate.Verify(ct);
            activeStage = ChdDvdEncodingStage.ToolLaunch;
            ChdmanProcessExpectation inherited = ChdmanToolRunner.CurrentExpectation();
            if (inherited.Nice != priorityValue)
                return RejectExtraction(MediaWorkerStatus.IoFailure, activeStage, ChdDvdEncodingCause.ToolPriorityMismatch, activePath);
            activeStage = ChdDvdEncodingStage.ToolExecution;
            // Exact logical bound applies during writes, independently of the aggregate's conservative ceiling.
            ChdmanToolRun run = await ChdmanToolRunner.RunAsync(candidate, ["extractdvd", "-i", sourcePath, "-o", reservedOutputPath],
                expectedLogicalBytes, admissionDescriptor, inherited, ct);
            diagnostics = run.Diagnostics;
            if (run.Rejection is not null)
                return new(run.Status, ToolIdentity: identity, Rejection: run.Rejection, Diagnostics: diagnostics);
            activeStage = ChdDvdEncodingStage.Source;
            activePath = sourcePath;
            try
            {
                if (RandomAccess.GetLength(source) != length || await Sha256Async(source, length, ct) != originalSha256 || ChdToolPaths.Linked(sourcePath))
                    return Changed();
                using SafeFileHandle named = NativeFileLease.OpenRegularRead(sourcePath);
                if (RandomAccess.GetLength(named) != length || await Sha256Async(named, length, ct) != originalSha256)
                    return Changed();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Changed(); }
            activeStage = ChdDvdEncodingStage.Output;
            activePath = reservedOutputPath;
            if (ChdToolPaths.Linked(reservedOutputPath))
                return RejectExtraction(MediaWorkerStatus.IoFailure, activeStage, ChdDvdEncodingCause.UnsafePath, activePath, identity, diagnostics);
            using SafeFileHandle output = NativeFileLease.OpenRegularRead(reservedOutputPath);
            long outputBytes = RandomAccess.GetLength(output);
            if (outputBytes > expectedLogicalBytes)
                return RejectExtraction(MediaWorkerStatus.LimitExceeded, activeStage, ChdDvdEncodingCause.OutputLimitExceeded, activePath, identity, diagnostics);
            if (outputBytes != expectedLogicalBytes)
                return RejectExtraction(MediaWorkerStatus.Malformed, activeStage, ChdDvdEncodingCause.OutputLengthMismatch, activePath, identity, diagnostics);
            Hashes measured = await HashAsync(output, outputBytes, ct, includeCrc32: true);
            return new(MediaWorkerStatus.Encoded, new(originalSha256, length, measured.Sha256, measured.Sha1,
                measured.Md5, measured.Crc32!, outputBytes, ExtractionProfile, priorityKind, priorityValue), identity, Diagnostics: diagnostics);
            ChdDvdExtractionOutcome Changed() => RejectExtraction(MediaWorkerStatus.IoFailure, ChdDvdEncodingStage.Source,
                ChdDvdEncodingCause.SourceChanged, sourcePath, identity, diagnostics);
        }
        catch (ChdmanCandidateException error)
        { return new(error.Status, Rejection: new(ChdDvdEncodingStage.ToolVerification, ChdDvdEncodingCause.CandidateRejected, error.Rejection.Path, error.Rejection)); }
        catch (IOException error) when (activeStage == ChdDvdEncodingStage.Output &&
            error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 })
        { return RejectExtraction(MediaWorkerStatus.Malformed, activeStage, ChdDvdEncodingCause.OutputMissing, activePath, identity, diagnostics); }
        catch (InvalidDataException) { return RejectExtraction(MediaWorkerStatus.IoFailure, activeStage, ChdDvdEncodingCause.UnsafePath, activePath, identity, diagnostics); }
        catch (System.ComponentModel.Win32Exception)
        {
            return RejectExtraction(MediaWorkerStatus.IoFailure, ChdDvdEncodingStage.ToolLaunch,
            ChdDvdEncodingCause.LaunchFailure, candidate.BinaryPath, identity, diagnostics);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return RejectExtraction(MediaWorkerStatus.IoFailure, activeStage, activeStage switch
            {
                ChdDvdEncodingStage.Source => ChdDvdEncodingCause.SourceIo,
                ChdDvdEncodingStage.Output => ChdDvdEncodingCause.OutputIo,
                ChdDvdEncodingStage.ToolExecution => ChdDvdEncodingCause.DrainFailure,
                _ => ChdDvdEncodingCause.LaunchFailure
            },
            activePath, identity, diagnostics);
        }
    }

    private static ChdDvdExtractionOutcome RejectExtraction(MediaWorkerStatus status, ChdDvdEncodingStage stage,
        ChdDvdEncodingCause cause, string path, ChdmanToolIdentity? identity = null, ChdDvdToolDiagnostics? diagnostics = null) =>
        new(status, ToolIdentity: identity, Rejection: new(stage, cause, path), Diagnostics: diagnostics);

    private sealed record Hashes(string Sha256, string Md5, string Sha1, string? Crc32 = null);
    // Source custody compares SHA-256 only; the source's other digests are never reported.
    private static async Task<string> Sha256Async(SafeFileHandle file, long length, CancellationToken ct)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await ReadExactlyAsync(file, length, (buffer, read) => sha256.AppendData(buffer, 0, read), ct);
        return Convert.ToHexStringLower(sha256.GetHashAndReset());
    }
    private static async Task<Hashes> HashAsync(SafeFileHandle file, long length, CancellationToken ct, bool includeCrc32 = false)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var crc32 = includeCrc32 ? new System.IO.Hashing.Crc32() : null;
        await ReadExactlyAsync(file, length, (buffer, read) =>
        {
            sha256.AppendData(buffer, 0, read);
            md5.AppendData(buffer, 0, read);
            sha1.AppendData(buffer, 0, read);
            crc32?.Append(buffer.AsSpan(0, read));
        }, ct);
        return new(Convert.ToHexStringLower(sha256.GetHashAndReset()), Convert.ToHexStringLower(md5.GetHashAndReset()),
            Convert.ToHexStringLower(sha1.GetHashAndReset()), crc32?.GetCurrentHashAsUInt32().ToString("x8", CultureInfo.InvariantCulture));
    }
    private static async Task ReadExactlyAsync(SafeFileHandle file, long length, Action<byte[], int> append, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 << 10);
        try
        {
            for (long offset = 0; offset < length;)
            {
                ct.ThrowIfCancellationRequested();
                int read = await RandomAccess.ReadAsync(file, buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - offset)), offset, ct);
                if (read <= 0)
                    throw new IOException("Artifact length changed during hashing.");
                append(buffer, read);
                offset += read;
            }
            if (RandomAccess.GetLength(file) != length)
                throw new IOException("Artifact length changed during hashing.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
    private static async Task<int> ReadPrefixAsync(SafeFileHandle file, byte[] bytes, CancellationToken ct)
    {
        int total = 0;
        while (total < bytes.Length)
        {
            int read = await RandomAccess.ReadAsync(file, bytes.AsMemory(total), total, ct);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }
    private static bool Absolute(string path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 1024 &&
        !path.Any(char.IsControl) && Path.IsPathFullyQualified(path);
    private static bool Sha256Text(string value) => value is not null && value.Length == 64 && value.Any(c => c != '0') &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool UnsupportedPrefix(ReadOnlySpan<byte> bytes) => bytes.StartsWith("MComprHD"u8) ||
        bytes.StartsWith(new byte[] { 0x52, 0x56, 0x5a, 1 }) || bytes.StartsWith(new byte[] { 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0 });
    private static ChdDvdEncodingOutcome Reject(MediaWorkerStatus status, ChdDvdEncodingStage stage, ChdDvdEncodingCause cause,
        string path, ChdmanToolIdentity? identity = null, ChdDvdToolDiagnostics? diagnostics = null) =>
        new(status, ToolIdentity: identity, Rejection: new(stage, cause, path), Diagnostics: diagnostics);
}
