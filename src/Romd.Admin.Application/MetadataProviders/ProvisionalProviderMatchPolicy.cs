using Romd.Domain.Catalog;

namespace Romd.Admin.Application.MetadataProviders;

public static class ProvisionalProviderMatchPolicy
{
    public const string Version = "steamgriddb-poster-1";
    public const string MetadataVersion = "igdb-metadata-1";
    private const int MinimumScoreLead = 15;

    internal static string? SelectMetadataGame(IdentityFacts facts, IReadOnlyList<ProviderIdentityCandidate> ranked)
    {
        if (ranked.Count == 0)
            return null;
        var lead = ranked[0];
        var platforms = facts.Platforms.Select(ArtworkIdentityMatcher.Normalize).ToHashSet(StringComparer.Ordinal);
        bool platformRelease = lead.Game.Releases?.Any(release => release is { Platform: not null, Year: >= 1900 and <= 2200 } &&
            platforms.Contains(ArtworkIdentityMatcher.Normalize(release.Platform)) &&
            (facts.Years.Count == 0 || facts.Years.Contains(release.Year.Value))) == true;
        if (!platformRelease || !lead.Eligible || lead.Conflicts.Count != 0 || !ExactName(facts, lead.Game.Name))
            return null;
        return ranked.Skip(1).Any(candidate => lead.Score - candidate.Score < MinimumScoreLead &&
            candidate.Conflicts.Count == 0) ? null : lead.Game.Id;
    }

    private static bool ExactName(IdentityFacts facts, string candidateName, IReadOnlyList<string>? corroboratingTitles = null)
    {
        var name = ProviderIdentityNames.Normalize(candidateName);
        return name == ProviderIdentityNames.Normalize(facts.Name) ||
            facts.Names.Any(local => name == ProviderIdentityNames.Normalize(local)) ||
            corroboratingTitles?.Any(title => name == ProviderIdentityNames.Normalize(title)) == true;
    }

    internal static string? SelectGame(IdentityFacts facts, IReadOnlyList<ProviderIdentityCandidate> ranked,
        IEnumerable<int> trustedPlatformYears, IReadOnlyList<string> corroboratingTitles)
    {
        var years = trustedPlatformYears.Where(year => year is >= 1900 and <= 2200).ToHashSet();
        if (years.Count == 0 || ranked.Count == 0)
            return null;

        var lead = ranked[0];
        if (!lead.Eligible || lead.Conflicts.Count != 0 || lead.Game.Year is not { } year ||
            !years.Contains(year) || facts.Years.Count != 0 && !facts.Years.Contains(year))
            return null;

        if (!ExactName(facts, lead.Game.Name, corroboratingTitles))
            return null;

        // A plausible competitor can also be missing its release year.
        return ranked.Skip(1).Any(candidate => lead.Score - candidate.Score < MinimumScoreLead &&
            candidate.Conflicts.Count == 0 &&
            (candidate.Game.Year is not { } candidateYear || years.Contains(candidateYear))) ? null : lead.Game.Id;
    }
}
