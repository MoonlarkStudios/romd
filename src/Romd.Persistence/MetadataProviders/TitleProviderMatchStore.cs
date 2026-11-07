using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.MetadataProviders;
using Romd.Application.Common.Concurrency;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;

namespace Romd.Persistence.MetadataProviders;

/// <summary>Detached match reads and staged database writes. The application owns the transaction.</summary>
public sealed class TitleProviderMatchStore : ITitleProviderMatchStore
{
    private readonly RomdDbContext _db;
    private readonly TimeProvider _clock;

    public TitleProviderMatchStore(RomdDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public Task<bool> ExistsAsync(int titleId, CancellationToken ct) => _db.Titles.AnyAsync(x => x.Id == titleId, ct);

    public Task<bool> IsSuppressedAsync(int titleId, string providerId, CancellationToken ct) =>
        _db.Set<TitleProviderMatchStateEntity>().AnyAsync(x => x.TitleId == titleId && x.ProviderId == providerId && x.SuppressAutomaticMatch, ct);

    public async Task<Guid?> GetLinkRevisionAsync(int titleId, string providerId, string gameId, CancellationToken ct)
    {
        // Publication already owns a transaction: current trust, link and revision share one statement.
        var current = await (from link in _db.TitleExternalIds
                             where link.TitleId == titleId && link.Provider.ToLower() == providerId && link.ExternalId == gameId
                             let state = _db.Set<TitleProviderMatchStateEntity>().FirstOrDefault(x => x.TitleId == titleId && x.ProviderId == providerId)
                             select new
                             {
                                 Link = link,
                                 State = state,
                                 Trusted = ProviderIdentityDatabaseFunctions.IsTrusted(titleId, providerId, ProviderIdentityPolicy.Version)
                             }).SingleOrDefaultAsync(ct);
        return current is { Trusted: true } && current.State?.SuppressAutomaticMatch != true
            ? Revision(current.State, current.Link) : null;
    }

    public async Task<Guid> ReadRevisionAsync(int titleId, string providerId, CancellationToken ct) => Revision(
        await _db.Set<TitleProviderMatchStateEntity>().SingleOrDefaultAsync(x => x.TitleId == titleId && x.ProviderId == providerId, ct),
        await _db.TitleExternalIds.SingleOrDefaultAsync(x => x.TitleId == titleId && x.Provider.ToLower() == providerId, ct));

    public async Task<ErrorOr<Success>> StageChangeAsync(int titleId, string providerId,
        RevisionPrecondition ifMatch, ProviderGame? game, bool unlink, CancellationToken ct)
    {
        RequireTransaction();
        await JobAcceptanceLock.AcquireAsync(_db, 12901, titleId, ct);
        // Every identity mutation takes the same title lock as enrichment and artwork publication.
        if (await _db.Titles.Where(x => x.Id == titleId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, Guid.NewGuid()), ct) != 1)
            return CatalogErrors.TitleNotFound();
        var link = await _db.TitleExternalIds.AsTracking().SingleOrDefaultAsync(x => x.TitleId == titleId && x.Provider.ToLower() == providerId, ct);
        var state = await _db.Set<TitleProviderMatchStateEntity>().AsTracking().SingleOrDefaultAsync(x => x.TitleId == titleId && x.ProviderId == providerId, ct);
        if (!ifMatch.IsSatisfiedBy(Revision(state, link)))
            return ProviderMatchErrors.PreconditionFailed;
        var changed = unlink || (game is not null && game.Id != link?.ExternalId);
        if (changed)
        {
            var pending = _db.Set<ArtworkImportJobEntity>().Where(x => x.TitleId == titleId && x.ProviderId == providerId).Select(x => x.Id);
            await _db.Set<ArtworkSelectionEntity>().Where(x => x.TitleId == titleId && x.PendingRequestId != null && pending.Contains(x.PendingRequestId.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, x => x.Revision + 1).SetProperty(x => x.PendingRequestId, (Guid?)null), ct);
        }
        if (state is null)
            _db.Add(state = new TitleProviderMatchStateEntity { TitleId = titleId, ProviderId = providerId });
        if (unlink)
        {
            if (link is not null)
                _db.Remove(link);
            state.GameJson = null;
            state.SuppressAutomaticMatch = true;
            state.IdentityChanged = true;
        }
        else if (game is not null)
        {
            state.IdentityChanged |= link?.ExternalId != game.Id;
            if (link is null)
                _db.Add(link = TitleExternalIdEntity.FromDomain(TitleExternalId.CreateNew(titleId, providerId, game.Id, 1, _clock.GetUtcNow())));
            link.Provider = providerId;
            link.ExternalId = game.Id;
            link.IsConfirmed = true;
            state.GameJson = JsonSerializer.Serialize(game);
            state.SuppressAutomaticMatch = false;
        }
        else
        {
            if (link is null)
                return ProviderMatchErrors.NotLinked;
            link.IsConfirmed = true;
        }
        await _db.Set<TitleProviderMatchStateEntity>().Where(x => x.TitleId == titleId && x.ProviderId != providerId &&
            !x.SuppressAutomaticMatch && !x.AutomaticallyTrusted)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.LastAttemptAt, (DateTimeOffset?)null), ct);
        state.AutomaticallyTrusted = false;
        state.Evidence = unlink ? "Explicitly unlinked; automatic matching is suppressed." : "Manually confirmed.";
        if (state.DecisionJson is { } priorDecision)
        {
            var decision = JsonSerializer.Deserialize<ProviderIdentityDecision>(priorDecision);
            if (decision is not null)
                state.DecisionJson = JsonSerializer.Serialize(decision with { State = unlink ? "Unlinked" : "Confirmed", Explanation = state.Evidence });
        }
        state.MatcherVersion = null;
        state.Revision = Guid.NewGuid();
        return Result.Success;
    }

    public Task<Guid?> FindPendingEnrichmentAsync(int titleId, CancellationToken ct)
    {
        RequireTransaction();
        return _db.Set<EnrichmentJobEntity>().Where(x => x.TitleId == titleId && x.Phase == "Pending")
            .OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<ProviderMatchTitle> ReadTitleAsync(int titleId, CancellationToken ct)
    {
        RequireTransaction();
        return await _db.Titles.Where(x => x.Id == titleId)
            .Select(x => new ProviderMatchTitle(x.Id, x.Name, x.PlatformId)).SingleAsync(ct);
    }

    public Task AddEnrichmentStagedAsync(EnrichmentJob job, CancellationToken ct)
    {
        RequireTransaction();
        ct.ThrowIfCancellationRequested();
        _db.Add(EnrichmentJobEntity.FromDomain(job));
        return Task.CompletedTask;
    }

    public async Task<TitleProviderMatchFacts> ReadFactsAsync(int titleId, IReadOnlyCollection<string> providerIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providerIds);
        ct.ThrowIfCancellationRequested();
        if (providerIds.Count == 0)
            return new([], [], []);
        var ids = providerIds.Distinct(StringComparer.Ordinal).ToArray();
        var normalizedIds = ids.Select(id => id.ToLowerInvariant()).ToArray();
        var links = await _db.TitleExternalIds.Where(x => x.TitleId == titleId && normalizedIds.Contains(x.Provider.ToLower())).ToListAsync(ct);
        var states = await _db.Set<TitleProviderMatchStateEntity>().Where(x => x.TitleId == titleId && ids.Contains(x.ProviderId)).ToListAsync(ct);
        var layers = await _db.TitleMetadataLayers.Where(x => x.TitleId == titleId && ids.Contains(x.SourceId)).ToListAsync(ct);
        return new(links.Select(x => x.ToDomain()).ToArray(), states.Select(x => new ProviderMatchState(
            x.ProviderId, x.Revision, x.SuppressAutomaticMatch, x.IdentityChanged, x.Evidence, x.MatcherVersion,
            x.GameJson is { } game ? JsonSerializer.Deserialize<ProviderGame>(game) : null,
            x.DecisionJson is { } decision ? JsonSerializer.Deserialize<ProviderIdentityDecision>(decision) : null)).ToArray(),
            layers.Select(x => x.ToDomain()).ToArray());
    }

    private void RequireTransaction()
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Provider match writes require a caller-owned transaction.");
    }

    private static Guid Revision(TitleProviderMatchStateEntity? state, TitleExternalIdEntity? link) =>
        ProviderMatchRevisions.For(state?.Revision, link?.ExternalId, link?.IsConfirmed);
}
