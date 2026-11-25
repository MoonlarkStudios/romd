namespace Romd.Dat;

internal static class HashNormalizer
{
    public static string? Crc32(string? value) => Normalize(value, 8);
    public static string? Md5(string? value) => Normalize(value, 32);
    public static string? Sha1(string? value) => Normalize(value, 40);

    public static (string? Crc, string? Md5, string? Sha1, bool HasMalformedChecksum) NormalizeFileChecksums(
        string? crc, string? md5, string? sha1)
    {
        string? normalizedCrc = Crc32(crc);
        string? normalizedMd5 = Md5(md5);
        string? normalizedSha1 = Sha1(sha1);
        bool malformed = (crc is not null && normalizedCrc is null)
            || (md5 is not null && normalizedMd5 is null)
            || (sha1 is not null && normalizedSha1 is null);
        return (normalizedCrc, normalizedMd5, normalizedSha1, malformed);
    }

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
