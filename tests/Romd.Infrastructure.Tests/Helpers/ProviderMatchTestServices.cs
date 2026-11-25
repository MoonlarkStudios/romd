using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Persistence;
using Romd.Persistence.MetadataProviders;
using Romd.Persistence.Repositories;

namespace Romd.Infrastructure.Tests.Helpers;

internal static class ProviderMatchTestServices
{
    public static TitleProviderMatchService Create(RomdDbContext db, IEnumerable<IProviderIdentityAdapter> adapters,
        IProviderIdentityService? identities = null, IProviderIdentityStore? identityStore = null,
        IArtworkCompletionStore? completion = null,
        IMetadataRematerializer? rematerializer = null)
    {
        var titles = new TitleRepository(db);
        rematerializer ??= new TitleMetadataRematerializer(new PlatformFieldDefaultRepository(db),
            Options.Create(new EnrichmentOptions()));
        var contributions = new ProviderContributionRematerializer(titles, rematerializer, titles, new LibraryRepository(db));
        return new TitleProviderMatchService(new TitleProviderMatchStore(db, clock: TimeProvider.System), adapters,
            new EfUnitOfWork(db), TimeProvider.System, NullLogger<TitleProviderMatchService>.Instance,
            contributions, identities, identityStore, completion);
    }
}
