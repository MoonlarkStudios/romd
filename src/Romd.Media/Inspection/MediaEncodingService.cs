using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using Romd.Media.Nintendo;
using Romd.Media.Rvz;
using Romd.Storage;

namespace Romd.Media.Inspection;

public sealed record MediaEncodingOutcome(MediaWorkerStatus Status, MediaEncoding? Encoding = null);

/// <summary>Child-only canonical encoding. Artifact metadata never issues decoded-output evidence.</summary>
public static class MediaEncodingService
{
    public static async Task<MediaEncodingOutcome> EncodeAsync(string sourcePath, string outputPath,
        long maxArtifactBytes, long maxLogicalBytes, long logicalBytes, long maxOutputBytes,
        NintendoDiscKind discKind, string priorityKind, int priorityValue, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(sourcePath) || !Path.IsPathFullyQualified(outputPath) ||
            string.Equals(sourcePath, outputPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            maxArtifactBytes < 4 || maxLogicalBytes <= 0x80 || maxOutputBytes < 0x124)
            return new(MediaWorkerStatus.LimitExceeded);
        if (RvzCodecFamily.For(discKind) is not { } family)
            return new(MediaWorkerStatus.Unsupported);
        var bound = family.GetOutputBound(logicalBytes, maxLogicalBytes, ct);
        if (bound is not RvzCanonicalOutputBound.Success accepted || accepted.MaximumOutputBytes > maxOutputBytes)
            return new(MediaWorkerStatus.LimitExceeded);
        try
        {
            await using var artifact = new LocalArtifact(sourcePath);
            if (artifact.Length < 4 || artifact.Length > maxArtifactBytes)
                return new(MediaWorkerStatus.LimitExceeded);
            var sourceHashes = await HashAsync(artifact, ct);
            if (sourceHashes is null)
                return new(MediaWorkerStatus.Malformed);
            byte[] magic = new byte[4];
            if (await artifact.ReadAtAsync(0, magic, ct) != 4)
                return new(MediaWorkerStatus.Malformed);
            await using var rvz = magic.AsSpan().SequenceEqual("RVZ\x01"u8)
                ? await RvzImage.OpenAsync(artifact, new RvzReadOptions { MaxLogicalBytes = maxLogicalBytes },
                    new MediaBlockCache(8L << 20), cacheIdentity: null, ct) : null;
            IRandomAccessContent logical = rvz is null ? artifact : rvz;
            if (logical.Length != logicalBytes)
                return new(MediaWorkerStatus.Malformed);
            var layout = await NintendoIsoInspector.InspectAsync(logical, maxLogicalBytes, ct);
            if (layout.Status == NintendoIsoInspectionStatus.Unsupported)
                return new(MediaWorkerStatus.Unsupported);
            if (layout.Status == NintendoIsoInspectionStatus.LimitExceeded)
                return new(MediaWorkerStatus.LimitExceeded);
            if (layout.Status != NintendoIsoInspectionStatus.Eligible || layout.Layout is null)
                return new(MediaWorkerStatus.Malformed);
            if (layout.Layout.Kind != discKind)
                return new(MediaWorkerStatus.Unsupported);
            if (rvz is not null && rvz.Descriptor.DiscType != family.ContainerDiscType)
                return new(MediaWorkerStatus.Malformed);

            RvzEncodeResult result;
            await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                131072, FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                result = await family.EncodeAsync(logical, output, maxLogicalBytes, maxOutputBytes, ct);
                if (result is RvzEncodeResult.Success)
                    await output.FlushAsync(ct);
            }
            if (result is RvzEncodeResult.Failure failed)
                return new(failed.Kind switch
                {
                    RvzEncodeFailureKind.UnsupportedMedia => MediaWorkerStatus.Unsupported,
                    RvzEncodeFailureKind.LogicalLimitExceeded or RvzEncodeFailureKind.OutputLimitExceeded or RvzEncodeFailureKind.InvalidOptions => MediaWorkerStatus.LimitExceeded,
                    _ => MediaWorkerStatus.Malformed
                });
            var complete = (RvzEncodeResult.Success)result;
            if (RandomAccess.GetLength(artifact.Handle) != artifact.Length)
                return new(MediaWorkerStatus.Malformed);
            await using var encodedArtifact = new LocalArtifact(outputPath);
            if (encodedArtifact.Length != complete.ArtifactBytes || encodedArtifact.Length > maxOutputBytes)
                return new(MediaWorkerStatus.LimitExceeded);
            var outputHashes = await HashAsync(encodedArtifact, ct);
            if (outputHashes is null)
                return new(MediaWorkerStatus.Malformed);
            await using var encoded = await RvzImage.OpenAsync(encodedArtifact,
                new RvzReadOptions { MaxLogicalBytes = maxLogicalBytes }, new MediaBlockCache(8L << 20), cacheIdentity: null, ct);
            string profile = RvzInspectionProfile.Capture(encoded.Descriptor);
            if (encoded.Length != logicalBytes || profile != MediaWorkerProtocol.CanonicalEncodingProfile ||
                encoded.Descriptor.DiscType != family.ContainerDiscType || encoded.Descriptor.HasWiiPartitions != family.HasWiiPartitions ||
                complete.EncoderBuild != family.EncoderBuild)
                return new(MediaWorkerStatus.Malformed);
            return new(MediaWorkerStatus.Encoded, new(sourceHashes.Sha256, artifact.Length, logicalBytes,
                outputHashes.Sha256, outputHashes.Md5, outputHashes.Sha1, encodedArtifact.Length,
                RvzCanonicalRecipe.Id, complete.EncoderBuild, profile, priorityKind, priorityValue));
        }
        catch (NotSupportedException) { return new(MediaWorkerStatus.Unsupported); }
        catch (InvalidDataException) { return new(MediaWorkerStatus.Malformed); }
        catch (OverflowException) { return new(MediaWorkerStatus.Malformed); }
        catch (IOException) { return new(MediaWorkerStatus.IoFailure); }
        catch (UnauthorizedAccessException) { return new(MediaWorkerStatus.IoFailure); }
    }

    private sealed record ArtifactHashes(string Sha256, string Md5, string Sha1);
    private static async Task<ArtifactHashes?> HashAsync(LocalArtifact artifact, CancellationToken ct)
    {
        byte[] buffer = new byte[131072];
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        for (long offset = 0; offset < artifact.Length;)
        {
            ct.ThrowIfCancellationRequested();
            int read = await artifact.ReadAtAsync(offset, buffer.AsMemory(0, (int)Math.Min(buffer.Length, artifact.Length - offset)), ct);
            if (read <= 0)
                return null;
            sha256.AppendData(buffer, 0, read);
            md5.AppendData(buffer, 0, read);
            sha1.AppendData(buffer, 0, read);
            offset += read;
        }
        if (RandomAccess.GetLength(artifact.Handle) != artifact.Length)
            return null;
        return new(Convert.ToHexStringLower(sha256.GetHashAndReset()), Convert.ToHexStringLower(md5.GetHashAndReset()),
            Convert.ToHexStringLower(sha1.GetHashAndReset()));
    }

    private sealed class LocalArtifact : IRandomAccessContent
    {
        internal SafeFileHandle Handle { get; }
        public LocalArtifact(string path)
        {
            Handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            Length = RandomAccess.GetLength(Handle);
        }
        public long Length { get; }
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default) => RandomAccess.ReadAsync(Handle, destination, offset, ct);
        public ValueTask DisposeAsync() { Handle.Dispose(); return ValueTask.CompletedTask; }
    }
}
