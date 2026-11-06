using System.Buffers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Moonlark.Libchdr;
using Romd.Media.Inspection;

namespace Romd.Media.PlayStation;

/// <summary>The facts one libchdr build recorded about itself, and after loading, the measured resident build.</summary>
/// <remarks>Binary size and SHA-256 bind verification and child re-verification to the exact bytes. Persisted evidence
/// keeps only the RID, upstream source pin and recipe version, so a rebuilt image does not invalidate stored CHDs.</remarks>
public sealed record ChdNativeIdentity(string Rid, string File, long BinaryBytes, string BinarySha256,
    string UpstreamCommit, string UpstreamVersion, int RecipeVersion, string FamilyVersion, string BuildId,
    bool HasRawSectors, bool HasSubcode, bool VerifiesBlockCrc);

public enum ChdRejectionStage { Header, NativeVerification, NativeLoad, NativeOpen, Decode, Integrity, Io }

public sealed record ChdRejection(ChdRejectionStage Stage, string Cause, string Path,
    int? CodecSlot = null, uint? Codec = null, int? NativeError = null);

public sealed class ChdNativeCandidateException(MediaWorkerStatus status, ChdRejection rejection)
    : IOException($"CHD native candidate rejected: {rejection.Cause}; path={rejection.Path}")
{
    public MediaWorkerStatus Status { get; } = status;
    public ChdRejection Rejection { get; } = rejection;
}

/// <summary>A configured libchdr directory holding its own <c>build-manifest.json</c> and <c>native/&lt;file&gt;</c>.</summary>
/// <remarks>
/// Construction validates arguments without opening files or initializing native code. Verification checks the binary
/// against the record its build wrote, then loads only that absolute path before any other wrapper use, so libchdr
/// never comes from a default, application-directory or system location. Paths must stay immutable between
/// verification and loading: handle-bound hashing cannot prevent external replacement of the path the loader opens.
/// </remarks>
public sealed class ChdNativeCandidate
{
    public const string RecordFileName = "build-manifest.json";
    private const int MaximumRecordBytes = 1 << 20;
    private static readonly string[] RequiredFeatures = ["raw-sectors", "subcode", "block-crc"];

    public ChdNativeCandidate(string buildDirectory, ChdNativeIdentity expectedIdentity)
    {
        BuildDirectory = AbsolutePath(buildDirectory);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        if (!MediaWorkerProtocol.TryReadNativeIdentity(MediaWorkerProtocol.WriteNativeIdentity(expectedIdentity), out _))
            throw new ArgumentException("A complete bounded native build identity is required.", nameof(expectedIdentity));
        ExpectedIdentity = expectedIdentity;
        ManifestPath = Path.Combine(BuildDirectory, RecordFileName);
        BinaryPath = Path.Combine(BuildDirectory, "native", expectedIdentity.File);
    }

