using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Moonlark.Libchdr;

/// <summary>Recomputes the format's SHA-1 checksums from every decoded logical byte, or completes them from a raw SHA-1
/// the caller computed over the same bytes.</summary>
/// <remarks>SHA-1 is required by the CHD file format and is not an authentication mechanism.</remarks>
public static class ChdIntegrity
{
    /// <summary>Verifies a complete synchronous decode using CHD's hash rules.</summary>
    /// <param name="file">The decoder, which remains open.</param>
    /// <returns>Computed and stored hashes, including explicit absent/mismatching status.</returns>
    /// <exception cref="InvalidOperationException">Another read of the file is in progress.</exception>
    public static ChdIntegrityResult Verify(ChdFile file) => Verify(file, CancellationToken.None);

    /// <summary>Verifies a complete decode, checking cancellation between hunks and metadata chunks.</summary>
    /// <param name="file">The decoder, which remains open.</param>
    /// <param name="cancellationToken">Cancellation between synchronous native operations.</param>
    /// <returns>Computed and stored hashes; a mismatch or absence does not report success.</returns>
    /// <exception cref="InvalidOperationException">Another read of the file is in progress.</exception>
    public static ChdIntegrityResult Verify(ChdFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ChdHeader header = file.Header;
        cancellationToken.ThrowIfCancellationRequested();
        ChdSha1 raw;
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)header.HunkBytes);
        try
        { raw = HashLogical(file, header, buffer, cancellationToken); }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        return VerifyOverall(file, raw, cancellationToken);
    }

    /// <summary>Completes verification from a raw SHA-1 that the caller computed during its own complete decode.</summary>
    /// <remarks>This lets a caller that already decodes every hunk avoid a second decode. The result reports
    /// <paramref name="computedRawSha1"/> as computed without rechecking it, so the caller must hash exactly the bytes
    /// <see cref="Verify(ChdFile, CancellationToken)"/> hashes. Passing the stored raw hash verifies nothing.</remarks>
    /// <param name="file">The decoder, which remains open.</param>
    /// <param name="computedRawSha1">The SHA-1 of every hunk from <see cref="ChdFile.ReadHunk"/> in index order,
    /// truncated to <see cref="ChdHeader.LogicalBytes"/>.</param>
    /// <param name="cancellationToken">Cancellation between metadata chunks.</param>
    /// <returns>Stored hashes, the supplied raw hash and the overall hash computed from it; a mismatch or absence does
    /// not report success.</returns>
    /// <exception cref="InvalidOperationException">Another read of a version 4 or later file is in progress.</exception>
    public static ChdIntegrityResult VerifyOverall(ChdFile file, ChdSha1 computedRawSha1, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ChdHeader header = file.Header;
        cancellationToken.ThrowIfCancellationRequested();
        if (header.Version < 4)
            return new(computedRawSha1, header.RawSha1, computedRawSha1, header.OverallSha1);
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)header.HunkBytes);
        try
        {
            ChdSha1 overall = HashOverall(file, computedRawSha1, buffer, cancellationToken);
            return new(computedRawSha1, header.RawSha1, overall, header.OverallSha1);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static ChdSha1 HashLogical(ChdFile file, ChdHeader header, byte[] buffer, CancellationToken token)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        ulong remaining = header.LogicalBytes;
        for (uint index = 0; index < header.HunkCount; index++)
        {
            token.ThrowIfCancellationRequested();
            file.ReadHunk(index, buffer);
            int take = (int)Math.Min(remaining, header.HunkBytes);
            hash.AppendData(buffer.AsSpan(0, take));
            remaining -= (uint)take;
        }
        return Finish(hash);
    }

    private static ChdSha1 HashOverall(ChdFile file, ChdSha1 raw, byte[] buffer, CancellationToken token)
    {
        var records = new List<MetadataHash>(file.MetadataCount);
        using IncrementalHash payload = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        for (int index = 0; index < file.MetadataCount; index++)
        {
            ChdMetadataInfo info = file.MetadataAt(index);
            if (!info.IsChecksummed)
                continue;
            uint offset = 0;
            while (offset < info.Length)
            {
                token.ThrowIfCancellationRequested();
                int read = file.ReadMetadataPart(index, offset, buffer);
                payload.AppendData(buffer.AsSpan(0, read));
                offset += (uint)read;
            }
            records.Add(new(info.Tag.Value, Finish(payload)));
        }
        records.Sort(static (left, right) => left.CompareTo(right));
        using IncrementalHash overall = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        Span<byte> record = stackalloc byte[24];
        raw.TryWriteBytes(record);
        overall.AppendData(record[..20]);
        foreach (MetadataHash value in records)
        {
            token.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteUInt32BigEndian(record, value.Tag);
            value.Hash.TryWriteBytes(record[4..]);
            overall.AppendData(record);
        }
        return Finish(overall);
    }

    private static ChdSha1 Finish(IncrementalHash hash)
    {
        Span<byte> bytes = stackalloc byte[20];
        _ = hash.GetHashAndReset(bytes);
        return ChdSha1.FromBytes(bytes);
    }

    private readonly record struct MetadataHash(uint Tag, ChdSha1 Hash)
    {
        internal int CompareTo(MetadataHash other)
        {
            int tag = Tag.CompareTo(other.Tag);
            return tag != 0 ? tag : Hash.CompareBytes(other.Hash);
        }
    }
}
