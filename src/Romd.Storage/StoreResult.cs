namespace Romd.Storage;

/// <summary>
///     Result of a store operation.
/// </summary>
public readonly record struct StoreResult(
    StorageKey Key,
    long Size,
    long CompressedSize,
    bool WasDeduplicated,
    bool IsCompressed)
{
    /// <summary>
    ///     Compression ratio (compressed / uncompressed). Lower is better.
    ///     Returns 1.0 if size is zero or content is not compressed.
    /// </summary>
    public double CompressionRatio => Size > 0 && IsCompressed ? CompressedSize / (double)Size : 1.0;

    /// <summary>
    ///     Bytes saved by compression. Zero if not compressed.
    ///     Negative if compression increased size (should not happen with skip-compression logic).
    /// </summary>
    public long BytesSaved => IsCompressed ? Size - CompressedSize : 0;
}
