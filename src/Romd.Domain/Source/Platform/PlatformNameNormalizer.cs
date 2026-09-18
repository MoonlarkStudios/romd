namespace Romd.Domain.Source.Platform;

/// <summary>
///     Canonical normalization for platform name matching keys.
///     Used both when storing name aliases and when matching DAT header names,
///     so the two sides can never drift apart.
/// </summary>
public static class PlatformNameNormalizer
{
    /// <summary>
    ///     Lowercases, trims, and collapses all internal whitespace runs to single spaces.
    /// </summary>
    public static string Normalize(string value) =>
        string.Join(' ', value.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
