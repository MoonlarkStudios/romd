using System.Diagnostics.CodeAnalysis;

namespace Romd.Domain.Hashing;

/// <summary>
///     Represents an immutable cryptographic hash value.
/// </summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
public interface IHashValue<TSelf> : IEquatable<TSelf>, IComparable<TSelf>
    where TSelf : struct, IHashValue<TSelf>
{
    /// <summary>
    ///     The fixed length in bytes for this hash type.
    /// </summary>
    static abstract int ByteLength { get; }

    /// <summary>
    ///     True if this hash is empty (uninitialized or all zeros for crypto hashes).
    /// </summary>
    bool IsEmpty { get; }

    /// <summary>
    ///     Creates a hash value from raw bytes.
    /// </summary>
    /// <exception cref="ArgumentException">Incorrect byte length.</exception>
    static abstract TSelf FromSpan(ReadOnlySpan<byte> data);

    /// <summary>
    ///     Returns the hash bytes as a read-only span.
    /// </summary>
    [UnscopedRef]
    ReadOnlySpan<byte> AsSpan();
}
