using Romd.Infrastructure.Tests.Helpers;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Search;
using Romd.Contracts.Management.Artwork;
using Romd.Contracts.Management.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Persistence;
using Romd.Persistence.Artwork;
using Romd.Persistence.Entities;
using Romd.Persistence.Enrichment;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;
using EnrichmentRequirementSatisfiedBy = Romd.Contracts.Management.Enrichment.EnrichmentRequirementSatisfiedBy;

namespace Romd.Infrastructure.Tests.Enrichment;

/// <summary>Catalog enrichment flags: AND semantics, Settled exclusivity, and parity with completion scopes.</summary>
public sealed class CatalogEnrichmentFilterTests : IDisposable
{
    private static readonly EnrichmentFlagFilter[] FourFlags =
        [EnrichmentFlagFilter.NeedsReview, EnrichmentFlagFilter.Blocked, EnrichmentFlagFilter.Processing, EnrichmentFlagFilter.Automatic];

    // Seeded title flags. Titles 8 and 10 are untracked.
    private static readonly IReadOnlyDictionary<int, EnrichmentSummaryDto> Seeded = new Dictionary<int, EnrichmentSummaryDto>
    {
        [1] = Flags(needsReview: true, blocked: true, processing: true),
        [2] = Flags(needsReview: true, blocked: true),
        [3] = Flags(processing: true),
        [4] = Flags(),
        [5] = Flags(automatic: true),
        [6] = Flags(needsReview: true, processing: true),
        [7] = Flags(blocked: true),
        [8] = Flags(automatic: true),
        [9] = Flags(blocked: true, automatic: true),
        [10] = Flags(blocked: true, automatic: true)
    };

    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly Guid actor = Guid.NewGuid();
    // SteamGridDB is enabled without credentials, so an open logo request is Blocked.
    private readonly IProviderIdentityAdapter[] adapters = [Provider("igdb", configured: true), Provider("steamgriddb", configured: false)];

    [Fact]
    public async Task Search_OverlappingFlags_ReturnsTitlesCarryingEverySelectedFlag()
    {
        await SeedOverlapAsync();
        await using var db = database.CreateContext();
        var repository = Repository(db);

        var unfiltered = ((await repository.SearchTitlesAsync(null, new TitleSearchFilters(), TitleSortField.Name, null, 50, null)).Value).Items;
        unfiltered.Select(x => x.Id).Order().ShouldBe(Seeded.Keys.Order());
        foreach (var item in unfiltered)
            item.EnrichmentAssessment.ShouldBe(Seeded[item.Id], $"title {item.Id}");

        foreach (var flags in FlagSets())
            (await Ids(repository, new(Enrichment: flags))).ShouldBe(Expected(flags), Label(flags));
        // Title 1 carries NeedsReview, Blocked and Processing: every combination of them matches it, Settled never does.
        foreach (var flags in FlagSets().Where(set => set.All(flag => flag is not (EnrichmentFlagFilter.Automatic or EnrichmentFlagFilter.Settled))))
            (await Ids(repository, new(Enrichment: flags))).ShouldContain(1, Label(flags));
        (await Ids(repository, new(Enrichment: [EnrichmentFlagFilter.NeedsReview, EnrichmentFlagFilter.Blocked, EnrichmentFlagFilter.Processing]))).ShouldBe([1]);
        (await Ids(repository, new(Enrichment: [EnrichmentFlagFilter.NeedsReview, EnrichmentFlagFilter.Processing]))).ShouldBe([1, 6]);
        (await Ids(repository, new(Enrichment: [EnrichmentFlagFilter.Settled]))).ShouldBe([4]);
    }

    [Fact]
    public async Task Search_TextQueryFlagsAndRelevanceSort_ReturnsTheFilteredSetAcrossPages()
    {
        await SeedOverlapAsync();
        await using var db = database.CreateContext();
        var repository = Repository(db);

        // Relevance is the catalog default once a curator types a search; it pages by rank, not by name.
        foreach (var flags in FlagSets())
        {
            var ids = new List<int>();
            string? cursor = null;
            do
            {
                var page = (await repository.SearchTitlesAsync("Title", new(Enrichment: flags), TitleSortField.Relevance, cursor, 2, null)).Value;
                page.Items.ShouldAllBe(item => flags.All(flag => Carries(item.EnrichmentAssessment!, flag)), Label(flags));
                ids.AddRange(page.Items.Select(x => x.Id));
                cursor = page.NextCursor;
            } while (cursor is not null);
            ids.Order().ToArray().ShouldBe(Expected(flags), Label(flags));
            ids.Distinct().Count().ShouldBe(ids.Count, Label(flags));
        }
    }

