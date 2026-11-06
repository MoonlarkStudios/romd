using Romd.Media.Rvz;
using Romd.Media.TestSupport;

namespace Romd.Media.Tests;

/// <summary>Small synthetic discs exercising each RVZ branch; expected bytes come from the reference model.</summary>
internal static class Discs
{
    public const long WiiPartitionFirstSector = 10;
    public const int WiiPartitionSectors = 130; // two full 2 MiB regions and a partial third

    public static SyntheticRvz GameCube(int chunkSize = 0x20000, RvzMethod method = RvzMethod.Zstd, Func<int, bool>? uncompressed = null)
    {
        var builder = new SyntheticRvzBuilder(0x300000, chunkSize, method, wii: false, seed: 3)
        {
            StoreUncompressed = uncompressed ?? (_ => false)
        };
        builder.Fill(0x100000, new byte[0x40000]); // whole zero chunks
        builder.Padding(0x10000 + 5, 2000, Seed(1));
        builder.Padding(0x18000, 0x8000, Seed(2)); // a full 32 KiB block
        builder.Padding(0x2A0000 + 0x7000, 0x1000, Seed(3)); // ends exactly at a block boundary
        return builder.Build();
    }

    public static SyntheticRvz Wii(int chunkSize = 0x20000, RvzMethod method = RvzMethod.Zstd, Func<int, bool>? uncompressed = null)
    {
        var builder = new SyntheticRvzBuilder(0x480000, chunkSize, method, wii: true, seed: 5)
        {
            StoreUncompressed = uncompressed ?? (_ => false)
        };
        builder.Padding(0x10000 + 5, 2000, Seed(4));
        builder.Padding(0x468000 + 16, 3000, Seed(5));
        byte[] plain = new byte[WiiPartitionSectors * ReferenceDisc.SectorData];
        new Random(9).NextBytes(plain);
        Array.Clear(plain, 64 * ReferenceDisc.SectorData, 8 * ReferenceDisc.SectorData); // sectors 64-71 are zero
        HashOverride[] overrides =
        [
            new(3, 0x000, Hash(1)),    // an H0 hash
            new(70, 0x26C, Hash(2)),   // H0 padding
            new(100, 0x2A8, Hash(3)),  // an H1 hash
            new(129, 0x3EC, Hash(4))   // the last 20 bytes of a hash block in the partial region
        ];
        PaddingRun[] padding = [new(3L * 0x8000 + 10, 1000, Seed(6)), new(90L * ReferenceDisc.SectorData + 64, 500, Seed(7))];
        builder.Partition(WiiPartitionFirstSector, WiiPartitionSectors, Key(), plain, firstSegmentSectors: 64,
            overrides: overrides, padding: padding);
        return builder.Build();
    }

    public static async Task<byte[]> ReadAllAsync(RvzImage image, int bufferSize = 1 << 20)
    {
        var output = new byte[image.Length];
        long position = 0;
        while (position < output.Length)
        {
            int read = await image.ReadAtAsync(position, output.AsMemory((int)position, (int)Math.Min(bufferSize, output.Length - position)));
            if (read <= 0)
                throw new InvalidDataException($"Read stopped at {position}.");
            position += read;
        }
        return output;
    }

    public static Task<RvzImage> OpenAsync(byte[] file, MediaBlockCache? cache = null, string? identity = null, RvzReadOptions? options = null) =>
        RvzImage.OpenAsync(new MemoryContent(file), options ?? new RvzReadOptions(), cache ?? new MediaBlockCache(16L << 20), identity);

    private static byte[] Seed(int value) => Deterministic(value, 68);
    private static byte[] Hash(int value) => Deterministic(100 + value, 20);
    private static byte[] Key() => Deterministic(42, 16);

    private static byte[] Deterministic(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
