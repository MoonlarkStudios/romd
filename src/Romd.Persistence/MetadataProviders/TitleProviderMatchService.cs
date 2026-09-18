using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.MetadataProviders;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Configuration;
using Microsoft.Extensions.Options;

namespace Romd.Persistence.MetadataProviders;

public sealed class TitleProviderMatchService(RomdDbContext db, IEnumerable<IProviderIdentityAdapter> adapters,
    IOptions<EnrichmentOptions>? options = null)
    : ITitleProviderMatchService
{
    public Task<bool> IsSuppressedAsync(int titleId, string providerId, CancellationToken ct) =>
        db.Set<TitleProviderMatchStateEntity>().AnyAsync(x => x.TitleId == titleId && x.ProviderId == providerId && x.SuppressAutomaticMatch, ct);

    private IProviderIdentityAdapter? Adapter(string id) => adapters.SingleOrDefault(x => x.Id == id);

    public async Task DiscoverAsync(int titleId, JobContext context, CancellationToken ct)
    {
        var title = await db.Titles.SingleOrDefaultAsync(x => x.Id == titleId, ct);
        if (title is null) return;
        foreach (var adapter in adapters.Where(x => !x.Capabilities.Contains("metadata")))
        {
            var available = await adapter.GetAvailabilityAsync(ct);
            if (!available.Enabled || !available.Configured || await IsSuppressedAsync(titleId, adapter.Id, ct) ||
                await db.TitleExternalIds.AnyAsync(x => x.TitleId == titleId && x.Provider == adapter.Id, ct)) continue;
            var found = await adapter.SearchAsync(title.Name, ct);
            if (found.IsError) continue;
            await context.ExecuteOwnedMutationAsync(async token =>
            {
                await db.Titles.Where(x => x.Id == titleId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, x => x.Revision), token);
                if (await IsSuppressedAsync(titleId, adapter.Id, token) || await db.TitleExternalIds.AnyAsync(x => x.TitleId == titleId && x.Provider == adapter.Id, token)) return;
                var state = await db.Set<TitleProviderMatchStateEntity>().AsTracking().SingleOrDefaultAsync(x => x.TitleId == titleId && x.ProviderId == adapter.Id, token);
                if (state is null) db.Add(state = new TitleProviderMatchStateEntity { TitleId = titleId, ProviderId = adapter.Id });
                // Name-only evidence is a suggestion, never authority to download or apply content.
                state.GameJson = found.Value.FirstOrDefault() is { } game ? JsonSerializer.Serialize(game) : null;
                state.Revision = Guid.NewGuid();
            }, ct);
        }
    }

    public async Task<Guid?> GetLinkRevisionAsync(int titleId, string providerId, string gameId, CancellationToken ct)
    {
        var link = await db.TitleExternalIds.SingleOrDefaultAsync(x => x.TitleId == titleId && x.Provider.ToLower() == providerId, ct);
        if (link?.ExternalId != gameId) return null;
        if (!link.IsConfirmed && link.MatchConfidence < (options?.Value.MinimumAutoEnrichConfidence ?? new EnrichmentOptions().MinimumAutoEnrichConfidence)) return null;
        var state = await db.Set<TitleProviderMatchStateEntity>().SingleOrDefaultAsync(x => x.TitleId == titleId && x.ProviderId == providerId, ct);
        return Revision(state, link);
    }

    public async Task<ErrorOr<IReadOnlyList<TitleProviderMatchDto>>> GetAsync(int titleId, CancellationToken ct)
    {
        if (!await db.Titles.AnyAsync(x => x.Id == titleId, ct)) return ProviderMatchErrors.NotFound;
        var links = await db.TitleExternalIds.Where(x => x.TitleId == titleId).ToListAsync(ct);
        var states = await db.Set<TitleProviderMatchStateEntity>().Where(x => x.TitleId == titleId).ToListAsync(ct);
        var layers = await db.TitleMetadataLayers.Where(x => x.TitleId == titleId).ToListAsync(ct);
        var rows = new List<TitleProviderMatchDto>();
        foreach (var adapter in adapters.OrderBy(x => x.Name))
        {
            var availability = await adapter.GetAvailabilityAsync(ct);
            if (!availability.Enabled) continue;
            var link = links.SingleOrDefault(x => x.Provider.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase));
            var state = states.SingleOrDefault(x => x.ProviderId == adapter.Id);
            var game = state?.GameJson is { } json ? JsonSerializer.Deserialize<ProviderGameDto>(json) : null;
            if (link is not null && game?.Id != link.ExternalId) game = null;
            string? error = null;
            if (game is null && link is not null && availability.Configured)
            {
                var resolved = await adapter.ResolveAsync(link.ExternalId, ct);
                if (!resolved.IsError) game = resolved.Value;
                else error = "Preview unavailable";
            }
            var payload = layers.SingleOrDefault(x => x.SourceId == adapter.Id)?.ToDomain().GetPayload();
            var stale = payload is not null && (payload.ProviderGameId is { } origin
                ? origin != link?.ExternalId : state?.IdentityChanged == true);
            rows.Add(new(adapter.Id, adapter.Name, availability.Configured, adapter.Capabilities,
                Revision(state, link), link is null ? (state?.SuppressAutomaticMatch == true ? "Unlinked" :
                    game is not null ? "NeedsReview" : state is not null ? "NoMatchFound" : "NeedsMatch") :
                link.IsConfirmed ? "Confirmed" : link.MatchConfidence >= (options?.Value.MinimumAutoEnrichConfidence ?? new EnrichmentOptions().MinimumAutoEnrichConfidence) ? "AutoMatched" : "NeedsReview", link?.ExternalId, game,
                link?.MatchConfidence, error, stale));
        }
        return rows;
    }

    public async Task<ErrorOr<IReadOnlyList<ProviderGameDto>>> SearchAsync(string providerId, ProviderMatchSearchRequest request, CancellationToken ct)
    {
        var adapter = Adapter(providerId);
        if (adapter is null) return ProviderMatchErrors.NotFound;
        var availability = await adapter.GetAvailabilityAsync(ct);
        if (!availability.Enabled || !availability.Configured) return ProviderMatchErrors.Unavailable;
        if (!request.Resolve) return await adapter.SearchAsync(request.Query, ct);
        var result = await adapter.ResolveAsync(request.Query, ct);
        if (result.IsError) return result.Errors;
        return new[] { result.Value };
    }

    public async Task<ErrorOr<Success>> SetAsync(int titleId, string providerId, SetProviderMatchRequest request, CancellationToken ct)
    {
        var result = await SearchAsync(providerId, new(request.ExternalId, true), ct);
        if (result.IsError) return result.Errors;
        return await MutateAsync(titleId, providerId, request.ExpectedRevision, result.Value.Single(), false, ct);
    }

    public Task<ErrorOr<Success>> ConfirmAsync(int titleId, string providerId, Guid revision, CancellationToken ct) =>
        MutateAsync(titleId, providerId, revision, null, false, ct);

    public Task<ErrorOr<Success>> UnlinkAsync(int titleId, string providerId, Guid revision, CancellationToken ct) =>
        MutateAsync(titleId, providerId, revision, null, true, ct);

    private async Task<ErrorOr<Success>> MutateAsync(int titleId, string providerId, Guid expected,
        ProviderGameDto? game, bool unlink, CancellationToken ct)
    {
        var adapter = Adapter(providerId);
        if (adapter is null) return ProviderMatchErrors.NotFound;
        var availability = await adapter.GetAvailabilityAsync(ct);
        if (!availability.Enabled || !availability.Configured) return ProviderMatchErrors.Unavailable;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Every identity mutation takes the same title lock as enrichment and artwork publication.
        if (await db.Titles.Where(x => x.Id == titleId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, Guid.NewGuid()), ct) != 1)
            return ProviderMatchErrors.NotFound;
        var link = await db.TitleExternalIds.AsTracking().SingleOrDefaultAsync(x => x.TitleId == titleId && x.Provider.ToLower() == providerId, ct);
        var state = await db.Set<TitleProviderMatchStateEntity>().AsTracking().SingleOrDefaultAsync(x => x.TitleId == titleId && x.ProviderId == providerId, ct);
        if (Revision(state, link) != expected) return ProviderMatchErrors.Conflict;
        var changed = unlink || (game is not null && game.Id != link?.ExternalId);
        if (changed)
        {
            var pending = db.Set<ArtworkImportJobEntity>().Where(x => x.TitleId == titleId && x.ProviderId == providerId).Select(x => x.Id);
            await db.Set<ArtworkSelectionEntity>().Where(x => x.TitleId == titleId && x.PendingRequestId != null && pending.Contains(x.PendingRequestId.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, x => x.Revision + 1).SetProperty(x => x.PendingRequestId, (Guid?)null), ct);
        }
        if (state is null) db.Add(state = new TitleProviderMatchStateEntity { TitleId = titleId, ProviderId = providerId });
        if (unlink)
        {
            if (link is not null) db.Remove(link);
            state.GameJson = null;
            state.SuppressAutomaticMatch = true;
            state.IdentityChanged = true;
        }
        else if (game is not null)
        {
            state.IdentityChanged |= link?.ExternalId != game.Id;
            if (link is null)
                db.Add(link = TitleExternalIdEntity.FromDomain(TitleExternalId.CreateNew(titleId, providerId, game.Id, 1)));
            link.Provider = providerId;
            link.ExternalId = game.Id;
            link.IsConfirmed = true;
            state.GameJson = JsonSerializer.Serialize(game);
            state.SuppressAutomaticMatch = false;
        }
        else
        {
            if (link is null) return ProviderMatchErrors.NotFound;
            link.IsConfirmed = true;
        }
        state.Revision = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success;
    }

    private static Guid Revision(TitleProviderMatchStateEntity? state, TitleExternalIdEntity? link) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{state?.Revision}:{link?.ExternalId}:{link?.IsConfirmed}"))[..16]);
}
