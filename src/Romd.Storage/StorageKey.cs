using Romd.Domain.Hashing;

namespace Romd.Storage;

public readonly struct StorageKey : IEquatable<StorageKey>
{
    private readonly Sha256 _hash;

    private StorageKey(Sha256 hash)
    {
        _hash = hash;
    }

    public Sha256 Hash => _hash;
    public bool IsEmpty => _hash.IsEmpty;

    public static StorageKey FromHash(Sha256 hash)
    {
        if (hash.IsEmpty)
        {
            throw new ArgumentException("Cannot create storage key from empty hash.", nameof(hash));
        }

        return new StorageKey(hash);
    }

    public bool Equals(StorageKey other) => _hash.Equals(other._hash);
    public override bool Equals(object? obj) => obj is StorageKey other && Equals(other);
    public override int GetHashCode() => _hash.GetHashCode();
    public override string ToString() => _hash.ToString(); // Just the hex, no path

    public static bool operator ==(StorageKey left, StorageKey right) => left.Equals(right);
    public static bool operator !=(StorageKey left, StorageKey right) => !left.Equals(right);
}
