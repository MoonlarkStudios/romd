namespace Romd.Storage;

/// <summary>
///     Metadata about stored content.
/// </summary>
public readonly record struct ContentInfo(
    StorageKey Key,
    long CompressedSize,
    DateTimeOffset CreatedAt,
    bool IsCompressed,
    ContentStorageFormat Format = ContentStorageFormat.Unknown)
{
    /// <summary>Physical bytes on disk for either raw or compressed content.</summary>
    public long StoredSize => CompressedSize;
}
