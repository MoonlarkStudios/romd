using System.Security.Cryptography;

namespace Romd.Media.Nintendo;

/// <summary>A 20-byte replacement for a recomputed hash, applied before encryption.</summary>
internal readonly record struct HashException(int Sector, int Offset, byte[] Hash);

/// <summary>
/// Rebuilds one encrypted 2 MiB Wii region (64 sectors) from decrypted sector data: H0/H1/H2
/// hash tree, hash exceptions, then AES-128-CBC with the partition key (see WiiBrew's Wii disc
/// layout and Dolphin's docs/WiaAndRvz.md).
/// </summary>
internal static class WiiRegion
{
    public const int SectorSize = 0x8000, SectorData = 0x7C00, HashBlock = 0x400, Sectors = 64;
    public const int HashSize = 20;
    private const int BlockSize = 0x400, Blocks = 31, H0Size = Blocks * HashSize, H1Offset = 0x280, H2Offset = 0x340, IvOffset = 0x3D0;

    // Sectors past the partition end are hashed as zero-filled data (Dolphin and wit agree).
    private static readonly byte[] s_zeroSectorH0 = CreateZeroSectorH0();

    /// <param name="plain">64 sector slots of 0x8000 bytes. Data lives at +0x400; hash blocks are overwritten.</param>
    /// <param name="output">Receives <paramref name="sectors"/> encrypted sectors.</param>
    public static void Build(byte[] key, Span<byte> plain, int sectors, IReadOnlyList<HashException> exceptions, Span<byte> output)
    {
        Span<byte> h1 = stackalloc byte[Sectors * HashSize];
        Span<byte> h2 = stackalloc byte[8 * HashSize];
        for (int s = 0; s < Sectors; s++)
        {
            ReadOnlySpan<byte> h0 = s_zeroSectorH0;
            if (s < sectors)
            {
                var sector = plain.Slice(s * SectorSize, SectorSize);
                var hashBlock = sector[..HashBlock];
                hashBlock.Clear();
                for (int b = 0; b < Blocks; b++)
                    SHA1.HashData(sector.Slice(HashBlock + b * BlockSize, BlockSize), hashBlock.Slice(b * HashSize, HashSize));
                h0 = hashBlock[..H0Size];
            }
            SHA1.HashData(h0, h1.Slice(s * HashSize, HashSize));
        }
        for (int group = 0; group < 8; group++)
            SHA1.HashData(h1.Slice(group * 8 * HashSize, 8 * HashSize), h2.Slice(group * HashSize, HashSize));
        for (int s = 0; s < sectors; s++)
        {
            var hashBlock = plain.Slice(s * SectorSize, HashBlock);
            h1.Slice(s / 8 * 8 * HashSize, 8 * HashSize).CopyTo(hashBlock[H1Offset..]);
            h2.CopyTo(hashBlock[H2Offset..]);
        }
        foreach (var exception in exceptions)
            exception.Hash.CopyTo(plain.Slice(exception.Sector * SectorSize + exception.Offset, HashSize));

        using var aes = Aes.Create();
        aes.Key = key;
        Span<byte> zeroIv = stackalloc byte[16];
        zeroIv.Clear();
        for (int s = 0; s < sectors; s++)
        {
            var source = plain.Slice(s * SectorSize, SectorSize);
            var target = output.Slice(s * SectorSize, SectorSize);
            aes.EncryptCbc(source[..HashBlock], zeroIv, target[..HashBlock], PaddingMode.None);
            aes.EncryptCbc(source[HashBlock..], target.Slice(IvOffset, 16), target[HashBlock..], PaddingMode.None);
        }
    }

    private static byte[] CreateZeroSectorH0()
    {
        var zero = new byte[BlockSize];
        var h0 = new byte[H0Size];
        for (int b = 0; b < Blocks; b++)
            SHA1.HashData(zero, h0.AsSpan(b * HashSize, HashSize));
        return h0;
    }
}
