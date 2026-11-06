using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.MetadataProviders;
using Romd.Persistence;
using Romd.Persistence.MetadataProviders;

namespace Romd.Infrastructure.Tests.Helpers;

internal static class ProviderMatchTestServices
{
    public static TitleProviderMatchService Create(RomdDbContext db, IEnumerable<IProviderIdentityAdapter> adapters,
        IProviderIdentityService? identities = null, IProviderIdentityStore? identityStore = null,
        IArtworkCompletionStore? completion = null) =>
        new(new TitleProviderMatchStore(db, clock: TimeProvider.System), adapters,
            new EfUnitOfWork(db), TimeProvider.System, NullLogger<TitleProviderMatchService>.Instance,
            identities, identityStore, completion);
}
