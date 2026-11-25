using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Catalog;
namespace Romd.Admin.Application.MetadataProviders;

/// <summary>One identity decision path for metadata and artwork. Providers only supply evidence.</summary>
public sealed class ProviderIdentityService(IProviderIdentityStore store, IEnumerable<IProviderIdentityAdapter> adapters,
    IEnumerable<IArtworkIdentityBridge> bridges, TimeProvider clock, IUnitOfWork unitOfWork,
    ILogger<ProviderIdentityService> logger) : IProviderIdentityService
{
    public async Task<string?> ResolveTrustedAsync(int titleId, string providerId, CancellationToken ct)
    {
        await DiscoverAsync(titleId, null, ct);
        return await GetTrustedAsync(titleId, providerId, ct);
    }
    public Task<string?> GetTrustedAsync(int titleId, string providerId, CancellationToken ct) =>
        store.ReadTrustedAsync(titleId, providerId, ct);
    public async Task<ProviderIdentityDiscoveryResult> DiscoverAsync(int titleId, JobContext? owner, CancellationToken ct)
    {
        var retryAfterByProvider = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        // Metadata-capable identities are resolved before optional cross-provider bridges.
        foreach (var adapter in adapters.OrderByDescending(x => x.Capabilities.Contains("metadata")).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            var snapshot = await store.ReadAsync(titleId, ct);
            if (snapshot is null)
                break;
            var link = snapshot.Links.SingleOrDefault(x => x.ProviderId == adapter.Id);
            if (link is { Confirmed: true } or { Suppressed: true } || link is { Trusted: true })
                continue;
            var available = await adapter.GetAvailabilityAsync(ct);
            if (!available.Enabled || !available.Configured)
                continue;
            bool datRequiresReview = snapshot.DatFamily?.RequiresReview == true;
            bool steamGridDb = adapter.Id == "steamgriddb";
            bool igdbMetadata = adapter.Id == "igdb";
            var parent = steamGridDb && !datRequiresReview ? snapshot.DatParent : null;
            var igdb = steamGridDb && !datRequiresReview
                ? snapshot.Links.SingleOrDefault(x => x is { ProviderId: "igdb", Trusted: true, Suppressed: false, GameId: not null })
                : null;
            var igdbDependency = igdb?.GameId is { } igdbGameId
                ? new ProviderIdentityDependency(igdb.ProviderId, igdbGameId, igdb.Revision)
                : null;
            var cachedIgdbDependency = link?.Decision?.Dependencies?.FirstOrDefault(x => x.ProviderId == "igdb");
            var delay = link?.Decision?.State == "Unavailable" ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(6);
            var provisionalPolicyVersion = steamGridDb ? ProvisionalProviderMatchPolicy.Version :
                igdbMetadata ? ProvisionalProviderMatchPolicy.MetadataVersion : null;
            var needsPolicyEvaluation = link?.Decision is { State: "NeedsReview" or "NoMatch" } priorDecision &&
                (provisionalPolicyVersion is not null && priorDecision.ProvisionalPolicyVersion != provisionalPolicyVersion ||
                    steamGridDb && priorDecision.ArtworkReleasePolicyVersion != ArtworkReleaseMatchPolicy.Version);
            if (!needsPolicyEvaluation && link?.Decision?.State != "AutoAccepted" && link?.InputFingerprint == snapshot.Fingerprint
                && link.LastAttempt > clock.GetUtcNow() - delay &&
                (!steamGridDb || igdbDependency == cachedIgdbDependency && link.Decision?.EvaluatedDatParent == parent?.Match &&
                    (link.Decision?.EvaluatedArtworkPlatformAliases ?? []).SequenceEqual(snapshot.ArtworkPlatformAliases)))
                continue;
            ArtworkBridgeMatch? bridged = null;
            ProviderIdentityDependency? dependency = igdbDependency;
            foreach (var bridge in bridges.Where(x => !datRequiresReview && x.TargetProviderId == adapter.Id))
            {
                var source = snapshot.Links.SingleOrDefault(x => x.ProviderId == bridge.SourceProviderId && x.Trusted && !x.Suppressed);
                if (source?.GameId is null)
                    continue;
                var resolved = await bridge.ResolveAsync(source.GameId, ct);
                if (resolved.IsError && RememberThrottle(adapter.Id, resolved.Errors))
                    break;
                if (!resolved.IsError)
                    bridged = resolved.Value;
                if (bridged is not null)
                { dependency = new(source.ProviderId, source.GameId, source.Revision); break; }
            }
            if (retryAfterByProvider.ContainsKey(adapter.Id))
                continue;
            var facts = snapshot.Facts;
            DatParentMatch? inherited = null;
            if (bridged is null && parent is not null)
            {
                var resolved = await adapter.ResolveAsync(parent.Match.GameId, ct);
                if (resolved.IsError && RememberThrottle(adapter.Id, resolved.Errors))
                    continue;
                if (!resolved.IsError && resolved.Value.Id == parent.Match.GameId)
                {
                    inherited = parent.Match;
                    dependency = null;
                    facts = new(facts.Name, [.. facts.Names, parent.TitleName], facts.Years, facts.Platforms, facts.Provenance);
                    bridged = new(resolved.Value,
                        $"The hash-verified DAT clone inherits the trusted SteamGridDB match for {parent.TitleName} via {parent.ReleaseName}.");
                }
            }
            var found = bridged is not null ? ErrorOr.ErrorOrFactory.From(new SearchEvidence([bridged.Game], true, true)) :
                await SearchCandidatesAsync(adapter, snapshot.Facts, snapshot.DatFamily?.Names ?? [], ct);
            if (found.IsError && RememberThrottle(adapter.Id, found.Errors))
                continue;
            ProviderIdentityDecision decision;
            ProviderGame? accepted = null;
            if (found.IsError)
                decision = new("Unavailable", "Provider search temporarily unavailable.", snapshot.Facts.Provenance, snapshot.Fingerprint,
                    [], dependency is null ? null : [dependency]);
            else
            {
                var candidates = found.Value.Games.Select(game => new IdentityCandidate(game.Id, game.Name, game.Aliases ?? [],
                    RelevantYears(game, snapshot.Facts.Platforms), game.Platforms ?? [])).ToArray();
                // Bridge trust is tied to the source game and its association revision.
                var evidence = new IdentitySearchEvidence(candidates, found.Value.Complete,
                    bridged is null ? null : new TrustedIdentityBridgeEvidence(bridged.Game.Id, bridged.Evidence));
                var evaluation = ProviderIdentityPolicy.Decide(facts, evidence, adapter.IdentityScope);
                bool artworkSearch = steamGridDb && bridged is null;
                if (!datRequiresReview && !artworkSearch && evaluation is IdentityDecision.AutoAccepted match)
                    accepted = found.Value.Games.Single(x => x.Id == match.CandidateId);
                var ranked = evaluation.Candidates.Select(x => new ProviderIdentityCandidate(
                    found.Value.Games.Single(g => g.Id == x.CandidateId), x.Score, x.Eligible, x.Evidence, x.Conflicts)).ToArray();
                var trustedGame = artworkSearch && igdb?.TrustedGame is { } sourceGame && sourceGame.Id == igdb.GameId
                    ? sourceGame
                    : null;
                var trustedPlatformYears = trustedGame is not null
                    ? PlatformYears(trustedGame, snapshot.Facts.Platforms).Where(year => year is >= 1900 and <= 2200).ToArray()
                    : [];
                if (trustedPlatformYears.Length > 0)
                    ranked = RankArtworkReviewByPlatformYear(ranked, trustedPlatformYears);
                decision = new(evaluation.State, evaluation.Explanation, snapshot.Facts.Provenance, snapshot.Fingerprint,
                    ranked, dependency is null ? [] : [dependency]);
                if (datRequiresReview)
                    decision = decision with
                    {
                        State = "NeedsReview",
                        Explanation = "The stored ROM matches do not identify a single resolved clone family in an active DAT. Confirm a provider game identity."
                    };
                else if (artworkSearch)
                {
                    var aliasReleases = trustedGame?.Releases?.Where(release => release.Year is >= 1900 and <= 2200 &&
                        !trustedPlatformYears.Contains(release.Year.Value) && snapshot.ArtworkPlatformAliases.Any(alias =>
                            string.Equals(alias.Name, release.Platform, StringComparison.OrdinalIgnoreCase))).ToArray() ?? [];
                    var artworkYears = trustedPlatformYears.Concat(aliasReleases.Select(release => release.Year!.Value)).Distinct().ToArray();
                    var release = found.Value is { Complete: true, Consistent: true }
                        ? ArtworkReleaseMatchPolicy.Select(snapshot.Facts, ranked, artworkYears, trustedGame?.Year)
                        : null;
                    if (release is not null)
                    {
                        accepted = release.Game;
                        var alias = aliasReleases.FirstOrDefault(entry => entry.Year == release.Game.Year)?.Platform;
                        var yearSource = alias is not null ? $"the trusted IGDB release on {alias}, a ROMD system alias" :
                            snapshot.Facts.Years.Count > 0 ? "local catalog evidence" : "the trusted IGDB release on the local platform";
                        decision = decision with
                        {
                            State = "AutoAccepted",
                            Explanation = $"SteamGridDB returned a single matching title for {accepted.Year}, supported by {yearSource}.",
                            Candidates = [release, .. ranked.Where(candidate => candidate.Game.Id != release.Game.Id)],
                            Dependencies = artworkYears.Length == 0 ? [] : decision.Dependencies,
                            ArtworkPlatformAliases = aliasReleases.Length > 0 ? snapshot.ArtworkPlatformAliases : null
                        };
                    }
                    else if (evaluation is IdentityDecision.AutoAccepted)
                        decision = decision with
                        {
                            State = "NeedsReview",
                            Explanation = "SteamGridDB title search did not establish a unique match with a corroborated release year."
                        };
                }
                if (artworkSearch)
                {
                    var provisionalGameId = decision.State == "NeedsReview" && found.Value.Consistent && link?.GameId is null &&
                        snapshot.DatFamily is { RequiresReview: false }
                        ? ProvisionalProviderMatchPolicy.SelectGame(snapshot.Facts, decision.Candidates, trustedPlatformYears)
                        : null;
                    decision = decision with
                    {
                        ProvisionalGameId = provisionalGameId,
                        ProvisionalPolicyVersion = ProvisionalProviderMatchPolicy.Version
                    };
                }
            }
            if (igdbMetadata && bridged is null && !found.IsError)
            {
                string? provisionalGameId = null;
                if (link?.GameId is null && found.Value.Consistent && snapshot.DatFamily is { RequiresReview: false } &&
                    (decision.State == "NeedsReview" || accepted is not null && snapshot.Facts.Years.Count == 0))
                {
                    provisionalGameId = ProvisionalProviderMatchPolicy.SelectMetadataGame(snapshot.Facts, decision.Candidates);
                }
                decision = decision with
                {
                    ProvisionalGameId = provisionalGameId,
                    ProvisionalPolicyVersion = ProvisionalProviderMatchPolicy.MetadataVersion
                };
                if (provisionalGameId is not null)
                {
                    accepted = null;
                    decision = decision with
                    {
                        State = "NeedsReview",
                        Explanation = "The active DAT ROM match and the provider release support use of this game provisionally. Confirm the provider identity to apply it to library rules."
                    };
                }
            }
            decision = decision with
            {
                LocalEvidence = new(snapshot.Facts.Name, snapshot.Facts.Names, snapshot.Facts.Years, snapshot.Facts.Platforms),
                ArtworkReleasePolicyVersion = steamGridDb ? ArtworkReleaseMatchPolicy.Version : null,
                DatParentMatch = accepted is not null ? inherited : null,
                EvaluatedDatParent = parent?.Match,
                EvaluatedArtworkPlatformAliases = steamGridDb && snapshot.ArtworkPlatformAliases.Count > 0
                    ? snapshot.ArtworkPlatformAliases : null
            };
            await PersistDecisionAsync(snapshot, adapter.Id, link?.Revision ?? Guid.Empty, decision, accepted, owner, ct);
        }
        return retryAfterByProvider.Count == 0 ? ProviderIdentityDiscoveryResult.Empty :
            new(new System.Collections.ObjectModel.ReadOnlyDictionary<string, DateTimeOffset>(retryAfterByProvider));

        bool RememberThrottle(string providerId, IReadOnlyList<ErrorOr.Error> errors)
        {
            if (ArtworkProviderRetryPolicy.GetDeferredUntil(errors, clock.GetUtcNow()) is not { } due)
                return false;
            retryAfterByProvider[providerId] = due;
            // Do not cache throttling as identity evidence or stop independent providers.
            // The caller determines whether its work actually needs this provider.
            return true;
        }
    }

    private async Task PersistDecisionAsync(ProviderIdentitySnapshot snapshot, string providerId, Guid expectedRevision,
        ProviderIdentityDecision decision, ProviderGame? accepted, JobContext? owner, CancellationToken ct)
    {
        IStagedProviderIdentityDecision? staged = null;
        Exception? primaryFailure = null;
        bool completed = false;
        async Task Stage(CancellationToken token)
        {
            staged = await store.StageDecisionAsync(snapshot, providerId, expectedRevision, decision, accepted, token);
        }
        try
        {
            if (owner is not null)
                await owner.ExecuteOwnedMutationAsync(Stage, ct);
            else
                await unitOfWork.ExecuteInTransactionAsync<Success>(async token =>
                {
                    await Stage(token);
                    // Rejected stale evidence is still a completed conditional attempt. Preserve
                    // the owner's commit/no-op behavior instead of converting Applied=false to an error.
                    return Result.Success;
                }, logger, ct);
            completed = true;
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            try
            { staged?.Dispose(); }
            catch (Exception error) when (completed || primaryFailure is not null)
            {
                logger.LogWarning(error, "Provider identity decision tracking cleanup failed for title {TitleId}, provider {ProviderId}",
                    snapshot.TitleId, providerId);
            }
        }
    }

    private sealed record SearchEvidence(IReadOnlyList<ProviderGame> Games, bool Complete, bool Consistent);

    private static async Task<ErrorOr.ErrorOr<SearchEvidence>> SearchCandidatesAsync(
        IProviderIdentityAdapter adapter, IdentityFacts facts, IReadOnlyList<string> datNames, CancellationToken ct)
    {
        // Bound provider load and make the query set independent of evidence enumeration order.
        // Gather the whole set before deciding, so an alias cannot hide a plausible alternative.
        var queries = datNames.Concat(facts.Names.Order(StringComparer.Ordinal)).Prepend(facts.Name)
            .SelectMany(name => new[] { ArtworkIdentityMatcher.Normalize(name), ProviderIdentityNames.Normalize(name) })
            .Where(query => query.Length > 0).Distinct(StringComparer.Ordinal).Take(7).ToArray();
        var complete = queries.Length is > 0 and <= 6;
        var consistent = complete;
        var games = new Dictionary<string, ProviderGame>(StringComparer.Ordinal);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            foreach (var query in queries.Take(6))
            {
                var result = await adapter.SearchAsync(query, deadline.Token);
                // A failure is retryable, and rate limiting must retain its deferred-until metadata.
                if (result.IsError)
                    return result.Errors;
                complete &= result.Value.Completeness == ProviderGameSearchCompleteness.Complete;
                consistent &= result.Value.Completeness != ProviderGameSearchCompleteness.BudgetLimited &&
                    result.Value.Items.Count < result.Value.Limit;
                foreach (var game in result.Value.Items)
                    if (!games.TryAdd(game.Id, game) && !SameEvidence(games[game.Id], game))
                        complete = consistent = false;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ProviderMatchErrors.Unavailable; }
        return new SearchEvidence(games.Values.ToArray(), complete, consistent);
    }

    private static bool SameEvidence(ProviderGame first, ProviderGame next) =>
        first.Name == next.Name && first.Year == next.Year &&
        (first.Platforms ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(next.Platforms ?? []) &&
        (first.Aliases ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(next.Aliases ?? []) &&
        (first.Releases ?? []).Select(x => (x.Year, x.Platform)).ToHashSet()
            .SetEquals((next.Releases ?? []).Select(x => (x.Year, x.Platform)));

    private static ProviderIdentityCandidate[] RankArtworkReviewByPlatformYear(
        ProviderIdentityCandidate[] candidates, IReadOnlyList<int> trustedPlatformYears)
    {
        // Keep identity scores intact. Year evidence breaks ties among review candidates.
        return candidates.Select(candidate => (Candidate: candidate,
                YearMatches: candidate.Conflicts.Count == 0 && candidate.Game.Year is { } year && trustedPlatformYears.Contains(year)))
            .OrderByDescending(result => result.Candidate.Score)
            .ThenByDescending(result => result.YearMatches)
            .Select(result => result.YearMatches
                ? result.Candidate with
                {
                    Evidence = [.. result.Candidate.Evidence, "Release year matches the trusted IGDB game on the local platform."]
                }
                : result.Candidate)
            .ToArray();
    }

    private static IReadOnlyList<int> RelevantYears(ProviderGame game, IReadOnlyList<string> platforms)
    {
        var releases = PlatformYears(game, platforms);
        return releases.Length > 0 ? releases : game.Year is { } year ? [year] : [];
    }

    private static int[] PlatformYears(ProviderGame game, IReadOnlyList<string> platforms)
    {
        var names = platforms.Select(ArtworkIdentityMatcher.Normalize).ToHashSet(StringComparer.Ordinal);
        return game.Releases?.Where(x => x.Year is not null && x.Platform is not null && names.Contains(ArtworkIdentityMatcher.Normalize(x.Platform)))
            .Select(x => x.Year!.Value).Distinct().ToArray() ?? [];
    }
}
