using System.Net.Http.Json;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Shouldly;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

internal static class ReferenceCatalogTestClient
{
    internal static async Task<ReferenceCatalog> ReadCatalogAsync(this HttpClient client)
    {
        var descriptor = (await client.GetFromJsonAsync<ReferenceCatalogDescriptor>("/api/catalog-snapshot"))!;
        var root = "/api/catalog-snapshot/";
        var pin = "?revision=" + descriptor.CatalogRevision;
        var catalog = new ReferenceCatalog
        {
            SchemaVersion = descriptor.SchemaVersion,
            BuiltInVersion = descriptor.BuiltInVersion,
            Revision = descriptor.CatalogRevision,
            Systems = await TestHelpers.ReadAllPageItemsAsync<ReferenceSystem>(client, root + "systems" + pin),
            Companies = await TestHelpers.ReadAllPageItemsAsync<ReferenceNamed>(client, root + "companies" + pin),
            Regions = await TestHelpers.ReadAllPageItemsAsync<ReferenceTaxonomy>(client, root + "regions" + pin),
            Languages = await TestHelpers.ReadAllPageItemsAsync<ReferenceTaxonomy>(client, root + "languages" + pin),
            RatingBoards = await TestHelpers.ReadAllPageItemsAsync<ReferenceRatingBoard>(client, root + "rating-boards" + pin),
            Ratings = await TestHelpers.ReadAllPageItemsAsync<ReferenceRating>(client, root + "ratings" + pin)
        };
        ReferenceCatalogCanonicalJson.Describe(catalog).ShouldBe(descriptor);
        return catalog;
    }
}