    /// <summary>The upstream commit this ROMD build compiled its libchdr bindings against.</summary>
    /// <remarks>Read from wrapper assembly metadata, which never touches native code.</remarks>
    public static string CompiledUpstreamCommit { get; } = typeof(LibchdrLibrary).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "UpstreamCommit").Value
        ?? throw new InvalidOperationException("The libchdr wrapper does not carry its upstream commit.");

    public string BuildDirectory { get; }
    public string ManifestPath { get; }
    public string BinaryPath { get; }
    public ChdNativeIdentity ExpectedIdentity { get; }
    public bool Matches(ChdNativeIdentity actual) => ExpectedIdentity == actual;

    /// <summary>Reads the directory's build record as the expected identity; this neither verifies nor loads.</summary>
    public static ChdNativeCandidate FromBuildRecord(string buildDirectory)
    {
        string directory = AbsolutePath(buildDirectory);
        return new(directory, ReadRecord(Path.Combine(directory, RecordFileName), CancellationToken.None));
    }

    public ChdNativeIdentity VerifyAndLoad(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (NativeRuntimeIdentifier.Current != ExpectedIdentity.Rid)
            throw Reject(ChdRejectionStage.NativeVerification, "ProcessRidMismatch", BinaryPath, MediaWorkerStatus.Unsupported);
        ChdNativeIdentity recorded = ReadRecord(ManifestPath, ct);
        if (recorded.UpstreamCommit != CompiledUpstreamCommit)
            throw Reject(ChdRejectionStage.NativeVerification, "ManifestPinMismatch", ManifestPath);
        if (recorded != ExpectedIdentity)
            throw Reject(ChdRejectionStage.NativeVerification, "ManifestIdentityMismatch", ManifestPath);
        RequireRegularBinary();
        using (var binary = Open(BinaryPath, "BinaryMissing"))
        {
            if (binary.Length != ExpectedIdentity.BinaryBytes)
                throw Reject(ChdRejectionStage.NativeVerification, "BinarySizeMismatch", BinaryPath);
            if (Hash(binary, ct) != ExpectedIdentity.BinarySha256)
                throw Reject(ChdRejectionStage.NativeVerification, "BinarySha256Mismatch", BinaryPath);
        }
        ct.ThrowIfCancellationRequested();
        try
        { LibchdrLibrary.Load(BinaryPath); }
        catch (Exception error) when (error is PlatformNotSupportedException or DllNotFoundException or
            BadImageFormatException or InvalidOperationException)
        { throw Reject(ChdRejectionStage.NativeLoad, "NativeLoadFailure", BinaryPath, MediaWorkerStatus.IoFailure); }

        LibchdrBuildInfo info = LibchdrLibrary.BuildInfo;
        ChdNativeIdentity resident = ExpectedIdentity with
        {
            FamilyVersion = info.FamilyVersion,
            UpstreamVersion = info.UpstreamVersion,
            UpstreamCommit = info.UpstreamCommit,
            BuildId = info.BuildId,
            HasRawSectors = info.HasRawSectors,
            HasSubcode = info.HasSubcode,
            VerifiesBlockCrc = info.VerifiesBlockCrc
        };
        if (!Matches(resident))
            throw Reject(ChdRejectionStage.NativeVerification, "ResidentBuildMismatch", BinaryPath);
        ct.ThrowIfCancellationRequested();
        return resident;
    }

    private static ChdNativeIdentity ReadRecord(string path, CancellationToken ct)
    {
        byte[] bytes;
        using (var record = Open(path, "ManifestMissing"))
        {
            if (record.Length is < 1 or > MaximumRecordBytes)
                throw Reject(ChdRejectionStage.NativeVerification, "ManifestInvalid", path);
            bytes = new byte[record.Length];
            try
            {
                ct.ThrowIfCancellationRequested();
                record.ReadExactly(bytes);
                if (record.ReadByte() != -1 || record.Length != bytes.Length)
                    throw Reject(ChdRejectionStage.NativeVerification, "ManifestInvalid", path);
            }
            catch (IOException error) when (error is not ChdNativeCandidateException)
            { throw Reject(ChdRejectionStage.NativeVerification, "NativeArtifactIo", path, MediaWorkerStatus.IoFailure); }
        }
        return ParseRecord(bytes, path);
    }

    private static ChdNativeIdentity ParseRecord(byte[] bytes, string path)
    {
        JsonDocument document;
        try
        { document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 }); }
        catch (JsonException) { throw Reject(ChdRejectionStage.NativeVerification, "ManifestInvalid", path); }
        using (document)
            try
            {
                JsonElement root = document.RootElement;
                if (!Unique(root) || root.GetProperty("schemaVersion").GetInt32() != 3 || Text(root, "product") != "moonlark_chdr")
                    throw new InvalidDataException();
                JsonElement build = root.GetProperty("buildInfo");
                string upstreamCommit = Text(root, "upstreamCommit"), upstreamVersion = Text(root, "upstreamVersion");
                string family = Text(root, "nativeVersion");
                string[] features = build.GetProperty("features").EnumerateArray()
                    .Select(feature => feature.GetString() ?? throw new InvalidDataException()).ToArray();
                if (Text(build, "upstreamCommit") != upstreamCommit || Text(build, "upstreamVersion") != upstreamVersion ||
                    Text(build, "nativeVersion") != family || features.Length != RequiredFeatures.Length ||
                    !features.ToHashSet(StringComparer.Ordinal).SetEquals(RequiredFeatures))
                    throw new InvalidDataException();
                var identity = new ChdNativeIdentity(Text(root, "rid"), Text(root, "file"), root.GetProperty("size").GetInt64(),
                    Text(root, "sha256"), upstreamCommit, upstreamVersion, root.GetProperty("recipeVersion").GetInt32(), family,
                    Text(build, "buildId"), true, true, true);
                if (!MediaWorkerProtocol.TryReadNativeIdentity(MediaWorkerProtocol.WriteNativeIdentity(identity), out _))
                    throw new InvalidDataException();
                return identity;
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or KeyNotFoundException or
                InvalidOperationException or FormatException or OverflowException)
            { throw Reject(ChdRejectionStage.NativeVerification, "ManifestInvalid", path); }
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

    // The binary and its native directory must not be links; the configured directory is the operator's absolute choice.
    private void RequireRegularBinary()
    {
        try
        {
            if (new DirectoryInfo(Path.GetDirectoryName(BinaryPath)!).LinkTarget is not null ||
                new FileInfo(BinaryPath).LinkTarget is not null || Directory.Exists(BinaryPath))
                throw Reject(ChdRejectionStage.NativeVerification, "BinaryNotRegularFile", BinaryPath);
        }
        catch (Exception error) when (error is (IOException and not ChdNativeCandidateException) or UnauthorizedAccessException)
        { throw Reject(ChdRejectionStage.NativeVerification, "NativeArtifactIo", BinaryPath, MediaWorkerStatus.IoFailure); }
        if (!File.Exists(BinaryPath))
            throw Reject(ChdRejectionStage.NativeVerification, "BinaryMissing", BinaryPath);
    }

    private static FileStream Open(string path, string missingCause)
    {
        try
        { return new(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { throw Reject(ChdRejectionStage.NativeVerification, missingCause, path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Reject(ChdRejectionStage.NativeVerification, "NativeArtifactIo", path, MediaWorkerStatus.IoFailure); }
    }

    private string Hash(FileStream file, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 << 10);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long remaining = file.Length;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                    throw Reject(ChdRejectionStage.NativeVerification, "BinarySizeMismatch", BinaryPath);
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            if (file.ReadByte() != -1 || file.Length != ExpectedIdentity.BinaryBytes)
                throw Reject(ChdRejectionStage.NativeVerification, "BinarySizeMismatch", BinaryPath);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (IOException error) when (error is not ChdNativeCandidateException)
        { throw Reject(ChdRejectionStage.NativeVerification, "NativeArtifactIo", BinaryPath, MediaWorkerStatus.IoFailure); }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static string AbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("A bounded absolute native build directory is required.", nameof(path));
        string full = Path.GetFullPath(path);
        if (full.Length > 1024)
            throw new ArgumentException("Native build directory exceeds its bound.", nameof(path));
        return full;
    }

    private static ChdNativeCandidateException Reject(ChdRejectionStage stage, string cause, string path,
        MediaWorkerStatus status = MediaWorkerStatus.Malformed) => new(status, new(stage, cause, path));
}
