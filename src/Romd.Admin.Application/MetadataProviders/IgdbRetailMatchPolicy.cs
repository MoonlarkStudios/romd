using Romd.Domain.Catalog;

namespace Romd.Admin.Application.MetadataProviders;

internal static class IgdbRetailMatchPolicy
{
    public const string Version = "igdb-retail-title-1";

    internal static bool CorroboratesDatedMatch(IdentityFacts facts, DatFamilySearchEvidence datFamily,
        IReadOnlyList<ProviderIdentityCandidate> ranked, ProviderGame game)
    {
        // A search alias or clone ancestor can find a game without identifying the owned release.
        string name = ProviderIdentityNames.Normalize(game.Name);
        return name == ProviderIdentityNames.Normalize(facts.Name) &&
            datFamily.MatchedRetailTitles.Count > 0 &&
            datFamily.MatchedRetailTitles.All(title => name == ProviderIdentityNames.Normalize(title)) &&
            ranked.Skip(1).All(candidate => candidate.Conflicts.Count > 0);
    }
}
