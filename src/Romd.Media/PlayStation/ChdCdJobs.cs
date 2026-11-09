using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Romd.Media.Inspection;
using Romd.Media.Processes;
using Romd.Storage;

namespace Romd.Media.PlayStation;

public enum ChdCdJobCause
{
    InvalidInput, SourceMismatch, SourceChanged, UnsupportedHost, CandidateRejected,
    ToolRejected, OutputRejected, ReconstructionMismatch, InspectionRejected, IoFailure, UnsupportedMedia
}
public sealed record ChdCdJobFailure(ChdCdJobCause Cause, ChdDvdEncodingCause? ToolCause = null);
public sealed record ChdCdFileHashes(long Bytes, string Sha256, string Sha1, string Md5, string Crc32);
public sealed record ChdCdMemberVerification(string Name, string Role, IReadOnlyList<int> Tracks,
    ChdCdFileHashes Original, ChdCdFileHashes? Reconstructed);
public sealed record ChdCdVerification(string Profile, string RequestSha256, IReadOnlyList<ChdCdMemberVerification> Members, Ps1CdObservedFacts? Ps1Boot,
    SaturnCdObservedFacts? SaturnBoot = null)
{
    public const string CurrentProfile = "chd-cd-whole-files/v1";
    [System.Text.Json.Serialization.JsonIgnore]
    public CdBootObservation ObservedBoot => new(Ps1Boot, SaturnBoot);
}
public sealed record ChdCdEncodedArtifact(string RequestSha256, long RequestBytes, ChdCdFileHashes Artifact,
    ChdHeaderFacts Header, string Recipe, string PriorityKind, int PriorityValue, Ps1CdObservedFacts? Ps1Boot = null, SaturnCdObservedFacts? SaturnBoot = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public CdBootObservation ObservedBoot => new(Ps1Boot, SaturnBoot);
}
public sealed record ChdCdVerificationOutcome(MediaWorkerStatus Status, ChdCdVerification? Verification = null,
    ChdCdInspection? Inspection = null, ChdNativeIdentity? Native = null, ChdCdJobFailure? Failure = null);

/// <summary>Fresh-child whole-file comparison, independent of the encoding process and its reported digests.</summary>
public static class ChdCdVerificationService
{
    public static async Task<ChdCdVerificationOutcome> VerifyAsync(string artifactPath, string requestPath, long requestBytes,
        string requestSha256, ChdNativeCandidate candidate, long artifactLimit, long logicalLimit, CancellationToken ct = default)
    {
        try
        {
            using var sources = await ChdCdHeldSources.OpenAsync(requestPath, requestBytes, requestSha256, ct);
            var boot = sources.IdentifyRequiredBoot(ct);
            var result = await ChdCdInspectionService.InspectAsync(artifactPath, candidate, artifactLimit, logicalLimit, ct);
            if (result.Status != MediaWorkerStatus.Inspected || result.Inspection is null || result.NativeIdentity is null)
                return new(result.Status, Failure: new(ChdCdJobCause.InspectionRejected));
            var inspection = result.Inspection;
            if (!inspection.Tracks.Select(t => t.Type).SequenceEqual(sources.Cue.TrackTypes))
                return new(MediaWorkerStatus.Malformed, Failure: new(ChdCdJobCause.ReconstructionMismatch));
            List<ChdCdMemberVerification> members = [];
            await using var pool = new ChdReadPool(new MediaBlockCache(8 << 20), 1, 1);
            foreach (var input in sources.Request.Files)
            {
                ct.ThrowIfCancellationRequested();
                var original = sources.Originals[input.Name].Hashes;
                if (input.Role != "bin")
                { members.Add(new(input.Name, input.Role, [], original, null)); continue; }
                var tracks = sources.Cue.Files.Single(f => f.Name == input.Name).Tracks;
                if (tracks.Sum(n => inspection.Tracks[n - 1].BinBytes) != original.Bytes)
                    return new(MediaWorkerStatus.Malformed, Failure: new(ChdCdJobCause.ReconstructionMismatch));
                var device = await ChdBlockDevice.OpenAsync(new ChdCdRawFile(artifactPath), pool, ChdCdLimits.Preflight,
                    inspection.ArtifactSha256, ct: ct);
                await using var member = await ChdCdBinContent.CreateAsync(device, tracks.Select(n => inspection.Tracks[n - 1])
                    .Select(t => new ChdCdBinExtent((long)t.FirstFrame, (int)t.Frames, t.Type == Moonlark.Libchdr.ChdCdTrackType.Audio)).ToArray());
                var reconstructed = await ChdCdHashing.MeasureAsync(member, ct);
                if (reconstructed != original)
                    return new(MediaWorkerStatus.Malformed, Failure: new(ChdCdJobCause.ReconstructionMismatch));
                members.Add(new(input.Name, input.Role, tracks.ToArray(), original, reconstructed));
            }
            await sources.AssertUnchangedAsync(ct);
            using var finalArtifact = new ChdCdRawFile(artifactPath);
            if ((await ChdCdHashing.MeasureAsync(finalArtifact, ct)).Sha256 != inspection.ArtifactSha256)
                return new(MediaWorkerStatus.IoFailure, Failure: new(ChdCdJobCause.SourceChanged));
            if (boot.Profile is not null && boot != inspection.ObservedBoot)
                return new(MediaWorkerStatus.Malformed, Failure: new(ChdCdJobCause.ReconstructionMismatch));
            return new(MediaWorkerStatus.Inspected, new(ChdCdVerification.CurrentProfile, requestSha256, members, boot.Ps1, boot.Saturn), inspection, result.NativeIdentity);
        }
        catch (ChdCdSourceException error) { return new(error.Cause == ChdCdJobCause.UnsupportedMedia ? MediaWorkerStatus.Unsupported : MediaWorkerStatus.Malformed, Failure: new(error.Cause)); }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidDataException or DecoderFallbackException)
        { return new(MediaWorkerStatus.Malformed, Failure: new(ChdCdJobCause.InvalidInput)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(MediaWorkerStatus.IoFailure, Failure: new(ChdCdJobCause.IoFailure)); }
    }
}

