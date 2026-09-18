using Romd.Domain.Hashing;

namespace Romd.Storage.Exceptions;

/// <summary>
///     Exception thrown when content integrity verification fails.
/// </summary>
public sealed class ContentCorruptedException : Exception
{
    public ContentCorruptedException(StorageKey key, Sha256 expected, Sha256 actual)
        : base($"Content corruption detected for {key}. Expected hash {expected}, got {actual}.")
    {
        Key = key;
        ExpectedHash = expected;
        ActualHash = actual;
    }

    public StorageKey Key { get; }
    public Sha256 ExpectedHash { get; }
    public Sha256 ActualHash { get; }
}
