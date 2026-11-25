using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Persistence.Entities;

namespace Romd.Persistence.MetadataProviders;

public sealed partial class TitleProviderMatchStore
{
    private async Task<IReadOnlyList<ProviderMatchState>> ReadMatchStatesAsync(int titleId, string[] providerIds, CancellationToken ct)
    {
        // Read the evidence, exact draft, and current contributions in one PostgreSQL statement.
        // Both the preview and the match revision use the same evidence result.
        var states = await (
            from state in _db.Set<TitleProviderMatchStateEntity>()
            join title in _db.Titles on state.TitleId equals title.Id
            where title.Id == titleId && providerIds.Contains(state.ProviderId)
            let gameId = state.ProviderId == "steamgriddb"
                ? ProviderIdentityDatabaseFunctions.ProvisionalGame(titleId, state.ProviderId,
                    ProviderIdentityPolicy.Version, ProvisionalProviderMatchPolicy.Version)
                : state.ProviderId == "igdb"
                    ? ProviderIdentityDatabaseFunctions.ProvisionalMetadataGame(titleId, state.ProviderId,
                        ProviderIdentityPolicy.Version, ProvisionalProviderMatchPolicy.MetadataVersion)
                    : null
            let draftJson = _db.Set<TitleProvisionalMetadataEntity>()
                .Where(draft => draft.TitleId == titleId && draft.ProviderId == state.ProviderId &&
                    draft.MatchRevision == state.Revision && draft.ProviderGameId == gameId)
                .Select(draft => draft.MetadataJson).FirstOrDefault()
            select new
            {
                State = state,
                GameId = gameId,
                DraftJson = draftJson,
                Published = new ProvisionalMetadataFields(title.Description, title.Publisher, title.Developer,
                    title.Genre, title.ReleaseDate, title.Players, title.Rating),
                Ratings = _db.PresentedRatings()
                    .Where(rating => draftJson != null && rating.TitleId == titleId && rating.SourceId == state.ProviderId &&
                        !_db.TitleContentRatings.Any(published => published.TitleId == titleId && published.Board == rating.Board))
                    .OrderBy(rating => rating.Board)
                    .Select(rating => new ProvisionalMatchRating((RatingBoard)rating.Board, rating.Code)).ToList(),
                Artwork = _db.ArtworkAssets
                    .Where(asset => gameId != null && asset.TitleId == titleId && asset.SourceId == state.ProviderId &&
                        asset.ProviderGameId == gameId && asset.IsProvisional && !asset.IsRejected)
                    .OrderByDescending(asset => asset.CreatedAt).ThenByDescending(asset => asset.Id)
                    .Select(asset => new
                    {
                        asset.Role,
                        asset.Id,
                        Preview = asset.Variants.OrderBy(variant => (long)variant.Width * variant.Height)
                            .ThenBy(variant => variant.Name).FirstOrDefault()
                    }).ToList()
            }).AsSingleQuery().ToListAsync(ct);

        return states.Select(row =>
        {
            var state = row.State;
            var decision = state.DecisionJson is { } json ? JsonSerializer.Deserialize<ProviderIdentityDecision>(json) : null;
            ProvisionalMatchReview? review = null;
            if (decision?.ProvisionalGameId is { } proposedGameId)
            {
                review = row.GameId == proposedGameId
                    ? new(proposedGameId, true, EffectiveFields(row.DraftJson, row.Published), row.Ratings,
                        row.Artwork.DistinctBy(asset => asset.Role).OrderBy(asset => asset.Role)
                            .Select(asset => new ProvisionalMatchArtwork(asset.Role, asset.Id, asset.Preview?.Name, asset.Preview?.ContentVersion)).ToArray())
                    : new(proposedGameId, false, null, [], []);
            }
            return new ProviderMatchState(state.ProviderId, state.Revision, state.SuppressAutomaticMatch, state.IdentityChanged,
                state.Evidence, state.MatcherVersion, state.GameJson is { } game ? JsonSerializer.Deserialize<ProviderGame>(game) : null,
                decision is null ? null : decision with { ProvisionalGameId = row.GameId }, review);
        }).ToArray();
    }

    private static ProvisionalMetadataFields? EffectiveFields(string? draftJson, ProvisionalMetadataFields published)
    {
        if (draftJson is null || JsonSerializer.Deserialize<TitleMetadataPayload>(draftJson) is not { } draft)
            return null;

        return new(published.Description is null ? draft.Description : null,
            published.Publisher is null ? draft.Publisher : null,
            published.Developer is null ? draft.Developer : null,
            published.Genre is null ? draft.Genre : null,
            published.ReleaseDate is null ? draft.ReleaseDate : null,
            published.Players is null ? draft.Players : null,
            published.Rating is null ? draft.Rating : null);
    }
}
