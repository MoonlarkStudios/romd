using System.Text.RegularExpressions;

namespace Romd.Domain.Catalog;

/// <summary>
///     Input for match confidence calculation.
/// </summary>
public sealed record MatchContext
{
    public required string SearchName { get; init; }
    public string? ResultName { get; init; }
    public bool YearMatched { get; init; }
    public bool HashMatched { get; init; }
}

/// <summary>
///     Calculates match confidence between a title and a provider result.
///     Uses multiple signals: name similarity, year match, hash match.
/// </summary>
public sealed partial class MatchConfidenceCalculator
{
    [GeneratedRegex(@"[\(\[\{].*?[\)\]\}]")]
    private static partial Regex BracketedContentRegex();

    [GeneratedRegex(@"[^a-z0-9\s]")]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    ///     Calculates the match confidence for a provider result.
    /// </summary>
    /// <returns>Confidence score from 0.0 to 1.0.</returns>
    public float Calculate(MatchContext match)
    {
        // Hash match is near-certain regardless of other signals
        if (match.HashMatched)
            return Math.Min(1.0f, 0.95f + (match.YearMatched ? 0.05f : 0f));

        // Name similarity is the primary signal (0.0–0.85)
        float nameSimilarity = match.ResultName != null
            ? ComputeNameSimilarity(match.SearchName, match.ResultName)
            : 0f;

        float confidence = nameSimilarity * 0.85f;

        // Year match is a genuine independent confirmation (+0.1)
        if (match.YearMatched)
            confidence += 0.1f;

        // Exact name match bonus (+0.05) — rewards perfect alignment
        if (nameSimilarity > 0.99f)
            confidence += 0.05f;

        return Math.Min(1.0f, confidence);
    }

    private static float ComputeNameSimilarity(string searchName, string resultName)
    {
        var normalizedSearch = NormalizeName(searchName);
        var normalizedResult = NormalizeName(resultName);

        if (string.IsNullOrWhiteSpace(normalizedSearch) ||
            string.IsNullOrWhiteSpace(normalizedResult))
            return 0f;

        // Exact match after normalization
        if (normalizedSearch == normalizedResult)
            return 1.0f;

        // Jaro-Winkler for fuzzy matching.
        var similarity = ComputeJaroWinklerSimilarity(normalizedSearch, normalizedResult);

        // Containment check: if one fully contains the other, boost slightly.
        // Handles "Zelda" matching "The Legend of Zelda A Link to the Past"
        if (normalizedResult.Contains(normalizedSearch) ||
            normalizedSearch.Contains(normalizedResult))
        {
            float lengthRatio = (float)Math.Min(normalizedSearch.Length, normalizedResult.Length)
                / Math.Max(normalizedSearch.Length, normalizedResult.Length);
            similarity = Math.Max(similarity, 0.6f + lengthRatio * 0.3f);
        }

        return similarity;
    }

    private static float ComputeJaroWinklerSimilarity(string first, string second)
    {
        if (first == second)
            return 1.0f;

        if (first.Length == 0 || second.Length == 0)
            return 0f;

        int matchDistance = Math.Max(first.Length, second.Length) / 2 - 1;
        matchDistance = Math.Max(0, matchDistance);

        var firstMatches = new bool[first.Length];
        var secondMatches = new bool[second.Length];

        int matchCount = 0;
        for (int i = 0; i < first.Length; i++)
        {
            int start = Math.Max(0, i - matchDistance);
            int end = Math.Min(i + matchDistance + 1, second.Length);

            for (int j = start; j < end; j++)
            {
                if (secondMatches[j] || first[i] != second[j])
                    continue;

                firstMatches[i] = true;
                secondMatches[j] = true;
                matchCount++;
                break;
            }
        }

        if (matchCount == 0)
            return 0f;

        int transpositions = 0;
        int secondIndex = 0;
        for (int i = 0; i < first.Length; i++)
        {
            if (!firstMatches[i])
                continue;

            while (!secondMatches[secondIndex])
                secondIndex++;

            if (first[i] != second[secondIndex])
                transpositions++;

            secondIndex++;
        }

        float matches = matchCount;
        float jaro = (matches / first.Length
                      + matches / second.Length
                      + (matches - transpositions / 2f) / matches) / 3f;

        int prefixLength = 0;
        int maxPrefixLength = Math.Min(4, Math.Min(first.Length, second.Length));
        while (prefixLength < maxPrefixLength && first[prefixLength] == second[prefixLength])
            prefixLength++;

        return jaro + prefixLength * 0.1f * (1f - jaro);
    }

    private static string NormalizeName(string name)
    {
        // Lowercase, remove common suffixes like (USA), [!], etc.
        var normalized = name.ToLowerInvariant();

        // Remove content in parentheses and brackets
        var result = BracketedContentRegex().Replace(normalized, "");

        // Remove non-alphanumeric (keep spaces for word matching)
        result = NonAlphanumericRegex().Replace(result, "");

        // Collapse whitespace
        result = WhitespaceRegex().Replace(result, " ").Trim();

        return result;
    }
}
