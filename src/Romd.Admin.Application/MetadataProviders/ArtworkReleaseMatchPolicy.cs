using Romd.Domain.Catalog;

namespace Romd.Admin.Application.MetadataProviders;

internal static class ArtworkReleaseMatchPolicy
{
    internal const string Version = "steamgriddb-title-year-6";

    internal static IReadOnlyList<string> FindAlternateTitles(IdentityFacts facts, ProviderGame trustedGame,
        IReadOnlyList<int> platformYears, IEnumerable<ProviderGame> primaryResults)
    {
        var years = (facts.Years.Count == 0 ? platformYears : platformYears.Intersect(facts.Years)).ToHashSet();
        var knownNames = facts.Names.Append(facts.Name).Append(trustedGame.Name).Select(ProviderIdentityNames.Normalize)
            .ToHashSet(StringComparer.Ordinal);
        // Leave existing dated matches and plausible undated records to normal adjudication.
        if (years.Count == 0 || primaryResults.Any(game =>
            (game.Year is null || years.Contains(game.Year.Value)) && knownNames.Contains(ProviderIdentityNames.Normalize(game.Name))))
            return [];

        return (trustedGame.Aliases ?? [])
            .Where(name => name.Length is > 0 and <= 200 && !name.Any(char.IsControl))
            .Order(StringComparer.Ordinal)
            .DistinctBy(ProviderIdentityNames.Normalize, StringComparer.Ordinal)
            .Where(name =>
            {
                var normalized = ProviderIdentityNames.Normalize(name);
                // Exclude single-token shortcuts, such as FFVI, from automatic artwork evidence.
                return normalized.Contains(' ') && !knownNames.Contains(normalized);
            })
            .ToArray();
    }

    internal static ProviderIdentityCandidate? Select(IdentityFacts facts,
        IReadOnlyList<ProviderIdentityCandidate> candidates, IReadOnlyList<int> trustedPlatformYears, int? trustedFirstReleaseYear,
        IReadOnlyList<string> corroboratingTitles)
    {
        var years = facts.Years.Count == 0 ? trustedPlatformYears :
            trustedPlatformYears.Count == 0 ? facts.Years : facts.Years.Intersect(trustedPlatformYears).ToArray();
        // A platform can have several dated releases. Use the original year when that same platform corroborates it.
        var year = years.Count == 1 ? years[0] :
            facts.Years.Count == 0 && trustedFirstReleaseYear is { } first && years.Contains(first) ? first : (int?)null;
        if (year is null)
            return null;

        var names = facts.Names.Prepend(facts.Name).Select(ProviderIdentityNames.Normalize).ToHashSet(StringComparer.Ordinal);
        var corroboratingNames = corroboratingTitles.Select(ProviderIdentityNames.Normalize).ToHashSet(StringComparer.Ordinal);
        // An undated exact-name record could describe the same release. Other dated editions can be ruled out.
        var contenders = candidates.Where(candidate => candidate is { Eligible: true, Conflicts.Count: 0 } &&
            (candidate.Game.Year is null || candidate.Game.Year == year))
            .Select(candidate => (Candidate: candidate, Name: ProviderIdentityNames.Normalize(candidate.Game.Name)))
            .Where(contender => names.Contains(contender.Name) || corroboratingNames.Contains(contender.Name)).Take(2).ToArray();
        // A trusted provider title can surface an alternative. Automatic trust still requires a local name match.
        return contenders.Length == 1 && contenders[0].Candidate.Game.Year is not null && names.Contains(contenders[0].Name)
            ? contenders[0].Candidate : null;
    }
}
