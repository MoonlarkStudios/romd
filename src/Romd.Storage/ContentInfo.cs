namespace Romd.Storage;

/// <summary>
///     Metadata about stored content.
/// </summary>
public readonly record struct ContentInfo(
    StorageKey Key,
    long CompressedSize,
    DateTimeOffset CreatedAt,
    bool IsCompressed);
