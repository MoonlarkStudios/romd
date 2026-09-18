using System.Text.RegularExpressions;
using Romd.Dat.Parsing.Models;

namespace Romd.Dat.Parsing;

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

    // Language patterns (En,Fr,De) or (English)
    [GeneratedRegex(@"\((([A-Z][a-z]{1,2}(-[A-Z]{2})?,?)+)\)")]
    private static partial Regex LanguageCodeRegex();

    // Tags [!] [b] etc
    [GeneratedRegex(@"\[(.*?)\]")]
    private static partial Regex TagRegex();

    // Parentheses content extractor
    [GeneratedRegex(@"\((.*?)\)")]
    private static partial Regex ParenthesesRegex();

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
