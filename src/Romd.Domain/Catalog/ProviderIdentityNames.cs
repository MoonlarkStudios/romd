using System.Text.RegularExpressions;

namespace Romd.Domain.Catalog;

/// <summary>Conservative spelling equivalence; edition labels, numbers and + remain significant.</summary>
public static partial class ProviderIdentityNames
{
    public static string Normalize(string name)
    {
        var normalized = ArtworkIdentityMatcher.Normalize(name);
        // Move an indexed article rather than dropping it: "The Game" must not become "Game".
        if (normalized.EndsWith(", THE", StringComparison.Ordinal))
            normalized = "THE " + normalized[..^5];
        normalized = Conjunction().Replace(normalized, "AND");
        return Whitespace().Replace(Separators().Replace(normalized, " "), " ").Trim();
    }

    [GeneratedRegex(@"(?<!\S)&(?!\S)", RegexOptions.CultureInvariant)]
    private static partial Regex Conjunction();

    // Separators between words are typographic. Embedded hyphens and all other
    // punctuation stay intact rather than broadening identity to fuzzy similarity.
    [GeneratedRegex(@"\s+[-–—]\s+|\s*:\s*", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
