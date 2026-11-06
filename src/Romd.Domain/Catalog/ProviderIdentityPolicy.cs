namespace Romd.Domain.Catalog;

/// <summary>Whether provider entries identify a release or collect artwork across releases.</summary>
public enum ProviderIdentityScope { Release, CrossPlatformArtwork }

/// <summary>Scores rank evidence; they are not probabilities. Contradictions veto automatic acceptance.</summary>
public static class ProviderIdentityPolicy
{
    private static bool HasMarker(string name, string marker) => marker == "+" ? name.Contains('+') :
        name.Split([' ', '(', ')', '[', ']', ':', '-'], StringSplitOptions.RemoveEmptyEntries).Contains(marker, StringComparer.Ordinal);
    public const string Version = "provider-identity-5";
    public static IdentityDecision Decide(IdentityFacts facts, IdentitySearchEvidence search,
        ProviderIdentityScope scope = ProviderIdentityScope.Release)
    {
        var names = facts.Names.Prepend(facts.Name).Select(ProviderIdentityNames.Normalize).ToHashSet(StringComparer.Ordinal);
        var platforms = facts.Platforms.Select(ArtworkIdentityMatcher.Normalize).ToHashSet(StringComparer.Ordinal);
        var ranked = search.Candidates.DistinctBy(x => x.Id).Select(candidate =>
        {
            var evidence = new List<string>();
            var conflicts = new List<string>();
            var exact = names.Contains(ProviderIdentityNames.Normalize(candidate.Name));
            var alias = !exact && candidate.Aliases.Any(x => names.Contains(ProviderIdentityNames.Normalize(x)));
            var score = exact ? 100 : alias ? 90 : 0;
            var markers = new[] { "+", "REMAKE", "REMASTER", "REMASTERED", "HACK", "DEMO", "BETA", "COLLECTION", "DELUXE", "DEFINITIVE" };
            var localName = ArtworkIdentityMatcher.Normalize(facts.Name);
            var candidateName = ArtworkIdentityMatcher.Normalize(candidate.Name);
            if (markers.Any(marker => HasMarker(localName, marker) != HasMarker(candidateName, marker)))
                conflicts.Add("Edition or modification markers differ from the local title.");
            if (exact || alias)
                evidence.Add(exact ? "Edition-preserving name matches local evidence." : "Provider alias matches local evidence.");
            else
                conflicts.Add("No identity-preserving name or alias match.");
            var platformMatch = candidate.Platforms.Any(x => platforms.Contains(ArtworkIdentityMatcher.Normalize(x)));
            if (platformMatch)
            { score += 20; evidence.Add("Platform matches the local system."); }
            else if (candidate.Platforms.Count > 0 && platforms.Count > 0)
                conflicts.Add("Provider platforms contradict the local system.");
            var yearMatch = candidate.Years.Intersect(facts.Years).Any();
            if (yearMatch)
            { score += 20; evidence.Add("Release year matches independent catalog evidence."); }
            else if (candidate.Years.Count > 0 && facts.Years.Count > 0)
            {
                if (scope == ProviderIdentityScope.Release)
                    conflicts.Add("Release years conflict with local catalog evidence.");
                else
                    evidence.Add("Provider date differs; cross-platform artwork may cover another release.");
            }
            if (!yearMatch && facts.Years.Count == 0)
                evidence.Add("Local release year is unknown.");
            // Cross-platform artwork can use a unique exact name. Release identities and provider aliases need corroboration.
            var eligible = conflicts.Count == 0 && (exact && (scope == ProviderIdentityScope.CrossPlatformArtwork || platformMatch || yearMatch) || alias && platformMatch && yearMatch);
            return new IdentityAssessment(candidate.Id, score, eligible, evidence, conflicts);
        }).OrderByDescending(x => x.Score).ThenBy(x => x.CandidateId, StringComparer.Ordinal).ToArray();
        // Unseen alternatives can invalidate both uniqueness and an apparent no-match, even with bridge evidence.
        if (!search.Complete)
            return new IdentityDecision.NeedsReview(null,
            "Provider search evidence is incomplete. Refine the query or confirm an explicit provider game identity.", ranked);
        if (search.TrustedBridge is { } bridge && ranked.All(candidate => candidate.Conflicts.Count == 0))
            return new IdentityDecision.AutoAccepted(ranked.Single(candidate => candidate.CandidateId == bridge.CandidateId), bridge.Evidence, ranked);
        var contenders = ranked.Where(x => x.Conflicts.Count == 0).ToArray();
        var best = contenders.FirstOrDefault() ?? ranked.FirstOrDefault();
        if (best is null)
            return new IdentityDecision.NoMatch("Provider search returned no candidates.");
        if (best.Eligible && (contenders.Length < 2 || best.Score - contenders[1].Score >= 15))
            return new IdentityDecision.AutoAccepted(best, string.Join(" ", best.Evidence), ranked);
        return new IdentityDecision.NeedsReview(best, best.Conflicts.Count > 0 ? string.Join(" ", best.Conflicts) :
            contenders.Length > 1 ? "Multiple plausible candidates; review the edition." : "Independent corroborating evidence is insufficient.", ranked);
    }
}
