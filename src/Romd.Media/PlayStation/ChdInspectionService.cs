using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Moonlark.Libchdr;
using Romd.Media.Inspection;
using Romd.Storage;

namespace Romd.Media.PlayStation;

public sealed record ChdInspection(uint Version, ChdHeaderFacts Header, string StoredRawSha1,
    string ComputedRawSha1, string StoredOverallSha1, string ComputedOverallSha1,
    ChdDvdMetadataObservation? DvdMetadata = null);

public sealed record ChdInspectionOutcome(MediaWorkerStatus Status, MediaInspection? Inspection = null,
    ChdInspection? ChdInspection = null, ChdNativeIdentity? NativeIdentity = null, ChdRejection? Rejection = null,
    MediaPs2Inspection? Ps2Inspection = null);

/// <summary>Opt-in DVD-shaped CHD inspection inside a supervised child; no production eligibility claim.</summary>
/// <remarks>Synchronous native operations rely on the supervisor's outer process bound; cancellation is checked between hunks.</remarks>
public static class ChdInspectionService
{
    public const string Profile = "chd-import/v1";

    public static Task<ChdInspectionOutcome> InspectAsync(string path, ChdNativeCandidate candidate,
        long artifactLimit, long logicalLimit, CancellationToken ct = default) =>
        InspectAsync(path, candidate, artifactLimit, logicalLimit, static source => source, ct);

