using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;

namespace Romd.Persistence.MetadataProviders;

public sealed partial class TitleProviderMatchStore
{
    public async Task<ProviderMatchAuthorizationResult?> ReadAuthorizationAsync(int titleId, string providerId, CancellationToken ct)
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
                                 ProvisionalGameId = providerId == "steamgriddb"
                                     ? ProviderIdentityDatabaseFunctions.ProvisionalGame(titleId, providerId,
                                         ProviderIdentityPolicy.Version, ProvisionalProviderMatchPolicy.Version)
                                     : null
                             }).SingleOrDefaultAsync(ct);
        if (current is null)
            return null;
        if (current.State?.SuppressAutomaticMatch == true)
            return new ProviderMatchAuthorizationResult.Blocked(ProviderMatchBlock.Unlinked);
        if (current is { Trusted: true, Link: { } association })
            return new ProviderMatchAuthorizationResult.Authorized(new(providerId, association.ExternalId, Revision(current.State, association)));

        if (current is { ProvisionalGameId: { } candidate, State: { } matchState })
            return new ProviderMatchAuthorizationResult.Authorized(new(providerId, candidate, matchState.Revision, IsProvisional: true));

        var block = current is { Link: null, State.Evidence: "Provider search temporarily unavailable." }
            ? ProviderMatchBlock.Failed
            : ProviderMatchBlock.NeedsMatch;
        return new ProviderMatchAuthorizationResult.Blocked(block);
    }
}
