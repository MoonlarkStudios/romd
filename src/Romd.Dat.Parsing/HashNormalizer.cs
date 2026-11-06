namespace Romd.Dat.Parsing;

internal static class HashNormalizer
{
    public static string? Crc32(string? value) => Normalize(value, 8);
    public static string? Md5(string? value) => Normalize(value, 32);
    public static string? Sha1(string? value) => Normalize(value, 40);

    private static string? Normalize(string? value, int expectedLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != expectedLength)
        {
            return null;
        }

        foreach (char character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return null;
            }
        }

        return value.ToLowerInvariant();
    }
}
