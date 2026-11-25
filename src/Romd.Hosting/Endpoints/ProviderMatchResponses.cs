using Romd.Admin.Application.MetadataProviders;
using Contract = Romd.Contracts.Management.MetadataProviders;

namespace Romd.Host.Endpoints;

internal static class ProviderMatchResponses
{
    internal static Contract.ProviderGame ToContract(this ProviderGame game) =>
        new(game.Id, game.Name, game.Url, game.ThumbnailUrl, game.Year, game.Platforms, game.Aliases,
            game.Releases?.Select(release => new Contract.ProviderRelease(release.Year, release.Platform)).ToArray());

    internal static Contract.ProviderGameSearchResult ToContract(this ProviderGameSearchResult search) =>
        new(search.Items.Select(game => game.ToContract()).ToArray(), ToContract(search.Completeness), search.Limit);

    internal static Contract.TitleProviderMatch ToContract(this TitleProviderMatch match) =>
        new(match.ProviderId, match.ProviderName, match.IsAvailable, match.Capabilities, match.Revision, match.State,
            match.ExternalId, match.Game?.ToContract(), match.Confidence, match.LastError, match.MetadataNeedsRefresh,
            match.Evidence, match.MatcherVersion, match.Decision is { } decision ? ToContract(decision) : null,
            match.PendingJobId);

    private static Contract.ProviderGameSearchCompleteness ToContract(ProviderGameSearchCompleteness completeness) =>
        completeness switch
        {
            ProviderGameSearchCompleteness.Complete => Contract.ProviderGameSearchCompleteness.Complete,
            ProviderGameSearchCompleteness.ProviderLimited => Contract.ProviderGameSearchCompleteness.ProviderLimited,
            ProviderGameSearchCompleteness.BudgetLimited => Contract.ProviderGameSearchCompleteness.BudgetLimited,
            _ => throw new ArgumentOutOfRangeException(nameof(completeness))
        };

    private static Contract.ProviderIdentityDecision ToContract(ProviderIdentityDecision decision) =>
        new(decision.State, decision.Explanation, decision.Provenance, decision.InputFingerprint,
            decision.Candidates.Select(candidate => new Contract.ProviderIdentityCandidate(
                candidate.Game.ToContract(), candidate.Score, candidate.Eligible, candidate.Evidence, candidate.Conflicts)).ToArray(),
            decision.Dependencies?.Select(dependency => new Contract.ProviderIdentityDependency(
                dependency.ProviderId, dependency.GameId, dependency.Revision)).ToArray(),
            decision.LocalEvidence is { } facts
                ? new Contract.ProviderIdentityFacts(facts.Name, facts.Names, facts.Years, facts.Platforms)
                : null, decision.ProvisionalGameId);
}