internal sealed class ChdCdSourceException(ChdCdJobCause cause) : IOException(cause.ToString()) { internal ChdCdJobCause Cause => cause; }

/// <summary>Held regular descriptors plus before/after named-file identity checks; outer custody is still mandatory.</summary>
internal sealed class ChdCdHeldSources : IDisposable
{
    private readonly Dictionary<string, (ChdCdRawFile File, ChdCdFileHashes Hashes)> _held = new(StringComparer.Ordinal);
    internal ChdCdRequest Request { get; private set; } = null!;
    internal ChdCdCue Cue { get; private set; } = null!;
    internal Dictionary<string, (ChdCdRawFile File, ChdCdFileHashes Hashes)> Originals { get; } = new(StringComparer.Ordinal);
    internal static async Task<ChdCdHeldSources> OpenAsync(string path, long bytes, string sha256, CancellationToken ct)
    {
        var result = new ChdCdHeldSources();
        try
        {
            if (bytes is < 1 or > ChdCdRequest.MaximumBytes || !ChdCdJson.Hash(sha256, 64))
                throw new InvalidDataException("Invalid CD request identity.");
            var request = await result.HoldAsync(path, bytes, sha256, ct);
            byte[] json = new byte[checked((int)bytes)];
            if (request.File.ReadAt(0, json) != json.Length)
                throw new IOException("Truncated CD request.");
            result.Request = ChdCdJson.Read<ChdCdRequest>(json, ChdCdRequest.MaximumBytes);
            result.Request.Validate();
            if (result.Request.Files.Any(f => f.Path == path) || result.Request.ToolCuePath == path)
                throw new InvalidDataException("CD request aliases an input.");
            foreach (var input in result.Request.Files)
                result.Originals.Add(input.Name, await result.HoldAsync(input.Path, input.Bytes, input.Sha256, ct));
            var originalCue = result.Request.Files.Single(f => f.Role == "cue");
            byte[] cueBytes = new byte[(int)originalCue.Bytes];
            if (result.Originals[originalCue.Name].File.ReadAt(0, cueBytes) != cueBytes.Length)
                throw new IOException("Truncated CUE.");
            result.Cue = ChdCdCue.Parse(cueBytes);
            var binPaths = result.Request.Files.Where(f => f.Role == "bin").ToDictionary(f => f.Name, f => f.Path, StringComparer.Ordinal);
            byte[] rendered = result.Cue.RenderToolCue(binPaths, result.Request.ToolCuePath);
            var toolCue = await result.HoldAsync(result.Request.ToolCuePath, rendered.Length, Convert.ToHexStringLower(SHA256.HashData(rendered)), ct);
            if (toolCue.Hashes.Bytes != rendered.Length)
                throw new InvalidDataException("Invalid tool descriptor.");
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    private async Task<(ChdCdRawFile File, ChdCdFileHashes Hashes)> HoldAsync(string path, long bytes, string sha256, CancellationToken ct)
    {
        var file = new ChdCdRawFile(path);
        try
        {
            if (file.Length != bytes)
                throw new ChdCdSourceException(ChdCdJobCause.SourceMismatch);
            var hashes = await ChdCdHashing.MeasureAsync(file, ct);
            if (hashes.Sha256 != sha256)
                throw new ChdCdSourceException(ChdCdJobCause.SourceMismatch);
            _held.Add(path, (file, hashes));
            return (file, hashes);
        }
        catch { file.Dispose(); throw; }
    }
    internal CdBootObservation IdentifyRequiredBoot(CancellationToken ct)
    {
        if (Request.RequiredBootProfile is null)
            return default;
        var observed = CdBootObservation.Inspect(Originals[Cue.Files[0].Name].File, Cue.TrackTypes[0], Cue.FirstTrackIndex01,
            Cue.Files.Sum(file => Originals[file.Name].File.Length / 2352), ct);
        return observed.Valid && observed.Profile == Request.RequiredBootProfile ? observed :
            throw new ChdCdSourceException(ChdCdJobCause.UnsupportedMedia);
    }
    internal async Task AssertUnchangedAsync(CancellationToken ct)
    {
        foreach (var (path, held) in _held)
        {
            if (await ChdCdHashing.MeasureAsync(held.File, ct) != held.Hashes)
                throw new ChdCdSourceException(ChdCdJobCause.SourceChanged);
            using var named = new ChdCdRawFile(path);
            if (await ChdCdHashing.MeasureAsync(named, ct) != held.Hashes)
                throw new ChdCdSourceException(ChdCdJobCause.SourceChanged);
        }
    }
    public void Dispose() { foreach (var held in _held.Values) held.File.Dispose(); }
}

internal sealed class ChdCdRawFile : ISynchronousRawContent
{
    private readonly SafeFileHandle _handle;
    internal ChdCdRawFile(string path)
    {
        if (!ChdCdJson.Absolute(path) || ChdToolPaths.Linked(path))
            throw new InvalidDataException("Unsafe CD source path.");
        _handle = NativeFileLease.OpenRegularRead(path);
        Length = RandomAccess.GetLength(_handle);
    }
    public long Length { get; }
    public int ReadAt(long offset, Span<byte> destination) => RandomAccess.Read(_handle, destination, offset);
    public void Dispose() => _handle.Dispose();
}

internal static class ChdCdHashing
{
    internal static Task<ChdCdFileHashes> MeasureAsync(ISynchronousRawContent source, CancellationToken ct) =>
        MeasureAsync(source.Length, (offset, bytes, _) => new ValueTask<int>(source.ReadAt(offset, bytes.Span)), ct);
    internal static Task<ChdCdFileHashes> MeasureAsync(IRandomAccessContent source, CancellationToken ct) =>
        MeasureAsync(source.Length, source.ReadAtAsync, ct);
    private static async Task<ChdCdFileHashes> MeasureAsync(long length, Func<long, Memory<byte>, CancellationToken, ValueTask<int>> read, CancellationToken ct)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var crc = new System.IO.Hashing.Crc32();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 << 10);
        try
        {
            for (long offset = 0; offset < length;)
            {
                ct.ThrowIfCancellationRequested();
                int take = (int)Math.Min(buffer.Length, length - offset);
                int count = await read(offset, buffer.AsMemory(0, take), ct);
                if (count <= 0 || count > take)
                    throw new IOException("CD source changed length.");
                sha256.AppendData(buffer, 0, count);
                sha1.AppendData(buffer, 0, count);
                md5.AppendData(buffer, 0, count);
                crc.Append(buffer.AsSpan(0, count));
                offset += count;
            }
            return new(length, Convert.ToHexStringLower(sha256.GetHashAndReset()), Convert.ToHexStringLower(sha1.GetHashAndReset()),
                Convert.ToHexStringLower(md5.GetHashAndReset()), crc.GetCurrentHashAsUInt32().ToString("x8", System.Globalization.CultureInfo.InvariantCulture));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
