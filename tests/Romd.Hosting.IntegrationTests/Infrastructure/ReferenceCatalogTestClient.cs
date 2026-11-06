using System.Net.Http.Json;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Shouldly;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

internal static class ReferenceCatalogTestClient
{
    internal static async Task<ReferenceCatalogDto> ReadCatalogAsync(this HttpClient client)
    {
        var descriptor = (await client.GetFromJsonAsync<ReferenceCatalogDescriptorDto>("/api/catalog-snapshot"))!;
        var root = "/api/catalog-snapshot/";
        var pin = "?revision=" + descriptor.CatalogRevision;
        var catalog = new ReferenceCatalogDto
        {
            SchemaVersion = descriptor.SchemaVersion,
            BuiltInVersion = descriptor.BuiltInVersion,
            Revision = descriptor.CatalogRevision,
            Systems = await TestHelpers.ReadAllPageItemsAsync<ReferenceSystemDto>(client, root + "systems" + pin),
            Companies = await TestHelpers.ReadAllPageItemsAsync<ReferenceNamedDto>(client, root + "companies" + pin),
            Regions = await TestHelpers.ReadAllPageItemsAsync<ReferenceTaxonomyDto>(client, root + "regions" + pin),
            Languages = await TestHelpers.ReadAllPageItemsAsync<ReferenceTaxonomyDto>(client, root + "languages" + pin),
            RatingBoards = await TestHelpers.ReadAllPageItemsAsync<ReferenceRatingBoardDto>(client, root + "rating-boards" + pin),
            Ratings = await TestHelpers.ReadAllPageItemsAsync<ReferenceRatingDto>(client, root + "ratings" + pin)
        };
        ReferenceCatalogCanonicalJson.Describe(catalog).ShouldBe(descriptor);
        return catalog;
    }
}
