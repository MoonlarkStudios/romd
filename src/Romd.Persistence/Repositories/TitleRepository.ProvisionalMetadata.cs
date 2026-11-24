using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;

namespace Romd.Persistence.Repositories;

public sealed partial class TitleRepository
{
    public async Task ApplyProvisionalMetadataStagedAsync(Title title, IReadOnlyList<ProvisionalMetadataResult> results,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        await LockSnapshotAsync(title, cancellationToken);
        foreach (var result in results)
        {
            var match = result.Match;
            if (match is not { IsProvisional: true, ProviderId: "igdb" } || result.Payload.ProviderGameId != match.GameId)
                throw new ArgumentException("The payload must belong to an authorized provisional metadata provider.", nameof(results));

            bool current = await _context.Set<TitleProviderMatchStateEntity>().AnyAsync(state =>
                state.TitleId == title.Id && state.ProviderId == match.ProviderId && state.Revision == match.Revision &&
                ProviderIdentityDatabaseFunctions.ProvisionalMetadataGame(state.TitleId, state.ProviderId,
                    ProviderIdentityPolicy.Version, ProvisionalProviderMatchPolicy.MetadataVersion) == match.GameId,
                cancellationToken);
            if (!current)
                continue;

            var row = await _context.Set<TitleProvisionalMetadataEntity>().AsTracking()
                .SingleOrDefaultAsync(value => value.TitleId == title.Id, cancellationToken);
            if (row is null)
                _context.Add(row = new TitleProvisionalMetadataEntity { TitleId = title.Id });
            row.ProviderId = match.ProviderId;
            row.ProviderGameId = match.GameId;
            row.MatchRevision = match.Revision;
            row.MetadataJson = JsonSerializer.Serialize(result.Payload);
            var ratings = (result.Payload.ContentRatings ?? [])
                .Select(claim => RatingBoardCatalog.TryResolve(claim, match.ProviderId))
                .OfType<ContentRating>()
                .DistinctBy(rating => rating.Board)
                .Select(rating => TitleContentRatingEntity.FromDomain(title.Id, rating))
                .Select(rating => new
                {
                    rating.Board,
                    rating.Code,
                    rating.Designation,
                    rating.MinimumAge,
                    rating.DescriptorsJson,
                    rating.Synopsis,
                    rating.SourceId,
                    rating.ExternalRatingId
                });
            row.RatingsJson = JsonSerializer.Serialize(ratings);
        }
    }
}
