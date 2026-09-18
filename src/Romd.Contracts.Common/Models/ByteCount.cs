using System.Globalization;
using System.Text.Json.Serialization;
using Romd.Contracts.Common.Serialization;

namespace Romd.Contracts.Common.Models;

/// <summary>
///     A nonnegative byte size or byte aggregate on a public contract. Serialized as a canonical
///     decimal JSON string because byte counts have no JavaScript-safe upper bound.
/// </summary>
[JsonConverter(typeof(ByteCountJsonConverter))]
public readonly record struct ByteCount
{
    public ByteCount(long value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "A byte count cannot be negative.");

        Value = value;
    }

    public long Value { get; }

    /// <summary>
    ///     Explicit conversion from long: constructing a ByteCount asserts the value counts bytes
    ///     and is nonnegative.
    /// </summary>
    public static explicit operator ByteCount(long value) => new(value);

    /// <summary>
    ///     Implicit conversion to long for seamless arithmetic over byte counts.
    /// </summary>
    public static implicit operator long(ByteCount byteCount) => byteCount.Value;

    /// <summary>
    ///     Returns the canonical decimal string carried on the wire.
    /// </summary>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
