using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Romd.Media.Inspection;

namespace Romd.Media.PlayStation;

/// <summary>The facts one chdman build recorded about itself: the MAME source pin, recipe version and exact binary.</summary>
/// <remarks>Binary size and SHA-256 bind verification and the launched tool image; persisted evidence keeps only the
/// RID, MAME tag and source archive SHA-256 and recipe version.</remarks>
public sealed record ChdmanToolIdentity(string Rid, string MameTag, string MameSourceSha256, int RecipeVersion,
    long BinaryBytes, string BinarySha256);

/// <summary>ROMD's pinned MAME source archive; a test keeps these equal to <c>native/chdman/mame.json</c>.</summary>
public static class ChdmanPin
{
    public const string Tag = "mame0289";
    public const string SourceSha256 = "0929cc749afabcef892900e10dd90bd8b05f94a7dde8f367ac6a5d2082589f84";
}

public sealed class ChdmanCandidateException(MediaWorkerStatus status, ChdRejection rejection)
    : IOException($"CHD tool candidate rejected: {rejection.Cause}; path={rejection.Path}")
{
    public MediaWorkerStatus Status { get; } = status;
    public ChdRejection Rejection { get; } = rejection;
}

/// <summary>A configured chdman directory holding its own <c>build-manifest.json</c> and <c>native/chdman</c>.</summary>
/// <remarks>
/// Construction performs no I/O, and verification neither spawns nor loads code. The caller must preserve immutable
/// file custody through execution; hashing held handles does not prevent replacement of a path after verification.
/// </remarks>
public sealed class ChdmanCandidate
{
    public const string RecordFileName = "build-manifest.json";
    private const string BinaryRelativePath = "native/chdman";
    private const long MaximumRecordBytes = 1L << 20;

    public ChdmanCandidate(string buildDirectory, ChdmanToolIdentity expectedIdentity)
    {
        BuildDirectory = AbsolutePath(buildDirectory);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        if (!MediaWorkerProtocol.TryReadToolIdentity(MediaWorkerProtocol.WriteToolIdentity(expectedIdentity), out _))
            throw new ArgumentException("A complete bounded tool build identity is required.", nameof(expectedIdentity));
        ExpectedIdentity = expectedIdentity;
        ManifestPath = Path.Combine(BuildDirectory, RecordFileName);
        BinaryPath = Path.Combine(BuildDirectory, "native", "chdman");
    }

    public string BuildDirectory { get; }
    public string ManifestPath { get; }
    public string BinaryPath { get; }
    public ChdmanToolIdentity ExpectedIdentity { get; }
    public bool Matches(ChdmanToolIdentity actual) => ExpectedIdentity == actual;

    /// <summary>Reads the directory's build record as the expected identity; this does not verify the binary.</summary>
    public static ChdmanCandidate FromBuildRecord(string buildDirectory)
    {
        string directory = AbsolutePath(buildDirectory);
        return new(directory, ReadRecord(Path.Combine(directory, RecordFileName), CancellationToken.None));
    }

    public ChdmanToolIdentity Verify(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (HostRid() != ExpectedIdentity.Rid)
            throw Reject("ProcessRidMismatch", BinaryPath, MediaWorkerStatus.Unsupported);
        ChdmanToolIdentity recorded = ReadRecord(ManifestPath, ct);
        if (recorded.MameTag != ChdmanPin.Tag || recorded.MameSourceSha256 != ChdmanPin.SourceSha256)
            throw Reject("ManifestPinMismatch", ManifestPath);
        if (recorded != ExpectedIdentity)
            throw Reject("ManifestIdentityMismatch", ManifestPath);
        RequireRegularBinary();
        VerifyBinary(ct);
        ct.ThrowIfCancellationRequested();
        return ExpectedIdentity;
    }

