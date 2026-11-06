using System.Buffers.Binary;

namespace Romd.Media.TestSupport;

/// <summary>Small, independently specified disc headers and extents; no encryption or common keys.</summary>
public static class SyntheticNintendoIso
{
    public const int GameCubeDolOffset = 0x3000, GameCubeFstOffset = 0x4000, GameCubeFileOffset = 0x5000;
    public const int WiiDirectoryOffset = 0x40000, WiiTableOffset = 0x40020, WiiPartitionOffset = 0x50000;
    public const int WiiPartitionStride = 0x30000, WiiDataRelative = 0x20000, WiiDataBytes = 0x8000;

    public static byte[] GameCube()
    {
        var disc = new byte[0x6000];
        U32(disc, 0x1C, 0xC2339F3D);
        U32(disc, 0x420, GameCubeDolOffset);
        U32(disc, 0x424, GameCubeFstOffset);
        U32(disc, 0x428, 32);
        U32(disc, 0x42C, 32);
        U32(disc, 0x2440 + 0x14, 0x100);
        U32(disc, GameCubeDolOffset, 0x100);
        U32(disc, GameCubeDolOffset + 0x90, 0x100);
        disc[GameCubeFstOffset] = 1;
        U32(disc, GameCubeFstOffset + 8, 2);
        U32(disc, GameCubeFstOffset + 12, 1);
        U32(disc, GameCubeFstOffset + 16, GameCubeFileOffset);
        U32(disc, GameCubeFstOffset + 20, 0x1000);
        "a.bin\0"u8.CopyTo(disc.AsSpan(GameCubeFstOffset + 25));
        return disc;
    }

    public static byte[] Wii(params byte[] commonKeyIndices)
    {
        if (commonKeyIndices.Length > 64)
            throw new ArgumentOutOfRangeException(nameof(commonKeyIndices));
        int length = commonKeyIndices.Length == 0 ? WiiPartitionOffset :
            WiiPartitionOffset + (commonKeyIndices.Length - 1) * WiiPartitionStride + WiiDataRelative + WiiDataBytes;
        var disc = new byte[length];
        U32(disc, 0x18, 0x5D1C9EA3);
        U32(disc, WiiDirectoryOffset, (uint)commonKeyIndices.Length);
        U32(disc, WiiDirectoryOffset + 4, WiiTableOffset / 4);
        for (int index = 0; index < commonKeyIndices.Length; index++)
        {
            int partition = WiiPartitionOffset + index * WiiPartitionStride;
            U32(disc, WiiTableOffset + index * 8, (uint)partition / 4);
            U32(disc, WiiTableOffset + index * 8 + 4, index == 0 ? 0U : 1U);
            U32(disc, partition, 0x00010001);
            "Root-CA00000001-XS00000003\0"u8.CopyTo(disc.AsSpan(partition + 0x140));
            disc[partition + 0x1DC] = 1;
            disc[partition + 0x1F1] = commonKeyIndices[index];
            U32(disc, partition + 0x2A4, 0x208);
            U32(disc, partition + 0x2A8, 0x2C0 / 4);
            U32(disc, partition + 0x2AC, 0xA00);
            U32(disc, partition + 0x2B0, 0x4E0 / 4);
            U32(disc, partition + 0x2B4, 0x8000 / 4);
            U32(disc, partition + 0x2B8, WiiDataRelative / 4);
            U32(disc, partition + 0x2BC, WiiDataBytes / 4);
        }
        return disc;
    }

    public static void U32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
}
