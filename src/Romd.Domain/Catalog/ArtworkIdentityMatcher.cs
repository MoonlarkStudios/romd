using System.Text;
using System.Text.RegularExpressions;

namespace Romd.Domain.Catalog;

public sealed record ArtworkIdentityCandidate(string Id, string Name, int? Year);
public sealed record ArtworkIdentityDecision(string? CandidateId, bool Trusted, string Evidence);

/// <summary>Conservative evidence rules, not probability estimates. Unknown years cannot corroborate a name.</summary>
public static partial class ArtworkIdentityMatcher
{
    public const string Version = "artwork-identity-1";

    public static ArtworkIdentityDecision Match(string name, int? year, IEnumerable<ArtworkIdentityCandidate> candidates)
    {
        var normalized = Normalize(name);
        var exact = candidates.DistinctBy(x => x.Id).Where(x => Normalize(x.Name) == normalized)
            .OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        if (exact.Length == 0)
            return new(null, false, "No exact identity-preserving name match.");
        if (exact.Length > 1)
            return new(null, false, "Multiple games share this name; choose the correct edition.");
        var candidate = exact[0];
        if (year is null || candidate.Year is null)
            return new(candidate.Id, false, "Exact name; release year is unknown. Review required.");
        if (year != candidate.Year)
            return new(candidate.Id, false, "Exact name but different release years. Review required.");
        return new(candidate.Id, true, "Unique exact name and matching release year.");
    }

    public static string Normalize(string name) => Whitespace().Replace(DumpAnnotation().Replace(
        name.Normalize(NormalizationForm.FormKC), " ").Trim(), " ").ToUpperInvariant();

    // Only known dump annotations are removed. Years, editions, sequel numbers,
    // remake/remaster labels, and unknown parenthesized words retain their meaning.
    [GeneratedRegex(@"\((?:USA|Europe|Japan|World|En(?:,Fr|,De|,Es|,It)*|Rev [A-Z0-9.]+)\)|\[(?:!|b[0-9]*|a[0-9]*)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DumpAnnotation();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
