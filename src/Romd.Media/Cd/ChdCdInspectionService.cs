using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Moonlark.Libchdr;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Storage;

namespace Romd.Media.Cd;

public sealed record ChdCdInspectionOutcome(MediaWorkerStatus Status, ChdCdInspection? Inspection = null,
    ChdNativeIdentity? NativeIdentity = null, ChdRejection? Rejection = null);

/// <summary>Measures raw CD tracks in an isolated child. It neither invents original files nor admits CD storage.</summary>
public static partial class ChdCdInspectionService
{
    public const string Profile = "chd-cd-inspection/v2";
    public const string ProjectionProfile = "chd-cd-bin-projection/v1";

    public static Task<ChdCdInspectionOutcome> InspectAsync(string path, ChdNativeCandidate candidate,
        long artifactLimit, long logicalLimit, CancellationToken ct = default) =>
        InspectAsync(path, candidate, artifactLimit, logicalLimit, static source => source, ct);

    internal static async Task<ChdCdInspectionOutcome> InspectAsync(string path, ChdNativeCandidate candidate,
        long artifactLimit, long logicalLimit, Func<IChdDataSource, IChdDataSource> observeNativeSource, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!Path.IsPathFullyQualified(path) || artifactLimit is < 4 or > (16L << 30) || logicalLimit is < 0x81 or > (16L << 30))
            throw new ArgumentException("Absolute staged input and bounded artifact/logical limits are required.");
        ct.ThrowIfCancellationRequested();
        ChdNativeIdentity? native = null;
        ChdRejectionStage stage = ChdRejectionStage.Io;
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
            var limits = ChdCdLimits.Preflight with
            {
                MaximumArtifactBytes = Math.Min(artifactLimit, ChdCdLimits.MaximumArtifactBytes),
                MaximumLogicalBytes = (ulong)Math.Min(logicalLimit, ChdCdLimits.MaximumLogicalBytes)
            };
            var preflight = ChdHeaderPreflight.Inspect(fixedHeader.AsSpan(0, filled), artifact.Length, limits);
            if (preflight is ChdHeaderPreflightResult.Rejected rejected)
                return new(HeaderStatus(rejected.Cause), Rejection: new(ChdRejectionStage.Header,
                    rejected.Cause.ToString(), path, rejected.CodecSlot, rejected.Codec));
            var facts = ((ChdHeaderPreflightResult.Accepted)preflight).Facts;
            if (facts.UnitBytes != ChdCdImage.FrameBytes || facts.LogicalBytes % ChdCdImage.FrameBytes != 0 ||
                facts.HunkBytes % ChdCdImage.FrameBytes != 0)
                return Reject(MediaWorkerStatus.Unsupported, ChdRejectionStage.Header, "InvalidCdGeometry");

            native = candidate.VerifyAndLoad(ct);
            string artifactSha256 = await HashArtifactAsync(artifact, ct);
            stage = ChdRejectionStage.NativeOpen;
            using IChdDataSource source = observeNativeSource(new ChdRawContentSource(artifact));
            using ChdFile file = ChdFile.Open(source, leaveOpen: true, options: new ChdOpenOptions { ReadAheadBytes = 0 });
            ChdHeader header = file.Header;
            if (header.Version != 5 || header.HasParent || header.LogicalBytes != facts.LogicalBytes ||
                header.HunkBytes != facts.HunkBytes || header.UnitBytes != facts.UnitBytes || header.HunkCount != facts.HunkCount ||
                (uint)header.GetCodec(0) != facts.Codec0 || (uint)header.GetCodec(1) != facts.Codec1 ||
                (uint)header.GetCodec(2) != facts.Codec2 || (uint)header.GetCodec(3) != facts.Codec3)
                return Reject(MediaWorkerStatus.Malformed, ChdRejectionStage.Decode, "UnexpectedLogicalLength");

            stage = ChdRejectionStage.Decode;
            int metadataCount = 0;
            foreach (var entry in file.EnumerateMetadata())
            {
                ct.ThrowIfCancellationRequested();
                if (++metadataCount > ChdCdLimits.MaximumTracks)
                    return Reject(MediaWorkerStatus.LimitExceeded, stage, "CdMetadataLimitExceeded");
                if (entry.Tag != ChdMetadataTag.CdTrackV2)
                    return Reject(MediaWorkerStatus.Unsupported, stage, "UnsupportedCdMetadata");
                if (entry.Length > 512)
                    return Reject(MediaWorkerStatus.LimitExceeded, stage, "CdMetadataLimitExceeded");
                if (entry.Flags != 1)
                    return Reject(MediaWorkerStatus.Unsupported, stage, "UnchecksummedCdMetadata");
            }
            using ChdCdImage image = ChdCdImage.Open(file, leaveOpen: true);
            ChdCdTrack[] tracks = image.Tracks.ToArray();
            ChdCdTrackLayout[] layouts = image.TrackLayouts.ToArray();
            foreach (ChdCdTrack track in tracks)
            {
                if (track.PregapFrames > ChdCdLimits.MaximumFrames || track.PostgapFrames > ChdCdLimits.MaximumFrames)
                    return Reject(MediaWorkerStatus.LimitExceeded, stage, "CdMetadataLimitExceeded");
                if (track.Subtype != ChdCdSubcodeType.None)
                    return Reject(MediaWorkerStatus.Unsupported, stage, "UnsupportedCdSubcode");
                if (track.Type is not (ChdCdTrackType.Mode1Raw or ChdCdTrackType.Mode2Raw or ChdCdTrackType.Audio))
                    return Reject(MediaWorkerStatus.Unsupported, stage, "UnsupportedCdTrackType");
            }
            // ChdCdImage and VerifyOverall read metadata separately. Bind the parsed track table to exact
            // checksummed payloads, so a changing source cannot substitute a different layout between them.
            if (!TryCaptureMetadata(file, tracks, ct, out byte[][] metadataHashes, out ChdCdMetadataInspection[] metadata))
                return Reject(MediaWorkerStatus.Malformed, ChdRejectionStage.Integrity, "DecodedMetadataMismatch");

