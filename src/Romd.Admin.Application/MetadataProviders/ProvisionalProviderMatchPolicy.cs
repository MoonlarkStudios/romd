using Romd.Domain.Catalog;

namespace Romd.Admin.Application.MetadataProviders;

public static class ProvisionalProviderMatchPolicy
{
    public const string Version = "steamgriddb-poster-1";

    internal static string? SelectGame(IdentityFacts facts, IReadOnlyList<ProviderIdentityCandidate> ranked,
        IEnumerable<int> trustedPlatformYears)
    {
        var years = trustedPlatformYears.Where(year => year is >= 1900 and <= 2200).ToHashSet();
        if (years.Count == 0 || ranked.Count == 0)
            return null;

        var lead = ranked[0];
        if (!lead.Eligible || lead.Conflicts.Count != 0 || lead.Game.Year is not { } year ||
            !years.Contains(year) || facts.Years.Count != 0 && !facts.Years.Contains(year))
            return null;

        var name = ProviderIdentityNames.Normalize(lead.Game.Name);
        if (name != ProviderIdentityNames.Normalize(facts.Name) &&
            !facts.Names.Any(local => name == ProviderIdentityNames.Normalize(local)))
            return null;

        // A plausible competitor can also be missing its release year.
        const int minimumScoreLead = 15;
        return ranked.Skip(1).Any(candidate => lead.Score - candidate.Score < minimumScoreLead &&
            candidate.Conflicts.Count == 0 &&
            (candidate.Game.Year is not { } candidateYear || years.Contains(candidateYear))) ? null : lead.Game.Id;
    }
}
