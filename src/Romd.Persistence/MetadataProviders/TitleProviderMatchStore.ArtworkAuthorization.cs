using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;

namespace Romd.Persistence.MetadataProviders;

public sealed partial class TitleProviderMatchStore
{
    public async Task<ArtworkMatchResolution?> ReadAsync(int titleId, string providerId, ArtworkRole role, CancellationToken ct)
    {
        var current = await (from title in _db.Titles
                             where title.Id == titleId
                             let link = _db.TitleExternalIds.SingleOrDefault(x => x.TitleId == title.Id && x.Provider.ToLower() == providerId)
                             let state = _db.Set<TitleProviderMatchStateEntity>().FirstOrDefault(x => x.TitleId == title.Id && x.ProviderId == providerId)
                             select new
                             {
                                 Link = link,
                                 State = state,
                                 Trusted = ProviderIdentityDatabaseFunctions.IsTrusted(titleId, providerId, ProviderIdentityPolicy.Version),
                                 ProvisionalGameId = role == ArtworkRole.Poster && providerId == "steamgriddb"
                                     ? ProviderIdentityDatabaseFunctions.ProvisionalPosterGame(titleId, providerId,
                                         ProviderIdentityPolicy.Version, ProvisionalPosterPolicy.Version)
                                     : null
                             }).SingleOrDefaultAsync(ct);
        if (current is null)
            return null;
        if (current.State?.SuppressAutomaticMatch == true)
            return new ArtworkMatchResolution.Blocked(ArtworkMatchBlock.Unlinked);
        if (current is { Trusted: true, Link: { } association })
            return new ArtworkMatchResolution.Authorized(new(providerId, association.ExternalId, role, Revision(current.State, association)));

        if (current is { ProvisionalGameId: { } candidate, State: { } matchState })
            return new ArtworkMatchResolution.Authorized(new(providerId, candidate, role, matchState.Revision, IsProvisional: true));

        var block = current is { Link: null, State.Evidence: "Provider search temporarily unavailable." }
            ? ArtworkMatchBlock.Failed
            : ArtworkMatchBlock.NeedsMatch;
        return new ArtworkMatchResolution.Blocked(block);
    }
}