    [Fact]
    public async Task Search_LegacyLastAttemptStatus_KeepsEqualityAndCombinesWithFlags()
    {
        await SeedOverlapAsync();
        await using var db = database.CreateContext();
        await db.Titles.Where(x => x.Id == 4).ExecuteUpdateAsync(s => s.SetProperty(x => x.EnrichmentStatus, "Completed"));
        await db.Titles.Where(x => x.Id == 2 || x.Id == 7).ExecuteUpdateAsync(s => s.SetProperty(x => x.EnrichmentStatus, "Failed"));
        var repository = Repository(db);

        (await Ids(repository, new(EnrichmentStatus: "Failed"))).ShouldBe([2, 7]);
        (await Ids(repository, new(EnrichmentStatus: "Failed", Enrichment: [EnrichmentFlagFilter.NeedsReview]))).ShouldBe([2]);
        (await Ids(repository, new(EnrichmentStatus: "Failed", Enrichment: [EnrichmentFlagFilter.Settled]))).ShouldBeEmpty();
        (await Ids(repository, new(EnrichmentStatus: "Completed", Enrichment: [EnrichmentFlagFilter.Settled]))).ShouldBe([4]);
        (await Ids(repository, new(EnrichmentStatus: "None"))).ShouldBe([1, 3, 5, 6, 8, 9, 10]);
    }

    [Fact]
    public async Task PreviewScope_FlagSets_SelectCatalogTitlesAndTheirAutomaticSubset()
    {
        await SeedOverlapAsync();
        await using var db = database.CreateContext();
        var repository = Repository(db);
        var availability = await Romd.Admin.Application.Enrichment.EnrichmentProviderAvailability.ReadAsync(adapters, default);
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System, identityAdapters: adapters);
        var service = new ArtworkCompletionService(store);
        var tracked = await db.TrackedTitles.Select(x => x.TitleId).ToListAsync();

        foreach (var flags in FlagSets())
        {
            var catalog = await Ids(repository, new(Enrichment: flags));
            var scoped = await repository.ScopeTitles(null, new(Enrichment: flags), availability).Select(x => x.Id).Order().ToArrayAsync();
            var filters = new ArtworkCompletionFilterDto(Enrichment: flags.Select(flag => flag.ToString()).ToArray());
            var filtered = (await service.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Filtered", Filters: filters, Mode: "Enrich"), default)).Value;
            var automatic = (await service.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Automatic", Filters: filters, Mode: "Enrich"), default)).Value;

            scoped.ShouldBe(catalog, Label(flags));
            filtered.Titles.ShouldBe(catalog.Length, Label(flags));
            automatic.Titles.ShouldBe(catalog.Count(id => tracked.Contains(id) && Seeded[id].CanEnrichAutomatically), Label(flags));
        }
    }

    [Theory]
    [InlineData("Filtered", new[] { "NeedsReview", "Processing" }, new[] { 1, 6 })]
    [InlineData("Filtered", new[] { "Settled" }, new[] { 4 })]
    [InlineData("Filtered", new[] { "Automatic", "Blocked" }, new[] { 9, 10 })]
    [InlineData("Automatic", new[] { "Automatic", "Blocked" }, new[] { 9 })]
    [InlineData("Automatic", new[] { "Blocked" }, new[] { 9 })]
    public async Task QueueScope_EnrichmentFilter_QueuesWhatTheCatalogShows(string scope, string[] flags, int[] expected)
    {
        await SeedOverlapAsync();
        await using var db = database.CreateContext();
        var catalog = await Ids(Repository(db), new(Enrichment: EnrichmentFilterParser.ParseFlags(flags).Value));
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System, identityAdapters: adapters);
        var service = new ArtworkCompletionService(store);

        var job = (await service.GetAsync((await store.AcceptCompletionAsync(db, new(Guid.NewGuid(), Scope: scope, Filters: new(Enrichment: flags), Mode: "Enrich"), actor, default)).Value, default)).Value;

        var queued = await db.Set<ArtworkCompletionItemEntity>().Where(x => x.JobId == job.JobId).OrderBy(x => x.TitleId).Select(x => x.TitleId).ToArrayAsync();
        queued.ShouldBe(expected);
        (scope == "Filtered" ? catalog : catalog.Where(id => id is not (8 or 10) && Seeded[id].CanEnrichAutomatically).ToArray()).ShouldBe(expected);
    }

