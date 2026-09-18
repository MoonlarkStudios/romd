using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Taxonomy;
using Romd.Domain.Source.Platform;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.Taxonomy;
using Romd.Persistence;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed class BundledReferenceDataTests
{
    [Fact]
    public async Task Seeders_FreshDatabase_InitializeEverySharedRecordAndAliasOffline()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var services = Services(database.ConnectionString);
        await SeedAsync(services);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var platforms = await db.Platforms.ToDictionaryAsync(p => p.ShortName);
        platforms.Count.ShouldBe(56);
        (await db.Regions.CountAsync()).ShouldBe(20);
        (await db.GameLanguages.CountAsync()).ShouldBe(16);
        BundledReferenceData.Document.GetProperty("catalogVersion").GetInt32().ShouldBe(1);
        var companies = BundledReferenceData.Category("companies");
        var platformAliases = await db.PlatformAliases.ToListAsync();
        foreach (var entry in BundledReferenceData.Category("systems").EnumerateObject())
        {
            var platform = platforms[entry.Name];
            platform.Name.ShouldBe(entry.Value.GetProperty("name").GetString());
            var manufacturers = entry.Value.GetProperty("manufacturerIds").EnumerateArray()
                .Select(id => companies.GetProperty(id.GetString()!).GetProperty("name").GetString()).ToArray();
            platform.Manufacturer.ShouldBe(manufacturers.Length == 0 ? null : string.Join(", ", manufacturers));
            var aliases = platformAliases.Where(a => a.PlatformId == platform.Id).ToArray();
            foreach (var alias in entry.Value.GetProperty("aliases").EnumerateArray())
                aliases.ShouldContain(a => a.NormalizedValue == alias.GetString()!.ToLowerInvariant()
                    && a.Type == PlatformAliasType.Name);
            foreach (var mapping in entry.Value.GetProperty("providerMappings").EnumerateObject())
                aliases.ShouldContain(a => a.Provider == mapping.Name && a.Value == mapping.Value.GetString()
                    && a.Type == PlatformAliasType.ProviderMapping);
        }
        var regions = scope.ServiceProvider.GetRequiredService<ITaxonomyRepository<Region>>();
        foreach (var entry in BundledReferenceData.Category("regions").EnumerateObject())
        {
            var name = entry.Value.GetProperty("name").GetString()!;
            var row = (await regions.GetAllAsync()).Single(r => r.Name == name);
            row.SortOrder.ShouldBe(entry.Value.GetProperty("sortOrder").GetInt32());
            (await regions.GetAliasesAsync(row.Id)).Select(a => a.Alias).Order()
                .ShouldBe(entry.Value.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()!)
                    .Append(name).Select(a => a.ToLowerInvariant()).Distinct().Order());
        }
        var languages = scope.ServiceProvider.GetRequiredService<ITaxonomyRepository<GameLanguage>>();
        foreach (var entry in BundledReferenceData.Category("languages").EnumerateObject())
        {
            var row = (await languages.GetAllAsync()).Single(l => l.Code == entry.Name);
            row.Name.ShouldBe(entry.Value.GetProperty("name").GetString());
            row.SortOrder.ShouldBe(entry.Value.GetProperty("sortOrder").GetInt32());
            (await languages.GetAliasesAsync(row.Id)).Select(a => a.Alias).Order()
                .ShouldBe(entry.Value.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()!)
                    .Append(row.Name).Append(row.Code).Select(a => a.ToLowerInvariant()).Distinct().Order());
        }
    }

    [Fact]
    public async Task Seeders_ExistingCustomizedRows_PreserveIdsAndLabels_ReconcileRemovedImporterAliases()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var services = Services(database.ConnectionString);
        await SeedAsync(services);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var psx = await db.Platforms.SingleAsync(p => p.ShortName == "psx");
        var english = await db.GameLanguages.SingleAsync(l => l.Code == "en");
        var catalog = new ReferenceCatalogService(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var system = (await new TypedReferenceTestCommands(db).System("psx", default))!;
        (await new TypedReferenceTestCommands(db).UpdateSystem("psx", new SystemPatchDto { Name = "My PlayStation" }, system.ETag, default)).IsError.ShouldBeFalse();
        await db.PlatformAliases.Where(a => a.PlatformId == psx.Id && a.NormalizedValue == "ps1").ExecuteDeleteAsync();
        await db.GameLanguageAliases.Where(a => a.GameLanguageId == english.Id && a.NormalizedAlias == "eng").ExecuteDeleteAsync();
        await SeedAsync(services);
        (await db.Platforms.SingleAsync(p => p.ShortName == "psx")).Id.ShouldBe(psx.Id);
        (await db.Platforms.SingleAsync(p => p.ShortName == "psx")).Name.ShouldBe("My PlayStation");
        (await db.GameLanguages.SingleAsync(l => l.Code == "en")).Id.ShouldBe(english.Id);
        (await db.GameLanguages.SingleAsync(l => l.Code == "en")).Name.ShouldBe(english.Name);
        (await db.PlatformAliases.AnyAsync(a => a.PlatformId == psx.Id && a.NormalizedValue == "ps1")).ShouldBeTrue();
        (await db.GameLanguageAliases.AnyAsync(a => a.GameLanguageId == english.Id && a.NormalizedAlias == "eng")).ShouldBeTrue();
    }

    private static ServiceProvider Services(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Romd.Application.Common.ReferenceCatalog.IReferenceBlobStore>(Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        services.AddDbContext<RomdDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IPlatformRepository, PlatformRepository>();
        services.AddScoped<IPlatformAliasRepository, PlatformAliasRepository>();
        services.AddScoped<ITaxonomyRepository<Region>, RegionRepository>();
        services.AddScoped<ITaxonomyRepository<GameLanguage>, LanguageRepository>();
        return services.BuildServiceProvider();
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        await new SharedReferenceDataSeeder(services, NullLogger<SharedReferenceDataSeeder>.Instance).StartAsync(default);
    }
}
