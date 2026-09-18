using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Search;
using Romd.Domain.Catalog;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>
///     Verifies the catalog search tracked filter and that <c>IsTracked</c> is projected onto rows.
/// </summary>
public sealed class SearchRepositoryTrackedFilterTests : IDisposable
{
    private const int PlatformId = 1;
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public SearchRepositoryTrackedFilterTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
        SeedTitles(db);
    }

    [Fact]
    public async Task SearchTitlesAsync_TrackedFilter_ReturnsOnlyTrackedRows()
    {
        using var db = CreateDb();
        var repository = new SearchRepository(db);

        var result = await SearchAsync(repository, TrackedFilter.Tracked);

        result.Items.Select(i => i.Id).OrderBy(id => id).ShouldBe([1]);
        result.Items.ShouldAllBe(i => i.IsTracked);
    }

    [Fact]
    public async Task SearchTitlesAsync_UntrackedFilter_ReturnsOnlyUntrackedRows()
    {
        using var db = CreateDb();
        var repository = new SearchRepository(db);

        var result = await SearchAsync(repository, TrackedFilter.Untracked);

        result.Items.Select(i => i.Id).OrderBy(id => id).ShouldBe([2]);
        result.Items.ShouldAllBe(i => !i.IsTracked);
    }

    [Fact]
    public async Task SearchTitlesAsync_AllFilter_ReturnsBoth_WithIsTrackedProjected()
    {
        using var db = CreateDb();
        var repository = new SearchRepository(db);

        var result = await SearchAsync(repository, TrackedFilter.All);

        result.Items.Select(i => i.Id).OrderBy(id => id).ShouldBe([1, 2]);
        result.Items.Single(i => i.Id == 1).IsTracked.ShouldBeTrue();
        result.Items.Single(i => i.Id == 2).IsTracked.ShouldBeFalse();
    }

    [Fact]
    public async Task SearchTitlesAsync_ImportOnlyPayload_ProjectsCanonicalFactOutsideDatCompletenessBuckets()
    {
        using var db = CreateDb();
        var repository = new SearchRepository(db);

        var all = await SearchAsync(repository, TrackedFilter.All, ReleaseCompletenessFilter.All);
        var importOnly = all.Items.Single(item => item.Id == 1);
        importOnly.HasLocalPayload.ShouldBeTrue();
        importOnly.TotalVersionCount.ShouldBe(0);
        importOnly.LocalPayloadVersionCount.ShouldBe(0);

        foreach (var releaseCompleteness in new[]
                 {
                     ReleaseCompletenessFilter.Complete,
                     ReleaseCompletenessFilter.Partial,
                     ReleaseCompletenessFilter.None
                 })
        {
            var filtered = await SearchAsync(repository, TrackedFilter.All, releaseCompleteness);
            filtered.Items.ShouldNotContain(item => item.Id == importOnly.Id,
                "provider-neutral title availability must not be classified as DAT release completeness");
        }
    }

    [Fact]
    public async Task GetCatalogFiltersAsync_RunsConcurrentAggregatesOnSharedContext_WithoutThrowing()
    {
        // GetCatalogFiltersAsync issues eight aggregations sequentially over one DbContext (the
        // scoped context is single-flight); this locks in that they compose without throwing.
        using var db = CreateDb();
        var repository = new SearchRepository(db);

        var filters = await repository.GetCatalogFiltersAsync();

        filters.ShouldNotBeNull();
        filters.Platforms.ShouldContain(f => f.Value == "Super Nintendo Entertainment System" && f.Count == 2);
        filters.EnrichmentStatuses.ShouldContain(f => f.Count == 2);
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static Task<Romd.Application.Common.Pagination.PagedList<Romd.Admin.Application.Search.ReadModels.CatalogTitleData>>
        SearchAsync(
            SearchRepository repository,
            TrackedFilter tracked,
            ReleaseCompletenessFilter releaseCompleteness = ReleaseCompletenessFilter.All) =>
        repository.SearchTitlesAsync(
            query: null,
            filters: new TitleSearchFilters(
                Tracked: tracked,
                ReleaseCompleteness: releaseCompleteness),
            sortField: TitleSortField.Name,
            cursor: null,
            limit: 50,
            libraryId: null);

    private static void SeedTitles(RomdDbContext db)
    {
        db.Platforms.Add(new PlatformEntity
        {
            Id = PlatformId,
            Name = "Super Nintendo Entertainment System",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo Entertainment System", BaseCompactLabel = "Super Nintendo Entertainment System", CanonicalKey = "snes", ShortName = "snes",
            Manufacturer = "Nintendo",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = Guid.NewGuid()
        });

        // Import-only availability: no DAT graph exists, but the provider-neutral projection says
        // local payload is present.
        AddTitle(db, id: 1, hasLocalPayload: true);
        AddTitle(db, id: 2);
        db.TrackedTitles.Add(new TrackedTitleEntity
        {
            TitleId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    private static void AddTitle(RomdDbContext db, int id, bool hasLocalPayload = false)
    {
        db.Titles.Add(new TitleEntity
        {
            Id = id,
            PlatformId = PlatformId,
            HasLocalPayload = hasLocalPayload,
            Name = $"Title {id}",
            NormalizedName = $"title{id}",
            EnrichmentStatus = EnrichmentStatus.None.ToString(),
            FieldProvenanceJson = "{}",
            FieldSourceOverridesJson = "{}",
            ScreenshotPrefsJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = Guid.NewGuid()
        });
    }
}
