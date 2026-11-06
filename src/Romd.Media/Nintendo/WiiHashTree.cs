using System.Security.Cryptography;

namespace Romd.Media.Nintendo;

/// <summary>Encoder hash tree over one 64-sector region; missing sector data is all zero.</summary>
internal static class WiiHashTree
{
    private static readonly byte[] ZeroH0 = CreateZeroH0();

    public static void Write(Span<byte> plain, int sectors, CancellationToken ct)
    {
        Span<byte> h1 = stackalloc byte[64 * 20];
        Span<byte> h2 = stackalloc byte[8 * 20];
        for (int sector = 0; sector < 64; sector++)
        {
            ct.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> h0 = ZeroH0;
            if (sector < sectors)
            {
                var hash = plain.Slice(sector * 0x8000, 0x400);
                hash.Clear();
                for (int block = 0; block < 31; block++)
                    SHA1.HashData(plain.Slice(sector * 0x8000 + 0x400 + block * 0x400, 0x400), hash.Slice(block * 20, 20));
                h0 = hash[..0x26C];
            }
            SHA1.HashData(h0, h1.Slice(sector * 20, 20));
        }
        for (int group = 0; group < 8; group++)
            SHA1.HashData(h1.Slice(group * 160, 160), h2.Slice(group * 20, 20));
        for (int sector = 0; sector < sectors; sector++)
        {
            var hash = plain.Slice(sector * 0x8000, 0x400);
            h1.Slice(sector / 8 * 160, 160).CopyTo(hash[0x280..]);
            h2.CopyTo(hash[0x340..]);
        }
    }

    private static byte[] CreateZeroH0()
    {
        byte[] hash = new byte[31 * 20], block = new byte[0x400];
        for (int i = 0; i < 31; i++)
            SHA1.HashData(block, hash.AsSpan(i * 20, 20));
        return hash;
    }
}
