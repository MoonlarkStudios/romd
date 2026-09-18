using Sqids;

namespace Romd.Application.Common.Ids;

/// <summary>
///     Provides encoding and decoding of internal integer IDs to URL-safe friendly strings.
///     Uses Sqids to generate short, unique, non-sequential identifiers.
/// </summary>
public static class IdCoder
{
    // Alphabet without easily confused characters: 0, O, 1, I, l removed
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private static readonly SqidsEncoder<int> Encoder = new(new SqidsOptions
    {
        Alphabet = Alphabet,
        MinLength = 6
    });

    /// <summary>
    ///     Encodes an integer ID to a URL-safe friendly string.
    /// </summary>
    public static string Encode(int id) => Encoder.Encode(id);

    /// <summary>
    ///     Attempts to decode a friendly string ID back to its integer value.
    ///     Returns true if successful, false otherwise.
    /// </summary>
    public static bool TryDecode(string? id, out int result)
    {
        result = 0;

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        // Validate all characters are from our alphabet before attempting decode
        foreach (char c in id)
        {
            if (!Alphabet.Contains(c))
            {
                return false;
            }
        }

        try
        {
            var decoded = Encoder.Decode(id);
            if (decoded.Count != 1 || decoded[0] <= 0)
            {
                return false;
            }

            result = decoded[0];
            return true;
        }
        catch
        {
            return false;
        }
    }

}
