using System.Text;
using System.Text.RegularExpressions;

namespace Romd.Admin.Application.Titles.Matching;

/// <summary>
///     Normalizes game names for title matching.
///     Strips region tags, revision info, articles, and special characters to produce
///     a canonical form for matching across DAT naming conventions (No-Intro, TOSEC, Redump).
/// </summary>
public static partial class TitleNormalizer
{
    // Common articles that appear at the start or end of titles
    private static readonly string[] LeadingArticles = ["the ", "a ", "an "];
    private static readonly string[] TrailingArticles = [", the", ", a", ", an"];

    /// <summary>
    ///     Creates a display-friendly name by stripping region/revision tags
    ///     while preserving human-readable formatting (case, spaces, punctuation).
    /// </summary>
    /// <example>
    ///     "Aladdin (Europe)" → "Aladdin"
    ///     "Super Mario World (USA) (Rev 1)" → "Super Mario World"
    ///     "Legend of Zelda, The (Europe)" → "Legend of Zelda, The"
    ///     "Sonic the Hedgehog 2 [!]" → "Sonic the Hedgehog 2"
    /// </example>
    public static string ToDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var result = name;

        // Remove parenthetical content (regions, revisions, etc.)
        result = ParenthesesRegex().Replace(result, string.Empty);

        // Remove bracketed content (status tags)
        result = BracketsRegex().Replace(result, string.Empty);

        // Clean up extra whitespace and trim
        result = MultipleSpacesRegex().Replace(result.Trim(), " ");

        return result;
    }

    /// <summary>
    ///     Normalizes a game name for matching.
    /// </summary>
    /// <example>
    ///     "Super Mario World (USA) (Rev 1)" → "supermarioworld"
    ///     "Legend of Zelda, The (Europe)" → "legendofzelda"
    ///     "The Legend of Zelda (USA)" → "legendofzelda"
    ///     "Sonic the Hedgehog 2 [!]" → "sonicthehedgehog2"
    /// </example>
    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var result = name;

        // Remove parenthetical content (regions, revisions, etc.)
        // Examples: (USA), (Europe), (Rev 1), (v1.0), (Proto), (Beta)
        result = ParenthesesRegex().Replace(result, string.Empty);

        // Remove bracketed content (status tags, etc.)
        // Examples: [!], [a], [b], [h]
        result = BracketsRegex().Replace(result, string.Empty);

        // Convert to lowercase and trim
        result = result.ToLowerInvariant().Trim();

        // Remove trailing articles (TOSEC/Redump style: "Legend of Zelda, The")
        foreach (var article in TrailingArticles)
        {
            if (result.EndsWith(article, StringComparison.Ordinal))
            {
                result = result[..^article.Length];
                break;
            }
        }

        // Remove leading articles (No-Intro style: "The Legend of Zelda")
        foreach (var article in LeadingArticles)
        {
            if (result.StartsWith(article, StringComparison.Ordinal))
            {
                result = result[article.Length..];
                break;
            }
        }

        // Remove all non-alphanumeric characters
        var sb = new StringBuilder(result.Length);
        foreach (var c in result)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Creates a search-friendly name for external API queries (e.g., IGDB).
    ///     Fixes trailing articles, strips subtitle separators, normalizes ampersands,
    ///     and removes parenthetical/bracketed content while preserving readable formatting.
    /// </summary>
    /// <example>
    ///     "Legend of Zelda, The (Europe)" → "The Legend of Zelda"
    ///     "Super Mario World - Super Mario Bros. 4 (USA)" → "Super Mario World Super Mario Bros. 4"
    ///     "Toejam &amp; Earl (USA)" → "Toejam and Earl"
    /// </example>
    public static string ToSearchName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var result = name;

        // Remove parenthetical content (regions, revisions, etc.)
        result = ParenthesesRegex().Replace(result, string.Empty);

        // Remove bracketed content (status tags)
        result = BracketsRegex().Replace(result, string.Empty);

        // Clean up extra whitespace and trim
        result = MultipleSpacesRegex().Replace(result.Trim(), " ");

        // Fix trailing articles: "Legend of Zelda, The" → "The Legend of Zelda"
        foreach (var article in TrailingArticles)
        {
            if (result.EndsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                var articleWord = result[(result.Length - article.Length + 2)..]; // skip ", "
                result = articleWord + " " + result[..^article.Length];
                break;
            }
        }

        // Normalize subtitle separators: " - " → " " (DAT convention vs IGDB's ": ")
        result = SubtitleSeparatorRegex().Replace(result, " ");

        // Normalize ampersands: " & " → " and "
        result = result.Replace(" & ", " and ");

        // Clean up any resulting extra whitespace
        result = MultipleSpacesRegex().Replace(result.Trim(), " ");

        return result;
    }

    [GeneratedRegex(@"\([^)]*\)", RegexOptions.Compiled)]
    private static partial Regex ParenthesesRegex();

    [GeneratedRegex(@"\[[^\]]*\]", RegexOptions.Compiled)]
    private static partial Regex BracketsRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.Compiled)]
    private static partial Regex MultipleSpacesRegex();

    [GeneratedRegex(@"\s+-\s+", RegexOptions.Compiled)]
    private static partial Regex SubtitleSeparatorRegex();
}
