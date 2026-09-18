using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Romd.Domain.Hashing;

namespace Romd.Persistence.Converters;

public sealed class HashValueConverter<T> : ValueConverter<T, byte[]>
    where T : struct, IHashValue<T>
{
    public HashValueConverter() : base(
        v => ToBytes(v),
        v => FromBytes(v))
    {
    }

    private static byte[] ToBytes(T value)
    {
        var span = value.AsSpan();
        return span.IsEmpty ? new byte[T.ByteLength] : span.ToArray();
    }

    private static T FromBytes(byte[] bytes) => T.FromSpan(bytes);
}

public sealed class NullableHashValueConverter<T> : ValueConverter<T?, byte[]?>
    where T : struct, IHashValue<T>
{
    public NullableHashValueConverter() : base(
        v => ToBytes(v),
        v => FromBytes(v))
    {
    }

    private static byte[]? ToBytes(T? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        var span = value.Value.AsSpan();
        return span.IsEmpty ? new byte[T.ByteLength] : span.ToArray();
    }

    private static T? FromBytes(byte[]? bytes)
        => bytes is not null ? T.FromSpan(bytes) : null;
}