    // Test seam: observes the native decoder's artifact reads without changing what inspection decides.
    internal static async Task<ChdInspectionOutcome> InspectAsync(string path, ChdNativeCandidate candidate,
        long artifactLimit, long logicalLimit, Func<IChdDataSource, IChdDataSource> observeNativeSource, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!Path.IsPathFullyQualified(path) || artifactLimit is < 4 or > (16L << 30) || logicalLimit is < 0x81 or > (16L << 30))
            throw new ArgumentException("Absolute staged input and bounded artifact/logical limits are required.");
        ct.ThrowIfCancellationRequested();
        ChdRejectionStage stage = ChdRejectionStage.Io;
        ChdNativeIdentity? native = null;
        try
        {
            using var artifact = new LocalArtifact(path);
            byte[] fixedHeader = new byte[124];
            int filled = 0;
            while (filled < fixedHeader.Length)
            {
                int read = await RandomAccess.ReadAsync(artifact.Handle, fixedHeader.AsMemory(filled), filled, ct);
                if (read == 0)
                    break;
                filled += read;
            }
            var limits = ChdDvdLimits.Preflight with
            {
                MaximumArtifactBytes = Math.Min(artifactLimit, ChdDvdLimits.MaximumArtifactBytes),
                MaximumLogicalBytes = (ulong)Math.Min(logicalLimit, ChdDvdLimits.MaximumLogicalBytes)
            };
            ChdHeaderPreflightResult preflight = ChdHeaderPreflight.Inspect(fixedHeader.AsSpan(0, filled), artifact.Length, limits);
            if (preflight is ChdHeaderPreflightResult.Rejected rejected)
                return new(HeaderStatus(rejected.Cause), Rejection: new(ChdRejectionStage.Header,
                    rejected.Cause.ToString(), path, rejected.CodecSlot, rejected.Codec));
            ChdHeaderFacts facts = ((ChdHeaderPreflightResult.Accepted)preflight).Facts;
            if (facts.UnitBytes != 2048 || facts.LogicalBytes % 2048 != 0 || facts.HunkBytes % 2048 != 0)
                return Reject(MediaWorkerStatus.Unsupported, ChdRejectionStage.Header, "InvalidDvdGeometry");
            uint[] codecs = [facts.Codec0, facts.Codec1, facts.Codec2, facts.Codec3];
            for (int slot = 0; slot < codecs.Length; slot++)
                if (ChdDvdLimits.IsCdCodec(codecs[slot]))
                    return new(MediaWorkerStatus.Unsupported, Rejection: new(ChdRejectionStage.Header,
                        "UnsupportedDvdCodec", path, slot, codecs[slot]));

            // No native file is opened or verified until the independent fixed-header guard accepts.
            native = candidate.VerifyAndLoad(ct);
            string artifactSha256 = await HashArtifactAsync(artifact, ct);
            stage = ChdRejectionStage.NativeOpen;
            using IChdDataSource source = observeNativeSource(new ChdRawContentSource(artifact));
            using ChdFile chd = ChdFile.Open(source, leaveOpen: true, options: new ChdOpenOptions { ReadAheadBytes = 0 });
            ChdHeader header = chd.Header;
            if (header.Version != 5 || header.HasParent || header.LogicalBytes != facts.LogicalBytes ||
                header.HunkBytes != facts.HunkBytes || header.UnitBytes != facts.UnitBytes || header.HunkCount != facts.HunkCount ||
                (uint)header.GetCodec(0) != facts.Codec0 || (uint)header.GetCodec(1) != facts.Codec1 ||
                (uint)header.GetCodec(2) != facts.Codec2 || (uint)header.GetCodec(3) != facts.Codec3)
                return Reject(MediaWorkerStatus.Malformed, ChdRejectionStage.Decode, "UnexpectedLogicalLength");

            stage = ChdRejectionStage.Decode;
            ct.ThrowIfCancellationRequested();
            if (!ChdDvdMetadataObserver.TryObserve(chd, out var metadata))
                return Reject(MediaWorkerStatus.Malformed, stage, "InvalidDvdMetadata");
            // PS2 identification reads a few bounded regions whose positions depend on the disc, so it runs first and
            // records a digest of every hunk it decoded; the verified pass below must decode those hunks identically.
            Ps2DvdIdentificationResult ps2;
            PspUmdObservedFacts? psp;
            IReadOnlyDictionary<uint, byte[]> identifiedHunks;
            using (var logical = new BorrowedLogicalContent(chd))
            {
                ps2 = await Ps2DvdIdentifier.InspectAsync(logical, logicalLimit, ct);
                psp = ps2.Status == Ps2DvdIdentificationStatus.Identified ? null : await PspUmdIdentifier.InspectAsync(logical, logicalLimit, ct);
                if (logical.Diverged)
                    return Reject(MediaWorkerStatus.Malformed, ChdRejectionStage.Integrity, "DecodedHunkMismatch");
                identifiedHunks = logical.DecodedHunks;
            }
            // The only full decode: CHD's raw SHA-1 is the logical SHA-1, so verification completes from it.
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var crc32 = new System.IO.Hashing.Crc32();
            byte[] buffer = ArrayPool<byte>.Shared.Rent((int)facts.HunkBytes);
            ulong remaining = facts.LogicalBytes;
            byte[] logicalPrefix = new byte[(int)Math.Min(32UL, facts.LogicalBytes)];
            bool identifiedBytesVerified = true;
            try
            {
                for (uint index = 0; index < facts.HunkCount; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    chd.ReadHunk(index, buffer);
                    if (index == 0)
                        buffer.AsSpan(0, logicalPrefix.Length).CopyTo(logicalPrefix);
                    if (identifiedHunks.TryGetValue(index, out byte[]? identified) &&
                        !SHA256.HashData(buffer.AsSpan(0, (int)facts.HunkBytes)).AsSpan().SequenceEqual(identified))
                        identifiedBytesVerified = false;
                    int take = (int)Math.Min(remaining, facts.HunkBytes);
                    sha256.AppendData(buffer, 0, take);
                    sha1.AppendData(buffer, 0, take);
                    md5.AppendData(buffer, 0, take);
                    crc32.Append(buffer.AsSpan(0, take));
                    remaining -= (uint)take;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            byte[] logicalSha1Bytes = sha1.GetHashAndReset();
            ChdIntegrityResult integrity = ChdIntegrity.VerifyOverall(chd, ChdSha1.FromBytes(logicalSha1Bytes), ct);
            if (!integrity.HasStoredRawSha1)
                return Reject(MediaWorkerStatus.Malformed, ChdRejectionStage.Integrity, "MissingRawSha1");
            if (!integrity.HasStoredOverallSha1)
                return Reject(MediaWorkerStatus.Malformed, ChdRejectionStage.Integrity, "MissingOverallSha1");
            if (!integrity.RawMatches)
                return Reject(MediaWorkerStatus.Malformed, ChdRejectionStage.Integrity, "RawSha1Mismatch");
            if (!integrity.OverallMatches)
                return Reject(MediaWorkerStatus.Malformed, ChdRejectionStage.Integrity, "OverallSha1Mismatch");
            // PS2 facts are kept only when every hunk they were read from decoded to the bytes the SHA-1s just verified.
            if (!identifiedBytesVerified)
                return Reject(MediaWorkerStatus.Malformed, ChdRejectionStage.Integrity, "DecodedHunkMismatch");
            if (remaining != 0)
                return Reject(MediaWorkerStatus.Malformed, stage, "UnexpectedLogicalLength");
            string logicalSha1 = Convert.ToHexStringLower(logicalSha1Bytes);
            stage = ChdRejectionStage.Io;
            if (RandomAccess.GetLength(artifact.Handle) != artifact.Length || await HashArtifactAsync(artifact, ct) != artifactSha256)
                return Reject(MediaWorkerStatus.IoFailure, stage, "SourceChanged");
            return new(MediaWorkerStatus.Inspected,
                new(artifactSha256, artifact.Length, Convert.ToHexStringLower(sha256.GetHashAndReset()), logicalSha1,
                    Convert.ToHexStringLower(md5.GetHashAndReset()), crc32.GetCurrentHashAsUInt32().ToString("x8", CultureInfo.InvariantCulture),
                    checked((long)facts.LogicalBytes), Convert.ToHexStringLower(logicalPrefix), Profile, Psp: psp),
                new(header.Version, facts, integrity.StoredRawSha1.ToString(), integrity.ComputedRawSha1.ToString(),
                    integrity.StoredOverallSha1.ToString(), integrity.ComputedOverallSha1.ToString(), metadata), native,
                Ps2Inspection: new(ps2.Status, Ps2DvdIdentifier.ProfileVersion, ps2.Facts));
        }
        catch (ChdNativeCandidateException error) { return new(error.Status, Rejection: error.Rejection); }
        catch (ChdException error)
        {
            return Reject(error.Error == ChdError.OutOfMemory ? MediaWorkerStatus.LimitExceeded : MediaWorkerStatus.Malformed,
                stage, stage == ChdRejectionStage.NativeOpen ? "NativeOpenFailure" : "NativeDecodeFailure", (int)error.Error);
        }
        catch (IOException) { return Reject(MediaWorkerStatus.IoFailure, ChdRejectionStage.Io, "SourceIo"); }
        catch (UnauthorizedAccessException) { return Reject(MediaWorkerStatus.IoFailure, ChdRejectionStage.Io, "SourceIo"); }

        ChdInspectionOutcome Reject(MediaWorkerStatus status, ChdRejectionStage origin, string cause, int? nativeError = null) =>
            new(status, NativeIdentity: origin is ChdRejectionStage.Header or ChdRejectionStage.NativeVerification or ChdRejectionStage.NativeLoad ? null : native,
                Rejection: new(origin, cause, path, NativeError: nativeError));
    }


    private static MediaWorkerStatus HeaderStatus(ChdHeaderPreflightCause cause) => cause switch
    {
        ChdHeaderPreflightCause.UnsupportedVersion or ChdHeaderPreflightCause.ParentReference or ChdHeaderPreflightCause.UnsupportedCodec => MediaWorkerStatus.Unsupported,
        ChdHeaderPreflightCause.ArtifactLimitExceeded or ChdHeaderPreflightCause.LogicalLimitExceeded or ChdHeaderPreflightCause.HunkLimitExceeded or
            ChdHeaderPreflightCause.HunkCountOverflow or ChdHeaderPreflightCause.HunkCountLimitExceeded => MediaWorkerStatus.LimitExceeded,
        _ => MediaWorkerStatus.Malformed
    };

    private static async Task<string> HashArtifactAsync(LocalArtifact artifact, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 << 10);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (long offset = 0; offset < artifact.Length;)
            {
                int read = await RandomAccess.ReadAsync(artifact.Handle, buffer.AsMemory(0, (int)Math.Min(buffer.Length, artifact.Length - offset)), offset, ct);
                if (read == 0)
                    throw new EndOfStreamException("Staged CHD was truncated.");
                hash.AppendData(buffer, 0, read);
                offset += read;
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private sealed class LocalArtifact : ISynchronousRawContent
    {
        public LocalArtifact(string path)
        {
            Handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
            try
            { Length = RandomAccess.GetLength(Handle); }
            catch { Handle.Dispose(); throw; }
        }
        public SafeFileHandle Handle { get; }
        public long Length { get; }
        public int ReadAt(long offset, Span<byte> destination)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (offset >= Length)
                return 0;
            int take = (int)Math.Min(destination.Length, Length - offset), total = 0;
            while (total < take)
            {
                int read = RandomAccess.Read(Handle, destination.Slice(total, take - total), offset + total);
                if (read == 0)
                    throw new EndOfStreamException("Staged CHD was truncated.");
                total += read;
            }
            return total;
        }
        public void Dispose() => Handle.Dispose();
    }

    // One borrowed handle and bounded hunk buffer, inside the already admitted child.
    // Disposing this view returns its buffer; the inspection owns the CHD lifetime.
    // It records the SHA-256 of every hunk it decodes so the verified pass can prove those bytes; the PS2 identifier's
    // own read limits bound how many hunks that is.
    private sealed class BorrowedLogicalContent : IRandomAccessContent, IDisposable
    {
        private readonly ChdFile _file;
        private readonly byte[] _buffer;
        private readonly uint _hunkBytes;
        private readonly Dictionary<uint, byte[]> _decoded = [];
        private uint? _loaded;
        public BorrowedLogicalContent(ChdFile file)
        {
            _file = file;
            Length = checked((long)file.Header.LogicalBytes);
            _hunkBytes = file.Header.HunkBytes;
            _buffer = ArrayPool<byte>.Shared.Rent(checked((int)_hunkBytes));
        }
        public long Length { get; }
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public IReadOnlyDictionary<uint, byte[]> DecodedHunks => _decoded;
        // A hunk this view decoded twice to different bytes; the decoder is not deterministic for this artifact.
        public bool Diverged { get; private set; }
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ct.ThrowIfCancellationRequested();
            if (offset >= Length || destination.IsEmpty)
                return ValueTask.FromResult(0);
            int take = (int)Math.Min(destination.Length, Length - offset), copied = 0;
            while (copied < take)
            {
                ct.ThrowIfCancellationRequested();
                long at = offset + copied;
                uint index = checked((uint)(at / _hunkBytes));
                int within = (int)(at % _hunkBytes);
                if (_loaded != index)
                { _file.ReadHunk(index, _buffer); _loaded = index; Record(index); }
                int count = Math.Min(take - copied, checked((int)_hunkBytes) - within);
                _buffer.AsMemory(within, count).CopyTo(destination[copied..]);
                copied += count;
            }
            return ValueTask.FromResult(copied);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);

        private void Record(uint index)
        {
            byte[] digest = SHA256.HashData(_buffer.AsSpan(0, checked((int)_hunkBytes)));
            if (_decoded.TryAdd(index, digest))
                return;
            if (!_decoded[index].AsSpan().SequenceEqual(digest))
                Diverged = true;
        }
    }
}
