using System.Text.RegularExpressions;
using Romd.Dat.Models;

namespace Romd.Dat;

public static partial class DatNameParser
{
    // Region heuristics — includes canonical names and common aliases from seed data
    private static readonly HashSet<string> Regions = new(StringComparer.OrdinalIgnoreCase)
    {
        "USA", "US", "U", "America", "United States", "NTSC-U",
        "Europe", "EU", "EUR", "PAL",
        "Japan", "JP", "J", "JPN", "NTSC-J",
        "World", "W", "WLD",
        "Germany", "DE", "D", "GER",
        "France", "FR", "FRA",
        "Spain", "ES", "SPA",
        "Italy", "IT", "ITA",
        "United Kingdom", "UK", "GB", "GBR", "Great Britain",
        "Australia", "AU", "AUS",
        "Canada", "CA", "CAN",
        "Korea", "KR", "K", "KOR", "South Korea",
        "China", "CN", "CHN", "PRC",
        "Brazil", "BR", "BRA",
        "Sweden", "SE", "SWE",
        "Netherlands", "NL", "NLD", "Holland",
        "Asia", "AS",
        "Taiwan", "TW", "TWN",
        "Hong Kong", "HK", "HKG",
        "Russia", "RU", "RUS"
    };

    // Revision patterns (Rev 1, Rev A, v1.0, Beta, Proto)
    // Changed [\d\.]+ to [\w\.]+ to support "Rev A"
    [GeneratedRegex(@"\((Rev\s*[\w\.]+|v[\d\.]+|Beta|Proto|Alpha|Demo|Sample)\)", RegexOptions.IgnoreCase)]
    private static partial Regex RevisionRegex();

    [GeneratedRegex(@"\(\s*(?:Alpha|Beta|Proto(?:type)?|Demo|Preview|Sample)(?=[\s\d.\-]|\))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonRetailQualifierRegex();

    /// <summary>Checks every parenthesized qualifier because an ordinary revision can follow a prerelease tag.</summary>
    public static bool HasNonRetailQualifier(string name) => NonRetailQualifierRegex().IsMatch(name);

    // Language patterns (En,Fr,De) or (English)
    [GeneratedRegex(@"\((([A-Z][a-z]{1,2}(-[A-Z]{2})?,?)+)\)")]
    private static partial Regex LanguageCodeRegex();

    // Tags [!] [b] etc
    [GeneratedRegex(@"\[(.*?)\]")]
    private static partial Regex TagRegex();

    // Parentheses content extractor
    [GeneratedRegex(@"\((.*?)\)")]
    private static partial Regex ParenthesesRegex();

    // Match only known language codes here. Parse's broader language heuristic is
    // useful for retaining annotations, but would strip labels such as (Pro).
    [GeneratedRegex(@"^(?:En|Ja|Fr|De|Es|It|Nl|Pt|Sv|No|Da|Fi|Ru|Ko|Zh)(?:,\s*(?:En|Ja|Fr|De|Es|It|Nl|Pt|Sv|No|Da|Fi|Ru|Ko|Zh))*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchLanguageCodes();

    [GeneratedRegex(@"\A(?:Alpha|Beta|Proto(?:type)?|Demo|Preview|Sample)(?:[ \t-]+[0-9]+(?:\.[0-9]+)*)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchNonRetailAnnotation();

    /// <summary>Removes known trailing release annotations for game-level lookups.</summary>
    public static string StripReleaseAnnotations(string name, bool includeNonRetail = false)
    {
        var title = name.TrimEnd();
        while (title.EndsWith(')'))
        {
            var start = title.LastIndexOf('(');
            if (start < 2 || !char.IsWhiteSpace(title[start - 1]))
                break;

            var qualifier = title[(start + 1)..^1];
            var tag = title[start..];
            if (!IsReleaseAnnotation(qualifier, tag) && !(includeNonRetail && SearchNonRetailAnnotation().IsMatch(qualifier)))
                break;

            title = title[..start].TrimEnd();
        }

        return title;
    }

    private static bool IsReleaseAnnotation(string qualifier, string tag) =>
        qualifier.Split(',', StringSplitOptions.TrimEntries).All(Regions.Contains) ||
        SearchLanguageCodes().IsMatch(qualifier) ||
        (RevisionRegex().IsMatch(tag) && !HasNonRetailQualifier(tag)) ||
        qualifier.Equals("Virtual Console", StringComparison.OrdinalIgnoreCase) ||
        qualifier.Equals("Switch Online", StringComparison.OrdinalIgnoreCase);

    public static ParsedNameMetadata Parse(string name, string? explicitCategory = null)
    {
        var regions = new List<string>();
        var languages = new List<string>();
        var revisions = new List<string>();
        bool isVerified = false;
        string? category = explicitCategory;

        // 1. Check for [BIOS] prefix (Common in No-Intro) if category is missing
        if (string.IsNullOrEmpty(category))
        {
            if (name.StartsWith("[BIOS]", StringComparison.OrdinalIgnoreCase))
            {
                category = "BIOS";
            }
        }

        // 2. Parse Bracket Tags [!]
        foreach (Match match in TagRegex().Matches(name))
        {
            string tag = match.Groups[1].Value;
            if (tag == "!")
            {
                isVerified = true;
            }
        }

        // 3. Parse Parentheses (Region) (Lang) (Rev)
        foreach (Match match in ParenthesesRegex().Matches(name))
        {
            string content = match.Groups[1].Value;

            // Is it a Revision?
            if (RevisionRegex().IsMatch(match.Value))
            {
                revisions.Add(content);
                continue;
            }

            // Is it Language codes? (En,Fr,De) — check before regions to avoid
            // ambiguity with 2-letter codes like FR/DE/ES/IT that are both region
            // aliases and language code abbreviations.
            if (LanguageCodeRegex().IsMatch(match.Value))
            {
                languages.Add(content);
                continue;
            }

            // Is it a Region? Split on comma only to preserve multi-word names
            // like "United Kingdom" and "Hong Kong".
            string[] parts = content.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Any(p => Regions.Contains(p)))
            {
                regions.Add(content);
            }
        }

        return new ParsedNameMetadata
        {
            Region = regions.Count > 0 ? string.Join(", ", regions) : null,
            Language = languages.Count > 0 ? string.Join(", ", languages) : null,
            Revision = revisions.Count > 0 ? revisions.Last() : null,
            Category = category ?? "Game",
            IsVerified = isVerified
        };
    }
}
