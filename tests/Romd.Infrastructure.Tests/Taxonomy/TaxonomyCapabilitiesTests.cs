using Microsoft.EntityFrameworkCore;
using Romd.Domain.ReferenceData;
using Romd.Persistence.Entities;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Taxonomy;

public sealed class TaxonomyCapabilitiesTests
{
    [Fact]
    public async Task Lists_DistinguishRegisteredIdentitiesAndAliasAuthority()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.Instance);
        var region = await db.Regions.SingleAsync(x => x.CanonicalKey == "asia");
        var language = await db.GameLanguages.SingleAsync(x => x.CanonicalKey == "en");
        db.Regions.Add(new RegionEntity { Name = "Unregistered region", IsAutoCreated = true });
        db.GameLanguages.Add(new GameLanguageEntity { Name = "Unregistered language", Code = "unknown", IsAutoCreated = true });
        db.RegionAliases.Add(new RegionAliasEntity { RegionId = region.Id, NormalizedAlias = "my-region", Ownership = ReferenceOwnership.Installation });
        db.RegionAliases.Add(new RegionAliasEntity { RegionId = region.Id, NormalizedAlias = "old-region", Ownership = null });
        db.GameLanguageAliases.Add(new GameLanguageAliasEntity { GameLanguageId = language.Id, NormalizedAlias = "my-language", Ownership = ReferenceOwnership.Installation });
        db.GameLanguageAliases.Add(new GameLanguageAliasEntity { GameLanguageId = language.Id, NormalizedAlias = "old-language", Ownership = null });
        await db.SaveChangesAsync();
        var regions = await new RegionRepository(db).GetAllWithAliasesAsync();
        var languages = await new LanguageRepository(db).GetAllWithAliasesAsync();
        regions.Single(x => x.Entity.Id == region.Id).CanMerge.ShouldBeFalse();
        languages.Single(x => x.Entity.Id == language.Id).CanMerge.ShouldBeFalse();
        regions.Single(x => x.Entity.Name == "Unregistered region").CanMerge.ShouldBeTrue();
        languages.Single(x => x.Entity.Code == "unknown").CanMerge.ShouldBeTrue();
        foreach (var aliases in new[] { regions.Single(x => x.Entity.Id == region.Id).Aliases, languages.Single(x => x.Entity.Id == language.Id).Aliases })
        {
            aliases.ShouldContain(x => x.Ownership == "Romd");
            aliases.ShouldContain(x => x.Ownership == "Installation");
            aliases.ShouldContain(x => x.Ownership == "Unresolved");
        }
    }
}
