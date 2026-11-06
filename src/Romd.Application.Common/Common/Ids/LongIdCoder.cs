using Sqids;

namespace Romd.Application.Common.Ids;

/// <summary>Opaque positive Int64 identity. The existing Int32 identity codec is unchanged.</summary>
public static class LongIdCoder
{
    private static readonly SqidsEncoder<long> Encoder = new(new SqidsOptions
    {
        Alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789",
        MinLength = 6
    });
    public static string Encode(long id) => Encoder.Encode(id);
}