            using var bootSource = new BootTrack(image, tracks[0].Frames, ct);
            var boot = CdBootObservation.Inspect(bootSource, tracks[0].Type,
                tracks[0].PregapStored ? tracks[0].PregapFrames : 0, tracks.Sum(track => (long)track.Frames), ct);

            using var rawSha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var trackHashes = new TrackHashes();
            var measured = new List<ChdCdTrackInspection>(tracks.Length);
            byte[] hunk = ArrayPool<byte>.Shared.Rent(checked((int)facts.HunkBytes));
            int trackIndex = 0;
            ulong firstFrame = 0;
            try
            {
                // Every projected byte is taken from the very hunk pass whose raw hash is verified below.
                for (uint index = 0; index < facts.HunkCount; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    file.ReadHunk(index, hunk);
                    int take = checked((int)Math.Min(facts.LogicalBytes - firstFrame * ChdCdImage.FrameBytes, facts.HunkBytes));
                    rawSha1.AppendData(hunk, 0, take);
                    for (int offset = 0; offset < take; offset += ChdCdImage.FrameBytes, firstFrame++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (trackIndex >= tracks.Length)
                            continue;
                        var track = tracks[trackIndex];
                        var layout = layouts[trackIndex];
                        if (firstFrame < layout.FirstFrame)
                            continue; // CHD-only alignment between tracks.
                        Span<byte> sector = hunk.AsSpan(offset, 2352);
                        if (boot.Profile is not null && trackIndex == 0 && !bootSource.Matches((uint)(firstFrame - layout.FirstFrame), sector))
                            return Reject(MediaWorkerStatus.Malformed, ChdRejectionStage.Integrity, "DecodedBootMismatch");
                        if (track.Type == ChdCdTrackType.Audio)
                            ChdCdImage.SwapAudioSamples16(sector);
                        trackHashes.Append(sector);
                        if (firstFrame + 1 == layout.FirstFrame + track.Frames)
                        {
                            measured.Add(trackHashes.Finish(track, layout));
                            trackIndex++;
                        }
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(hunk, clearArray: true); }
            if (measured.Count != tracks.Length || firstFrame != image.FrameCount)
                return Reject(MediaWorkerStatus.Malformed, stage, "UnexpectedLogicalLength");
            stage = ChdRejectionStage.Integrity;
            byte[] rawDigest = rawSha1.GetHashAndReset();
            ChdIntegrityResult integrity = ChdIntegrity.VerifyOverall(file, ChdSha1.FromBytes(rawDigest), ct);
            if (!integrity.HasStoredRawSha1)
                return Reject(MediaWorkerStatus.Malformed, stage, "MissingRawSha1");
            if (!integrity.HasStoredOverallSha1)
                return Reject(MediaWorkerStatus.Malformed, stage, "MissingOverallSha1");
            if (!integrity.RawMatches)
                return Reject(MediaWorkerStatus.Malformed, stage, "RawSha1Mismatch");
            if (!integrity.OverallMatches)
                return Reject(MediaWorkerStatus.Malformed, stage, "OverallSha1Mismatch");
            using (var boundOverall = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
            {
                boundOverall.AppendData(rawDigest);
                foreach (byte[] record in metadataHashes)
                    boundOverall.AppendData(record);
                if (ChdSha1.FromBytes(boundOverall.GetHashAndReset()) != integrity.ComputedOverallSha1)
                    return Reject(MediaWorkerStatus.Malformed, stage, "DecodedMetadataMismatch");
            }
            stage = ChdRejectionStage.Io;
            if (RandomAccess.GetLength(artifact.Handle) != artifact.Length || await HashArtifactAsync(artifact, ct) != artifactSha256)
                return Reject(MediaWorkerStatus.IoFailure, stage, "SourceChanged");
            return new(MediaWorkerStatus.Inspected, new(Profile, ProjectionProfile, artifactSha256, header.Version, header.HasParent, facts,
                integrity.StoredRawSha1.ToString(), integrity.ComputedRawSha1.ToString(),
                integrity.StoredOverallSha1.ToString(), integrity.ComputedOverallSha1.ToString(), measured.ToArray(), metadata, boot.Ps1, boot.Saturn), native);
        }
        catch (ChdNativeCandidateException error) { return new(error.Status, Rejection: error.Rejection); }
        catch (ChdException error)
        {
            return Reject(error.Error == ChdError.OutOfMemory ? MediaWorkerStatus.LimitExceeded : MediaWorkerStatus.Malformed,
                stage, stage == ChdRejectionStage.NativeOpen ? "NativeOpenFailure" : "NativeDecodeFailure", (int)error.Error);
        }
        catch (IOException) { return Reject(MediaWorkerStatus.IoFailure, ChdRejectionStage.Io, "SourceIo"); }
        catch (UnauthorizedAccessException) { return Reject(MediaWorkerStatus.IoFailure, ChdRejectionStage.Io, "SourceIo"); }

        ChdCdInspectionOutcome Reject(MediaWorkerStatus status, ChdRejectionStage origin, string cause, int? nativeError = null) =>
            new(status, NativeIdentity: origin == ChdRejectionStage.Header ? null : native,
                Rejection: new(origin, cause, path, NativeError: nativeError));
    }

    private static MediaWorkerStatus HeaderStatus(ChdHeaderPreflightCause cause) => cause switch
    {
        ChdHeaderPreflightCause.UnsupportedVersion or ChdHeaderPreflightCause.ParentReference or ChdHeaderPreflightCause.UnsupportedCodec => MediaWorkerStatus.Unsupported,
        ChdHeaderPreflightCause.ArtifactLimitExceeded or ChdHeaderPreflightCause.LogicalLimitExceeded or ChdHeaderPreflightCause.HunkLimitExceeded or
            ChdHeaderPreflightCause.HunkCountOverflow or ChdHeaderPreflightCause.HunkCountLimitExceeded => MediaWorkerStatus.LimitExceeded,
        _ => MediaWorkerStatus.Malformed
    };

    private static bool TryCaptureMetadata(ChdFile file, ChdCdTrack[] tracks, CancellationToken ct, out byte[][] hashes, out ChdCdMetadataInspection[] metadata)
    {
        hashes = new byte[tracks.Length][];
        metadata = new ChdCdMetadataInspection[tracks.Length];
        Span<byte> payload = stackalloc byte[512];
        Span<bool> seen = stackalloc bool[ChdCdLimits.MaximumTracks];
        seen.Clear();
        for (uint index = 0; index < tracks.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (!file.TryGetMetadata(ChdMetadataTag.CdTrackV2, index, payload, out var info) || info.Flags != 1 ||
                info.Length > payload.Length || !ChdCdTrack.TryParse(info.Tag, payload[..(int)info.Length], out var parsed) ||
                parsed.Number > tracks.Length || seen[(int)parsed.Number - 1] || parsed != tracks[(int)parsed.Number - 1])
                return false;
            seen[(int)parsed.Number - 1] = true;
            byte[] record = new byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(record, info.Tag.Value);
            SHA1.HashData(payload[..(int)info.Length], record.AsSpan(4));
            hashes[index] = record;
            metadata[index] = new(info.Tag.Value, info.Flags, Convert.ToHexStringLower(payload[..(int)info.Length]));
        }
        // CHD overall SHA-1 sorts tag plus payload SHA-1 records, independent of metadata order.
        Array.Sort(hashes, static (left, right) => left.AsSpan().SequenceCompareTo(right));
        return true;
    }

    private static async Task<string> HashArtifactAsync(LocalArtifact artifact, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 << 10);
        try
        {
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

    private sealed class TrackHashes : IDisposable
    {
        private readonly IncrementalHash _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly IncrementalHash _sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        private readonly Crc32 _crc32 = new();
        public void Append(ReadOnlySpan<byte> bytes)
        { _sha256.AppendData(bytes); _sha1.AppendData(bytes); _md5.AppendData(bytes); _crc32.Append(bytes); }
        public ChdCdTrackInspection Finish(ChdCdTrack track, ChdCdTrackLayout layout)
        {
            var result = new ChdCdTrackInspection(track.Number, track.Type, layout.FirstFrame, track.Frames, layout.AlignmentFrames,
                track.PregapFrames, track.PregapStored, track.PostgapFrames, checked((long)track.Frames * 2352),
                Convert.ToHexStringLower(_sha256.GetHashAndReset()), Convert.ToHexStringLower(_sha1.GetHashAndReset()),
                Convert.ToHexStringLower(_md5.GetHashAndReset()), _crc32.GetCurrentHashAsUInt32().ToString("x8", CultureInfo.InvariantCulture));
            _crc32.Reset();
            return result;
        }
        public void Dispose() { _sha256.Dispose(); _sha1.Dispose(); _md5.Dispose(); }
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
}
