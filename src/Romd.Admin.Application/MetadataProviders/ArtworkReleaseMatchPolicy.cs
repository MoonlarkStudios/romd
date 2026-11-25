using Romd.Domain.Catalog;

namespace Romd.Admin.Application.MetadataProviders;

internal static class ArtworkReleaseMatchPolicy
{
    internal const string Version = "steamgriddb-title-year-2";

    internal static ProviderIdentityCandidate? Select(IdentityFacts facts,
        IReadOnlyList<ProviderIdentityCandidate> candidates, IReadOnlyList<int> trustedPlatformYears, int? trustedFirstReleaseYear)
    {
        var years = facts.Years.Count == 0 ? trustedPlatformYears :
            trustedPlatformYears.Count == 0 ? facts.Years : facts.Years.Intersect(trustedPlatformYears).ToArray();
        // A platform can have several dated releases. Use the original year when that same platform corroborates it.
        var year = years.Count == 1 ? years[0] :
            facts.Years.Count == 0 && trustedFirstReleaseYear is { } first && years.Contains(first) ? first : (int?)null;
        if (year is null)
            return null;

        var names = facts.Names.Prepend(facts.Name).Select(ProviderIdentityNames.Normalize).ToHashSet(StringComparer.Ordinal);
        // An undated exact-name record could describe the same release. Other dated editions can be ruled out.
        var contenders = candidates.Where(candidate => candidate is { Eligible: true, Conflicts.Count: 0 } &&
            names.Contains(ProviderIdentityNames.Normalize(candidate.Game.Name)) &&
            (candidate.Game.Year is null || candidate.Game.Year == year)).Take(2).ToArray();
        return contenders.Length == 1 && contenders[0].Game.Year is not null ? contenders[0] : null;
    }
}
