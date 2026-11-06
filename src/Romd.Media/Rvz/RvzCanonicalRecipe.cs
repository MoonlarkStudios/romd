namespace Romd.Media.Rvz;

/// <summary>The owner-approved, versioned recipe for canonical GameCube and Wii RVZ artifacts.</summary>
public static class RvzCanonicalRecipe
{
    public const string Id = "rvz-canonical/v1";
    public const uint Compression = RvzCodec.Zstd;
    public const int CompressionLevel = 19;
    public const int ChunkSize = 128 * 1024;

    /// <summary>Compares inspected container parameters, independent of producer or artifact identity.</summary>
    public static bool Matches(uint compression, int compressionLevel, int chunkSize) =>
        compression == Compression && compressionLevel == CompressionLevel && chunkSize == ChunkSize;
}
