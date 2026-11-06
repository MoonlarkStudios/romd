using System.Buffers.Binary;

namespace Romd.Media.TestSupport;

/// <summary>Independent logical ISO bytes; no production encoder is used to create expected data.</summary>
public static class GameCubeIsoFixture
{
    public static byte[] Create(int length, int seed = 17)
    {
        if (length <= 0x80)
            throw new ArgumentOutOfRangeException(nameof(length));
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        "GMDE01"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x18), 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x1C), 0xC2339F3D);
        return bytes;
    }

    public static void AddPadding(byte[] bytes, int offset, int length, int seed = 31)
    {
        int written = 0;
        while (written < length)
        {
            int inBlock = (offset + written) % ReferenceDisc.SectorSize;
            int take = Math.Min(ReferenceDisc.SectorSize - inBlock, length - written);
            byte[] state = new byte[68];
            new Random(seed + (offset + written) / ReferenceDisc.SectorSize).NextBytes(state);
            ReferenceDisc.Padding(state, inBlock, take).CopyTo(bytes, offset + written);
            written += take;
        }
    }
}