    [Fact]
    public async Task Search_OnlyOptionalAssetsMissing_IsSettledAndCarriesNoFlag()
    {
        IProviderIdentityAdapter[] configured = [Provider("igdb", configured: true), Provider("steamgriddb", configured: true)];
        await using var db = database.CreateContext();
        db.Platforms.Add(new() { Id = 1, Name = "Game Boy", ShortName = "gb" });
        db.Files.Add(new FileEntityPersistence { Id = 1, Sha256 = Sha256.FromSpan(Enumerable.Repeat((byte)1, 32).ToArray()) });
        db.Titles.Add(new()
        {
            Id = 1,
            PlatformId = 1,
            Name = "Obscure title",
            NormalizedName = "OBSCURE TITLE",
            EnrichmentStatus = "Completed",
            Description = "Curated",
            Genre = "Puzzle",
            ReleaseDate = new DateOnly(1991, 6, 1)
        });
        db.Titles.Add(new() { Id = 2, PlatformId = 1, Name = "Unenriched title", NormalizedName = "UNENRICHED TITLE", EnrichmentStatus = "None" });
        db.TrackedTitles.AddRange(new TrackedTitleEntity { TitleId = 1 }, new TrackedTitleEntity { TitleId = 2 });
        db.TitleExternalIds.AddRange(new TitleExternalIdEntity { TitleId = 1, Provider = "igdb", ExternalId = "42", IsConfirmed = true },
            new TitleExternalIdEntity { TitleId = 1, Provider = "steamgriddb", ExternalId = "43", IsConfirmed = true });
        db.ArtworkAssets.AddRange(Asset(1, ArtworkRole.Poster), Asset(2, ArtworkRole.Hero));
        db.Set<ArtworkAcquisitionEntity>().AddRange(
            new ArtworkAcquisitionEntity { TitleId = 1, Role = ArtworkRole.Logo, Status = "Unavailable", UpdatedAt = DateTimeOffset.UtcNow },
            new ArtworkAcquisitionEntity { TitleId = 1, Role = ArtworkRole.Backdrop, Status = "Unavailable", UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(s => s.SetProperty(x => x.FillPosters, true)
            .SetProperty(x => x.FillHeroes, true).SetProperty(x => x.FillLogos, true).SetProperty(x => x.FillBackdrops, true)
            .SetProperty(x => x.ReviewBackdrops, false));

        var assessment = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: configured).GetAsync(1, default)).Value;
        new EnrichmentSummaryDto(assessment.NeedsReview, assessment.CanEnrichAutomatically, assessment.HasActiveWork, assessment.HasOperationalBlocker)
            .ShouldBe(Flags());
        assessment.Requirements.Where(x => x.Kind == "Artwork").Select(x => (x.Subject, x.SatisfiedBy)).ShouldBe([
            ("Poster", EnrichmentRequirementSatisfiedBy.Present), ("Backdrop", EnrichmentRequirementSatisfiedBy.UnavailableFallback),
            ("Hero", EnrichmentRequirementSatisfiedBy.Present), ("Logo", EnrichmentRequirementSatisfiedBy.UnavailableFallback)]);

        var repository = new SearchRepository(db, new ArtworkReader(db), identityAdapters: configured);
        (await Ids(repository, new())).ShouldBe([1, 2]);
        (await Ids(repository, new(Enrichment: [EnrichmentFlagFilter.Settled]))).ShouldBe([1]);
        foreach (var flag in FourFlags)
            (await Ids(repository, new(Enrichment: [flag]))).ShouldNotContain(1, flag.ToString());
    }

    [Fact]
    public void ScopeTitles_SeveralFlags_EvaluateOneAssessmentOverTheFilteredScope()
    {
        using var db = database.CreateContext();
        var sql = Repository(db).ScopeTitles(null, new(EnrichmentStatus: "Completed", Tracked: TrackedFilter.Tracked,
                Enrichment: [EnrichmentFlagFilter.NeedsReview, EnrichmentFlagFilter.Blocked, EnrichmentFlagFilter.Processing]),
            new(["igdb"], ["igdb"], [])).ToQueryString();

        Regex.Count(sql, "enrichment_requirement_facts_set").ShouldBe(1);
    }

    private SearchRepository Repository(RomdDbContext db) => new(db, new ArtworkReader(db), identityAdapters: adapters);

    private static async Task<int[]> Ids(SearchRepository repository, TitleSearchFilters filters) =>
        ((await repository.SearchTitlesAsync(null, filters, TitleSortField.Name, null, 50, null)).Value).Items.Select(x => x.Id).Order().ToArray();

