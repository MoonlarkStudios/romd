using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Romd.Media.Nintendo;
using Romd.Media.PlayStation;
using Romd.Media.Rvz;
using Romd.Storage;

namespace Romd.Media.Inspection;

public sealed record MediaInspectionOutcome(MediaWorkerStatus Status, MediaInspection? Inspection = null,
    MediaIsoInspection? IsoInspection = null, MediaPs2Inspection? Ps2Inspection = null);

/// <summary>Runs inside the dedicated child. Full decoded hashes are computed without a decoded copy.</summary>
public static class MediaInspectionService
{
    public static string DecoderBuild => FormattableString.Invariant(
        $"romd-media/mvid:{typeof(RvzImage).Module.ModuleVersionId};zstdsharp/mvid:{typeof(ZstdSharp.Decompressor).Module.ModuleVersionId};sharpcompress/mvid:{typeof(SharpCompress.Compressors.LZMA.LzmaStream).Module.ModuleVersionId}");

    public static async Task<MediaInspectionOutcome> InspectIsoAsync(string path, long maxArtifactBytes,
        long maxLogicalBytes, CancellationToken ct)
    {
        try
        {
            await using var artifact = new LocalArtifact(path);
            long length = artifact.Length;
            if (length > maxArtifactBytes || length > maxLogicalBytes)
                return new(MediaWorkerStatus.LimitExceeded);
            byte[] magic = new byte[(int)Math.Min(0x20, length)];
            if (!await ReadExactlyAsync(artifact, 0, magic, ct))
                return new(MediaWorkerStatus.Malformed);
            bool recognized = magic.Length >= 0x1C && BinaryPrimitives.ReadUInt32BigEndian(magic.AsSpan(0x18)) == 0x5D1C9EA3 ||
                magic.Length >= 0x20 && BinaryPrimitives.ReadUInt32BigEndian(magic.AsSpan(0x1C)) == 0xC2339F3D;
            var eligibility = recognized ? await NintendoIsoInspector.InspectAsync(artifact, maxLogicalBytes, ct)
                : new NintendoIsoInspectionResult(NintendoIsoInspectionStatus.Unsupported);
            if (eligibility.Status == NintendoIsoInspectionStatus.Malformed)
                return new(MediaWorkerStatus.Malformed);
            if (eligibility.Status == NintendoIsoInspectionStatus.LimitExceeded)
                return new(MediaWorkerStatus.LimitExceeded);
            var ps2 = recognized ? null : await Ps2DvdIdentifier.InspectAsync(artifact, maxLogicalBytes, ct);
            var psp = recognized || ps2?.Status == Ps2DvdIdentificationStatus.Identified
                ? null : await PspUmdIdentifier.InspectAsync(artifact, maxLogicalBytes, ct);
            byte[] buffer = new byte[4 << 20];
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var crc = new System.IO.Hashing.Crc32();
            byte[] measuredPrefix = new byte[magic.Length];
            for (long offset = 0; offset < length;)
            {
                ct.ThrowIfCancellationRequested();
                int read = await artifact.ReadAtAsync(offset, buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - offset)), ct);
                if (read <= 0)
                    return new(MediaWorkerStatus.Malformed);
                if (offset < measuredPrefix.Length)
                    buffer.AsSpan(0, Math.Min(read, measuredPrefix.Length - (int)offset)).CopyTo(measuredPrefix.AsSpan((int)offset));
                sha256.AppendData(buffer, 0, read);
                sha1.AppendData(buffer, 0, read);
                md5.AppendData(buffer, 0, read);
                crc.Append(buffer.AsSpan(0, read));
                offset += read;
            }
            ct.ThrowIfCancellationRequested();
            if (!measuredPrefix.AsSpan().SequenceEqual(magic) || RandomAccess.GetLength(artifact.Handle) != length)
                return new(MediaWorkerStatus.Malformed);
            string hash = Convert.ToHexStringLower(sha256.GetHashAndReset());
            return new(MediaWorkerStatus.Inspected, new(hash, length, hash, Convert.ToHexStringLower(sha1.GetHashAndReset()),
                Convert.ToHexStringLower(md5.GetHashAndReset()),
                crc.GetCurrentHashAsUInt32().ToString("x8", System.Globalization.CultureInfo.InvariantCulture), length, Convert.ToHexStringLower(magic), "identity/v1", eligibility.Layout?.Kind, null,
                eligibility.Layout is { } inspectedLayout ? new(inspectedLayout.WiiPartitionCount, inspectedLayout.StandardKeyPartitions, inspectedLayout.KoreanKeyPartitions) : null, psp),
                new(eligibility.Status, NintendoIsoInspector.ProfileVersion, eligibility.Layout),
                ps2 is null ? null : new(ps2.Status, Ps2DvdIdentifier.ProfileVersion, ps2.Facts));
        }
        catch (InvalidDataException) { return new(MediaWorkerStatus.Malformed); }
        catch (OverflowException) { return new(MediaWorkerStatus.Malformed); }
        catch (IOException) { return new(MediaWorkerStatus.IoFailure); }
        catch (UnauthorizedAccessException) { return new(MediaWorkerStatus.IoFailure); }
    }

    private static async Task<bool> ReadExactlyAsync(IRandomAccessContent source, long offset, Memory<byte> destination, CancellationToken ct)
    {
        for (int position = 0; position < destination.Length;)
        {
            ct.ThrowIfCancellationRequested();
            int read = await source.ReadAtAsync(offset + position, destination[position..], ct);
            if (read <= 0 || read > destination.Length - position)
                return false;
            position += read;
        }
        return true;
    }

    public static async Task<MediaInspectionOutcome> InspectRvzAsync(string path, long maxArtifactBytes, long maxLogicalBytes, CancellationToken ct)
    {
        try
        {
            var artifact = new LocalArtifact(path);
            if (artifact.Length < 4 || artifact.Length > maxArtifactBytes)
            {
                await artifact.DisposeAsync();
                return new(MediaWorkerStatus.LimitExceeded);
            }
            byte[] buffer = new byte[4 << 20];
            string artifactHash;
            try
            {
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (long offset = 0; offset < artifact.Length;)
                {
                    int read = await artifact.ReadAtAsync(offset, buffer.AsMemory(0, (int)Math.Min(buffer.Length, artifact.Length - offset)), ct);
                    if (read == 0)
                    {
                        await artifact.DisposeAsync();
                        return new(MediaWorkerStatus.Malformed);
                    }
                    sha256.AppendData(buffer, 0, read);
                    offset += read;
                }
                artifactHash = Convert.ToHexStringLower(sha256.GetHashAndReset());
            }
            catch { await artifact.DisposeAsync(); throw; }
            var cache = new MediaBlockCache(8L << 20);
            await using var image = await RvzImage.OpenAsync(artifact, new RvzReadOptions { MaxLogicalBytes = maxLogicalBytes }, cache, cacheIdentity: null, ct);
            using var logicalSha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var crc = new System.IO.Hashing.Crc32();
            byte[] measuredPrefix = new byte[(int)Math.Min(image.Length, 32)];
            for (long offset = 0; offset < image.Length;)
            {
                int read = await image.ReadAtAsync(offset, buffer.AsMemory(0, (int)Math.Min(buffer.Length, image.Length - offset)), ct);
                if (read == 0)
                    return new(MediaWorkerStatus.Malformed);
                if (offset < measuredPrefix.Length)
                    buffer.AsSpan(0, Math.Min(read, measuredPrefix.Length - (int)offset)).CopyTo(measuredPrefix.AsSpan((int)offset));
                logicalSha256.AppendData(buffer, 0, read);
                sha1.AppendData(buffer, 0, read);
                md5.AppendData(buffer, 0, read);
                crc.Append(buffer.AsSpan(0, read));
                offset += read;
            }
            var descriptor = image.Descriptor;
            NintendoDiscKind? discKind = null;
            NintendoIsoLayout? logicalLayout = null;
            byte[] prefix = new byte[(int)Math.Min(image.Length, 32)];
            if (!await ReadExactlyAsync(image, 0, prefix, ct) || !prefix.AsSpan().SequenceEqual(measuredPrefix))
                return new(MediaWorkerStatus.Malformed);
            bool recognized = prefix.Length >= 0x1C && BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(0x18)) == 0x5D1C9EA3 ||
                prefix.Length >= 0x20 && BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(0x1C)) == 0xC2339F3D;
            if (recognized)
            {
                var layout = await NintendoIsoInspector.InspectAsync(image, maxLogicalBytes, ct);
                if (layout.Status == NintendoIsoInspectionStatus.Eligible && layout.Layout is { } supported)
                {
                    if (descriptor.DiscType != (supported.Kind == NintendoDiscKind.GameCube ? 1u : 2u))
                        return new(MediaWorkerStatus.Malformed);
                    discKind = supported.Kind;
                    logicalLayout = supported;
                }
            }
            string profile = RvzInspectionProfile.Capture(descriptor);
            return new(MediaWorkerStatus.Inspected, new(artifactHash, artifact.Length, Convert.ToHexStringLower(logicalSha256.GetHashAndReset()),
                Convert.ToHexStringLower(sha1.GetHashAndReset()), Convert.ToHexStringLower(md5.GetHashAndReset()),
                crc.GetCurrentHashAsUInt32().ToString("x8", System.Globalization.CultureInfo.InvariantCulture), image.Length, Convert.ToHexStringLower(prefix), profile, discKind, descriptor,
                logicalLayout is { } observedLayout ? new(observedLayout.WiiPartitionCount, observedLayout.StandardKeyPartitions, observedLayout.KoreanKeyPartitions) : null));
        }
        catch (NotSupportedException) { return new(MediaWorkerStatus.Unsupported); }
        catch (InvalidDataException) { return new(MediaWorkerStatus.Malformed); }
        catch (OverflowException) { return new(MediaWorkerStatus.Malformed); }
        catch (IOException) { return new(MediaWorkerStatus.IoFailure); }
        catch (UnauthorizedAccessException) { return new(MediaWorkerStatus.IoFailure); }
    }

    private sealed class LocalArtifact : IRandomAccessContent
    {
        private readonly SafeFileHandle _handle;
        public LocalArtifact(string path)
        {
            _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            Length = RandomAccess.GetLength(_handle);
        }
        public long Length { get; }
        public SafeFileHandle Handle => _handle;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default) => RandomAccess.ReadAsync(_handle, destination, offset, ct);
        public ValueTask DisposeAsync() { _handle.Dispose(); return ValueTask.CompletedTask; }
    }
}
