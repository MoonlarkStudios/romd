using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Contracts.Management.MetadataProviders;
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
            var delay = link?.Decision?.State == "Unavailable" ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(6);
            if (link?.Decision?.State != "AutoAccepted" && link?.InputFingerprint == snapshot.Fingerprint && link.LastAttempt > clock.GetUtcNow() - delay)
                continue;
            ArtworkBridgeMatch? bridged = null;
            ProviderIdentityDependency? dependency = null;
            foreach (var bridge in bridges.Where(x => x.TargetProviderId == adapter.Id))
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
            var found = bridged is not null ? ErrorOr.ErrorOrFactory.From(new SearchEvidence([bridged.Game], true)) :
                await SearchCandidatesAsync(adapter, snapshot.Facts, ct);
            if (found.IsError && RememberThrottle(adapter.Id, found.Errors))
                continue;
            ProviderIdentityDecision decision;
            ProviderGame? accepted = null;
            if (found.IsError)
                decision = new("Unavailable", "Provider search temporarily unavailable.", snapshot.Facts.Provenance, snapshot.Fingerprint, []);
            else
            {
                var candidates = found.Value.Games.Select(game => new IdentityCandidate(game.Id, game.Name, game.Aliases ?? [],
                    RelevantYears(game, snapshot.Facts.Platforms), game.Platforms ?? [])).ToArray();
                // A bridge requires a currently trusted source; retaining its dependency in the input fingerprint fences changes.
                var evidence = new IdentitySearchEvidence(candidates, found.Value.Complete,
                    bridged is null ? null : new TrustedIdentityBridgeEvidence(bridged.Game.Id, bridged.Evidence));
                var evaluation = ProviderIdentityPolicy.Decide(snapshot.Facts, evidence, adapter.IdentityScope);
                if (evaluation is IdentityDecision.AutoAccepted match)
                    accepted = found.Value.Games.Single(x => x.Id == match.CandidateId);
                decision = new(evaluation.State, evaluation.Explanation, snapshot.Facts.Provenance, snapshot.Fingerprint,
                    evaluation.Candidates.Select(x => new ProviderIdentityCandidate(found.Value.Games.Single(g => g.Id == x.CandidateId), x.Score, x.Eligible, x.Evidence, x.Conflicts)).ToArray(), dependency is null ? [] : [dependency]);
            }
            decision = decision with { LocalEvidence = new(snapshot.Facts.Name, snapshot.Facts.Names, snapshot.Facts.Years, snapshot.Facts.Platforms) };
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

    private sealed record SearchEvidence(IReadOnlyList<ProviderGame> Games, bool Complete);

    private static async Task<ErrorOr.ErrorOr<SearchEvidence>> SearchCandidatesAsync(
        IProviderIdentityAdapter adapter, IdentityFacts facts, CancellationToken ct)
    {
        // Bound provider load and make the query set independent of evidence enumeration order.
        // Gather the whole set before deciding, so an alias cannot hide a plausible alternative.
        var queries = facts.Names.Order(StringComparer.Ordinal).Prepend(facts.Name)
            .SelectMany(name => new[] { ArtworkIdentityMatcher.Normalize(name), ProviderIdentityNames.Normalize(name) })
            .Where(query => query.Length > 0).Distinct(StringComparer.Ordinal).Take(7).ToArray();
        var complete = queries.Length is > 0 and <= 6;
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
                foreach (var game in result.Value.Items)
                    if (!games.TryAdd(game.Id, game) && !SameEvidence(games[game.Id], game))
                        complete = false;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ProviderMatchErrors.Unavailable; }
        return new SearchEvidence(games.Values.ToArray(), complete);
    }

    private static bool SameEvidence(ProviderGame first, ProviderGame next) =>
        first.Name == next.Name && first.Year == next.Year &&
        (first.Platforms ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(next.Platforms ?? []) &&
        (first.Aliases ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(next.Aliases ?? []) &&
        (first.Releases ?? []).Select(x => (x.Year, x.Platform)).ToHashSet()
            .SetEquals((next.Releases ?? []).Select(x => (x.Year, x.Platform)));

    private static IReadOnlyList<int> RelevantYears(ProviderGame game, IReadOnlyList<string> platforms)
    {
        var names = platforms.Select(ArtworkIdentityMatcher.Normalize).ToHashSet();
        var releases = game.Releases?.Where(x => x.Year is not null && x.Platform is not null && names.Contains(ArtworkIdentityMatcher.Normalize(x.Platform)))
            .Select(x => x.Year!.Value).Distinct().ToArray();
        return releases is { Length: > 0 } ? releases : game.Year is { } year ? [year] : [];
    }
}