    private static IEnumerable<EnrichmentFlagFilter[]> FlagSets() =>
        Enumerable.Range(1, 15).Select(mask => FourFlags.Where((_, bit) => (mask & (1 << bit)) != 0).ToArray())
            .Append([EnrichmentFlagFilter.Settled]);

    private static int[] Expected(EnrichmentFlagFilter[] flags) =>
        Seeded.Where(title => flags.All(flag => Carries(title.Value, flag))).Select(title => title.Key).Order().ToArray();

    private static bool Carries(EnrichmentSummaryDto summary, EnrichmentFlagFilter flag) => flag switch
    {
        EnrichmentFlagFilter.NeedsReview => summary.NeedsReview,
        EnrichmentFlagFilter.Blocked => summary.HasOperationalBlocker,
        EnrichmentFlagFilter.Processing => summary.HasActiveWork,
        EnrichmentFlagFilter.Automatic => summary.CanEnrichAutomatically,
        _ => summary == Flags()
    };

    private static string Label(EnrichmentFlagFilter[] flags) => string.Join("+", flags);

    private static EnrichmentSummaryDto Flags(bool needsReview = false, bool blocked = false, bool processing = false, bool automatic = false) =>
        new(needsReview, automatic, processing, blocked);

    private static IProviderIdentityAdapter Provider(string id, bool configured)
    {
        var provider = Substitute.For<IProviderIdentityAdapter>();
        provider.Id.Returns(id);
        provider.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, configured));
        return provider;
    }

    private static ArtworkAssetEntity Asset(int id, ArtworkRole role) => new()
    {
        Id = id,
        TitleId = 1,
        Role = role,
        SourceId = "steamgriddb",
        OriginalFileId = 1,
        ContentVersion = "v1",
        ContentType = "image/webp",
        Width = 600,
        Height = 900,
        IsEligible = true,
        Variants = [new() { Name = "large", FileId = 1, ContentVersion = "large-v1", ContentType = "image/webp", Width = 600, Height = 900 }]
    };

    private async Task SeedOverlapAsync()
    {
        await using var db = database.CreateContext();
        db.Users.Add(new Romd.Persistence.Identity.RomdUser { Id = actor, UserName = "curator", CreatedAt = DateTimeOffset.UtcNow });
        db.Platforms.Add(new() { Id = 1, Name = "System", ShortName = "system" });
        foreach (var id in Seeded.Keys)
        {
            var complete = id is 3 or 4 or 7;
            db.Titles.Add(new()
            {
                Id = id,
                PlatformId = 1,
                Name = $"Title {id}",
                NormalizedName = $"TITLE {id}",
                EnrichmentStatus = "None",
                Description = complete ? "Curated" : null,
                Genre = complete ? "Adventure" : null,
                ReleaseDate = complete ? new DateOnly(1995, 3, 11) : null
            });
            if (id is not (8 or 10))
                db.TrackedTitles.Add(new() { TitleId = id });
            // An empty logo is settled; any other title requests one from the uncredentialed SteamGridDB.
            if (id is 3 or 4 or 5 or 6 or 8)
                db.ArtworkSelections.Add(new() { TitleId = id, Role = ArtworkRole.Logo, Mode = ArtworkSelectionMode.Empty });
            // Continuing without IGDB leaves the missing core fields for the curator.
            if (id is 1 or 2 or 6)
                db.Set<TitleProviderMatchStateEntity>().Add(new() { TitleId = id, ProviderId = "igdb", SuppressAutomaticMatch = true });
            // A pending poster import is active work that leaves every other requirement's state intact.
            if (id is 1 or 3 or 6)
                AddPendingPosterImport(db, id);
        }
        await db.SaveChangesAsync();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(s => s.SetProperty(x => x.FillPosters, false)
            .SetProperty(x => x.FillHeroes, false).SetProperty(x => x.FillLogos, true).SetProperty(x => x.FillBackdrops, false));
    }

    private static void AddPendingPosterImport(RomdDbContext db, int titleId)
    {
        var requestId = Guid.NewGuid();
        var job = ArtworkImportJobEntity.FromDomain(ArtworkImportJob.Create(requestId, $"Title {titleId}", titleId, 1, ArtworkRole.Poster, 1,
            "igdb", "42", "asset", "https://example.com/image", null, TimeProvider.System.GetUtcNow()));
        job.Phase = "Importing";
        db.Add(job);
        db.ArtworkSelections.Add(new() { TitleId = titleId, Role = ArtworkRole.Poster, Mode = ArtworkSelectionMode.Automatic, Revision = 1, PendingRequestId = requestId });
    }

    public void Dispose() => database.Dispose();
}