    private void VerifyBinary(CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 << 10);
        try
        {
            using FileStream file = Open(BinaryPath, "BinaryMissing");
            if (file.Length != ExpectedIdentity.BinaryBytes)
                throw Reject("BinarySizeMismatch", BinaryPath);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long remaining = ExpectedIdentity.BinaryBytes;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                    throw Reject("BinarySizeMismatch", BinaryPath);
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            if (file.ReadByte() != -1 || file.Length != ExpectedIdentity.BinaryBytes)
                throw Reject("BinarySizeMismatch", BinaryPath);
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != ExpectedIdentity.BinarySha256)
                throw Reject("BinarySha256Mismatch", BinaryPath);
        }
        catch (Exception error) when ((error is IOException && error is not ChdmanCandidateException) || error is UnauthorizedAccessException or NotSupportedException)
        { throw Reject("NativeArtifactIo", BinaryPath, MediaWorkerStatus.IoFailure); }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static ChdmanToolIdentity ReadRecord(string path, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            using FileStream file = Open(path, "ManifestMissing");
            if (file.Length is < 1 or > MaximumRecordBytes)
                throw Reject("ManifestInvalid", path);
            bytes = new byte[file.Length];
            ct.ThrowIfCancellationRequested();
            file.ReadExactly(bytes);
            if (file.ReadByte() != -1 || file.Length != bytes.Length)
                throw Reject("ManifestInvalid", path);
        }
        catch (Exception error) when ((error is IOException && error is not ChdmanCandidateException) || error is UnauthorizedAccessException or NotSupportedException)
        { throw Reject("NativeArtifactIo", path, MediaWorkerStatus.IoFailure); }
        return ParseRecord(bytes, path);
    }

    private static ChdmanToolIdentity ParseRecord(byte[] bytes, string path)
    {
        JsonDocument document;
        try
        { document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 }); }
        catch (JsonException) { throw Reject("ManifestInvalid", path); }
        using (document)
            try
            {
                JsonElement root = document.RootElement;
                if (!Unique(root) || root.GetProperty("schemaVersion").GetInt32() != 2)
                    throw new InvalidDataException();
                JsonElement binary = root.GetProperty("binary"), pin = root.GetProperty("pin");
                if (Text(binary, "path") != BinaryRelativePath)
                    throw new InvalidDataException();
                var identity = new ChdmanToolIdentity(Text(root, "rid"), Text(pin, "tag"), Text(pin, "sourceSha256"),
                    root.GetProperty("recipeVersion").GetInt32(), binary.GetProperty("bytes").GetInt64(), Text(binary, "sha256"));
                if (!MediaWorkerProtocol.TryReadToolIdentity(MediaWorkerProtocol.WriteToolIdentity(identity), out _))
                    throw new InvalidDataException();
                return identity;
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or KeyNotFoundException or
                InvalidOperationException or FormatException or OverflowException)
            { throw Reject("ManifestInvalid", path); }
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new InvalidDataException();

    private static bool Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
                if (!names.Add(property.Name) || !Unique(property.Value))
                    return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray())
                if (!Unique(item))
                    return false;
        return true;
    }

    // The tool and its native directory must not be links; the configured directory is the operator's absolute choice.
    private void RequireRegularBinary()
    {
        try
        {
            if (new DirectoryInfo(Path.GetDirectoryName(BinaryPath)!).LinkTarget is not null ||
                new FileInfo(BinaryPath).LinkTarget is not null || Directory.Exists(BinaryPath))
                throw Reject("BinaryNotRegularFile", BinaryPath);
        }
        catch (Exception error) when (error is (IOException and not ChdmanCandidateException) or UnauthorizedAccessException)
        { throw Reject("NativeArtifactIo", BinaryPath, MediaWorkerStatus.IoFailure); }
        if (!File.Exists(BinaryPath))
            throw Reject("BinaryMissing", BinaryPath);
    }

    private static FileStream Open(string path, string missingCause)
    {
        try
        { return new(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { throw Reject(missingCause, path); }
    }

    // Under emulation the process RID would not name the tool's own architecture.
    private static string? HostRid() => RuntimeInformation.OSArchitecture == RuntimeInformation.ProcessArchitecture
        ? NativeRuntimeIdentifier.Current : null;

    private static string AbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("A bounded absolute tool build directory is required.", nameof(path));
        string full = Path.GetFullPath(path);
        if (full.Length > 1024)
            throw new ArgumentException("Tool build directory exceeds its bound.", nameof(path));
        return full;
    }

    private static ChdmanCandidateException Reject(string cause, string path, MediaWorkerStatus status = MediaWorkerStatus.Malformed) =>
        new(status, new(ChdRejectionStage.NativeVerification, cause, path));
}
