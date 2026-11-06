using System.Text.RegularExpressions;

namespace Romd.Domain.Source.Platform;

/// <summary>
///     Pure matching logic that routes a DAT header name to a platform.
///     Derives progressively looser candidates from the header and returns the
///     first one present in the supplied lookup. Matching is deterministic and
///     exact per candidate — no fuzzy scoring — because mis-routing a DAT is
///     worse than leaving it unrouted.
/// </summary>
public static partial class DatHeaderPlatformMatcher
{
    /// <summary>
    ///     Matches a DAT header name against a lookup of normalized platform keys
    ///     (names, short names, and name aliases — see <see cref="PlatformNameNormalizer" />).
    ///     Ambiguous keys must be excluded from the lookup by the caller.
    /// </summary>
    /// <returns>The matched platform ID, or null when no candidate matches.</returns>
    public static int? Match(string headerName, IReadOnlyDictionary<string, int> normalizedLookup)
    {
        if (string.IsNullOrWhiteSpace(headerName) || normalizedLookup.Count == 0)
        {
            return null;
        }

        foreach (var candidate in EnumerateCandidates(headerName))
        {
            if (normalizedLookup.TryGetValue(candidate, out var platformId))
            {
                return platformId;
            }
        }

        return null;
    }

    /// <summary>
    ///     Yields normalized candidates in decreasing order of specificity:
    ///     1. The full header name ("Nintendo - Super Nintendo Entertainment System (Parent-Clone)").
    ///     2. The header with parenthetical/bracketed groups stripped.
    ///     3. Suffixes of the stripped header, dropping leading " - " segments one at a
    ///        time ("Super Nintendo Entertainment System"; "Mega Drive - Genesis" → "Genesis").
    ///     4. Individual segments after the first — the leading segment is a vendor
    ///        prefix in No-Intro/Redump conventions and never tried alone.
    /// </summary>
    internal static IEnumerable<string> EnumerateCandidates(string headerName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var full = PlatformNameNormalizer.Normalize(headerName);
        if (full.Length > 0 && seen.Add(full))
        {
            yield return full;
        }

        var stripped = PlatformNameNormalizer.Normalize(GroupedSuffixRegex().Replace(headerName, " "));
        if (stripped.Length > 0 && seen.Add(stripped))
        {
            yield return stripped;
        }

        if (stripped.Length == 0)
        {
            yield break;
        }

        var segments = stripped.Split(" - ", StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            yield break;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            var suffix = string.Join(" - ", segments[i..]);
            if (seen.Add(suffix))
            {
                yield return suffix;
            }
        }

        foreach (var segment in segments[1..])
        {
            if (seen.Add(segment))
            {
                yield return segment;
            }
        }
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex GroupedSuffixRegex();
}
