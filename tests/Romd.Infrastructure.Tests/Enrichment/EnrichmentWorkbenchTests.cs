using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.MetadataProviders;
using Romd.Application.Common.Artwork;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Ids;
using Romd.Domain.Hashing;
using Romd.Contracts.Management.Enrichment;
using Romd.Contracts.Common.Models;
using Romd.Application.Common.Pagination;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Tests.Enrichment.Helpers;
using Romd.Persistence.Entities;
using Romd.Persistence.Enrichment;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;
using EnrichmentRequirementCause = Romd.Contracts.Management.Enrichment.EnrichmentRequirementCause;
using EnrichmentRequirementSatisfiedBy = Romd.Contracts.Management.Enrichment.EnrichmentRequirementSatisfiedBy;
using ContractEnrichmentRequirement = Romd.Contracts.Management.Enrichment.EnrichmentRequirement;

using EnrichmentRequirement = Romd.Domain.Catalog.EnrichmentRequirement;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class EnrichmentWorkbenchTests : IDisposable
{
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly IProviderIdentityAdapter adapter = Substitute.For<IProviderIdentityAdapter>();
    private readonly Xunit.Abstractions.ITestOutputHelper output;
    public EnrichmentWorkbenchTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        this.output = output;
        adapter.Id.Returns("igdb");
        adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
    }
    public void Dispose() => database.Dispose();

    [Fact]
    public async Task Read_ArtworkThumbnails_UseCuratedSmallVariantAndRespectEmptyAndLegacyCover()
    {
        await Seed(4);
        await using var db = database.CreateContext();
        await db.Platforms.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompactLabelOverride, "SYS"));
        db.Files.Add(new FileEntityPersistence { Id = 1, Sha256 = Sha256.FromSpan(Enumerable.Repeat((byte)1, 32).ToArray()) });
        db.ArtworkAssets.Add(new ArtworkAssetEntity
        {
            Id = 1,
            TitleId = 1,
            Role = ArtworkRole.Poster,
            SourceId = "user",
            OriginalFileId = 1,
            ContentVersion = "original",
            ContentType = "image/webp",
            Width = 1200,
            Height = 1600,
            IsEligible = true,
            Variants = [
                new() { Name = "large", FileId = 1, ContentVersion = "large-v1", ContentType = "image/webp", Width = 600, Height = 800 },
                new() { Name = "small", FileId = 1, ContentVersion = "small-v1", ContentType = "image/webp", Width = 120, Height = 160 }
            ]
        });
        db.ArtworkSelections.Add(new() { TitleId = 1, Role = ArtworkRole.Poster, Mode = ArtworkSelectionMode.Pinned, PinnedAssetId = 1 });
        db.ArtworkSelections.Add(new() { TitleId = 2, Role = ArtworkRole.Poster, Mode = ArtworkSelectionMode.Empty });
        db.TitleMedia.AddRange(
            new TitleMediaEntity { Id = 2, TitleId = 2, Type = "Cover", FileId = 1, SourceId = "igdb" },
            new TitleMediaEntity { Id = 3, TitleId = 3, Type = "Cover", FileId = 1, SourceId = "igdb" });
        await db.SaveChangesAsync();
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]);

        var page = (await workbench.ReadAsync("All", null, 4, null, default)).Value;

        page.Items[0].ThumbnailUrl.ShouldBe($"/artwork/{IdCoder.Encode(1)}/small/small-v1");
        page.Items[1].ThumbnailUrl.ShouldBeNull();
        page.Items[2].ThumbnailUrl.ShouldBe($"/media/{IdCoder.Encode(3)}");
        page.Items[3].ThumbnailUrl.ShouldBeNull();
        page.Items.ShouldAllBe(item => item.PlatformCompactLabel == "SYS");
        (await workbench.GetAsync(1, default)).Value.ThumbnailUrl.ShouldBe(page.Items[0].ThumbnailUrl);
    }

    [Fact]
    public async Task Read_ArtworkResolution_IsBatchedToPageAndSkippedForCounts()
    {
        await Seed(4);
        await using var db = database.CreateContext();
        var artwork = Substitute.For<IArtworkReader>();
        artwork.ResolveAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, IReadOnlyList<ArtworkResolution>>());
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, artwork, adapters: [adapter]);

        await workbench.ReadCountsAsync(null, default);
        await artwork.DidNotReceive().ResolveAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
        await workbench.ReadAsync("All", null, 2, null, default);
        await artwork.Received(1).ResolveAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 1, 2 })), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(EnrichmentIssueState.Automatic)]
    [InlineData(EnrichmentIssueState.NeedsReview)]
    [InlineData(EnrichmentIssueState.Blocked)]
    [InlineData(EnrichmentIssueState.Processing)]
    public async Task Get_CurrentRequirementState_IsExplicitInBothDetailAndQueue(EnrichmentIssueState expected)
    {
        await Seed(1);
        await using var db = database.CreateContext();
        if (expected == EnrichmentIssueState.Blocked)
            adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, false));
        if (expected == EnrichmentIssueState.NeedsReview)
            db.Set<TitleProviderMatchStateEntity>().Add(new() { TitleId = 1, ProviderId = "igdb", SuppressAutomaticMatch = true });
        if (expected == EnrichmentIssueState.Processing)
            db.Add(new EnrichmentJobEntity
            {
                Id = Guid.NewGuid(),
                TitleId = 1,
                PlatformId = 1,
                Phase = "Enriching",
                SourceFilename = "Title 1",
                ErrorsJson = "[]",
                JobType = "enrichment"
            });
        await db.SaveChangesAsync();

        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]);
        var detail = (await workbench.GetAsync(1, default)).Value;
        Open(detail).ShouldNotBeEmpty();
        Open(detail).ShouldAllBe(issue => issue.State == expected);
        var page = (await workbench.ReadAsync("All", null, 50, null, default)).Value;
        Open(page.Items.ShouldHaveSingleItem()).ShouldAllBe(issue => issue.State == expected);
    }

    [Fact]
    public async Task Read_NoJobHistory_AssessesTrackedCatalogAndPagesBeforeHydration()
    {
        await Seed(4);
        await using var db = database.CreateContext();
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]);
        var first = (await workbench.ReadAsync("Automatic", null, 2, null, default)).Value;
        var stats = (await workbench.ReadCountsAsync(null, default)).Value;
        stats.Total.ShouldBe(4);
        stats.Automatic.ShouldBe(4);
        first.Items.Count.ShouldBe(2);
        first.NextCursor.ShouldNotBeNull();
        var second = (await workbench.ReadAsync("Automatic", first.NextCursor, 2, null, default)).Value;
        second.Items.Count.ShouldBe(2);
        second.NextCursor.ShouldBeNull();
        second.Items.Select(x => x.TitleId).Intersect(first.Items.Select(x => x.TitleId)).ShouldBeEmpty();
        (await db.Set<JobEntity>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" Title 1 ")]
    [InlineData("missing")]
    public async Task ReadCounts_SearchScope_AgreesWithCountedPage(string? query)
    {
        await Seed(4);
        await using var db = database.CreateContext();
        await db.TrackedTitles.Where(x => x.TitleId == 4).ExecuteDeleteAsync();
        db.Add(new EnrichmentJobEntity
        {
            Id = Guid.NewGuid(),
            TitleId = 1,
            PlatformId = 1,
            Phase = "Enriching",
            SourceFilename = "Title 1",
            ErrorsJson = "[]",
            JobType = "enrichment"
        });
        await db.SaveChangesAsync();
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]);
        var counts = (await workbench.ReadCountsAsync(query, default)).Value;
        var page = (await workbench.ReadAsync("All", null, 1, query, default)).Value;
        page.Items.Count.ShouldBe(Math.Min(1, counts.Total));
        page.HasNextPage.ShouldBe(counts.Total > 1);
        if (query == "missing")
            counts.ShouldBe(new EnrichmentWorkbenchCounts(0, 0, 0, 0, 0));
        if (query is null)
        {
            counts.Total.ShouldBe(3);
            counts.Automatic.ShouldBe(2);
            counts.Processing.ShouldBe(1);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(1024)]
    [InlineData(1025)]
    public async Task ReadCounts_WholeAndPartialArrays_CountEveryScopedTitleOnce(int count)
    {
        await Seed(count);
        await using var db = database.CreateContext();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET plan_cache_mode = force_generic_plan");
        var actual = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter])
            .ReadCountsAsync(null, default)).Value;
        actual.ShouldBe(new EnrichmentWorkbenchCounts(count, 0, count, 0, 0));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("force_generic_plan")]
    public async Task ReadCounts_DisjointSignedAndSparseRanges_PreserveCanonicalRequirementsAndFlags(string planMode)
    {
        await Seed(0);
        await using var db = database.CreateContext();
        int[] ids = Enumerable.Range(1, 1025).Where(id => id != 510)
            .Concat([int.MinValue, -513, -512, -511, -1, 1_000_000_001, int.MaxValue]).ToArray();
        db.Titles.AddRange(ids.Append(510).Select(id => new TitleEntity
        {
            Id = id,
            PlatformId = 1,
            Name = $"Boundary {id}",
            NormalizedName = $"BOUNDARY {id}",
            EnrichmentStatus = "None"
        }));
        db.TrackedTitles.AddRange(ids.Select(id => new TrackedTitleEntity { TitleId = id }));
        db.TitleExternalIds.AddRange(
            new() { TitleId = 513, Provider = "igdb", ExternalId = "confirmed", IsConfirmed = true },
            new() { TitleId = 1024, Provider = "igdb", ExternalId = "blocked-artwork", IsConfirmed = true });
        db.Set<ArtworkAcquisitionEntity>().Add(new() { TitleId = 1024, Role = ArtworkRole.Poster, Status = "Failed" });
        db.Add(new TitleProviderMatchStateEntity { TitleId = 511, ProviderId = "igdb", SuppressAutomaticMatch = true });
        db.Add(new TitleProviderMatchStateEntity { TitleId = 510, ProviderId = "igdb", DecisionJson = "invalid json" });
        db.Add(new EnrichmentJobEntity
        {
            Id = Guid.NewGuid(),
            TitleId = 512,
            PlatformId = 1,
            Phase = "Enriching",
            SourceFilename = "Boundary 512",
            ErrorsJson = "[]",
            JobType = "enrichment"
        });
        await db.SaveChangesAsync();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(set => set.SetProperty(row => row.FillPosters, true));
        var fingerprint = await db.Database.SqlQuery<string>($"SELECT romd.provider_identity_fingerprint(1025, {ProviderIdentityPolicy.Version}) AS \"Value\"").SingleAsync();
        db.Add(new TitleProviderMatchStateEntity
        {
            TitleId = 1025,
            ProviderId = "igdb",
            MatcherVersion = ProviderIdentityPolicy.Version,
            InputFingerprint = fingerprint,
            DecisionJson = "{\"State\":\"NeedsReview\",\"Candidates\":[]}"
        });
        await db.SaveChangesAsync();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('plan_cache_mode', {planMode}, false)");
        var scope = db.Titles.Where(title => db.TrackedTitles.Any(tracked => tracked.TitleId == title.Id));
        var query = new EnrichmentAssessmentQuery(db, scope, providers: new(["igdb"], ["igdb"], []));
        var canonical = await query.Requirements().ToListAsync();
        var bounded = await query.RequirementsForCounts().ToListAsync();
        static object[] Rows(IEnumerable<EnrichmentRequirement> requirements) => requirements
            .OrderBy(row => row.TitleId).ThenBy(row => row.Kind).ThenBy(row => row.Subject)
            .Select(row => (object)new { row.TitleId, row.Kind, row.Subject, row.State, row.Cause, row.SatisfiedBy }).ToArray();
        Rows(bounded).ShouldBe(Rows(canonical));
        bounded.Select(row => row.TitleId).Distinct().Order().ShouldBe(ids.Order());
        var summaries = await query.Summaries().ToListAsync();
        var boundedSummaries = await query.SummariesForCounts().ToListAsync();
        static object[] Flags(IEnumerable<EnrichmentAssessmentSummary> rows) => rows.OrderBy(row => row.TitleId)
            .Select(row => (object)new { row.TitleId, row.NeedsReview, row.CanEnrichAutomatically, row.HasActiveWork, row.HasOperationalBlocker }).ToArray();
        Flags(boundedSummaries).ShouldBe(Flags(summaries));
        boundedSummaries.Select(row => row.TitleId).Order().ShouldBe(ids.Order());
        var actual = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter])
            .ReadCountsAsync(null, default)).Value;
        actual.ShouldBe(new EnrichmentWorkbenchCounts(summaries.Count, summaries.Count(row => row.NeedsReview),
            summaries.Count(row => row.CanEnrichAutomatically), summaries.Count(row => row.HasActiveWork), summaries.Count(row => row.HasOperationalBlocker)));
        actual.NeedsReview.ShouldBeGreaterThan(0);
        actual.Automatic.ShouldBeGreaterThan(0);
        actual.Processing.ShouldBeGreaterThan(0);
        actual.Blocked.ShouldBeGreaterThan(0);
        // Untracked malformed evidence must be excluded before the scoped array is built.
        bounded.ShouldNotContain(row => row.TitleId == 510);
    }

    [Fact]
    public async Task ReadCounts_FunctionJitSetting_RestoresCallerSessionSetting()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        await db.Database.OpenConnectionAsync();
        var original = await db.Database.SqlQuery<string>($"SELECT current_setting('jit') AS \"Value\"").SingleAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET jit = on");
            var counts = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter])
                .ReadCountsAsync(null, default)).Value;
            counts.Total.ShouldBe(1);
            (await db.Database.SqlQuery<string>($"SELECT current_setting('jit') AS \"Value\"").SingleAsync()).ShouldBe("on");
        }
        finally
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('jit', {original}, false)");
            await db.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task ReadCounts_OverlongSearch_ReturnsValidationError()
    {
        await using var db = database.CreateContext();
        var result = await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter])
            .ReadCountsAsync(new string('x', 201), default);
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Validation);
    }

    [Fact]
    public async Task Read_EmptyFilteredPage_PreservesGlobalScopeCounts()
    {
        await Seed(2);
        await using var db = database.CreateContext();
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]);
        var result = (await workbench.ReadAsync("NeedsReview", null, 1, null, default)).Value;
        var stats = (await workbench.ReadCountsAsync(null, default)).Value;
        stats.Total.ShouldBe(2);
        stats.Automatic.ShouldBe(2);
        result.Items.ShouldBeEmpty();
        result.NextCursor.ShouldBeNull();
        var first = (await workbench.ReadAsync("Automatic", null, 1, null, default)).Value;
        await db.TrackedTitles.Where(title => title.TitleId == 2).ExecuteDeleteAsync();
        var exhausted = (await workbench.ReadAsync("Automatic", first.NextCursor, 1, null, default)).Value;
        (await workbench.ReadCountsAsync(null, default)).Value.Total.ShouldBe(1);
        exhausted.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Read_SearchWithApostrophe_PreservesBoundQueryParametersAndOpenConnection()
    {
        await Seed(2);
        await using var db = database.CreateContext();
        // A tracked save maintains the full-text search document, as production title writes do.
        (await db.Titles.AsTracking().SingleAsync(x => x.Id == 1)).Name = "A curator's choice";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await db.Database.OpenConnectionAsync();
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]);
        var result = (await workbench.ReadAsync("Automatic", null, 50, "curator's", default)).Value;
        (await workbench.ReadCountsAsync("curator's", default)).Value.Total.ShouldBe(1);
        result.Items.Single().TitleName.ShouldBe("A curator's choice");
        db.Database.GetDbConnection().State.ShouldBe(System.Data.ConnectionState.Open);
        var unmatched = (await workbench.ReadAsync("All", null, 50, "' OR true --", default)).Value;
        (await workbench.ReadCountsAsync("' OR true --", default)).Value.Total.ShouldBe(0);
        unmatched.Items.ShouldBeEmpty();
        await db.Database.CloseConnectionAsync();
    }

    [Theory]
    [InlineData("mario", new[] { 1, 2 })]
    [InlineData("mar", new[] { 1, 2 })]
    [InlineData("super mario", new[] { 1 })]
    [InlineData("plumber", new[] { 3 })]
    [InlineData("  ", new[] { 1, 2, 3, 4 })]
    public async Task Read_Search_CountsAndPagesWhatAFilteredOrAutomaticLaunchSelects(string query, int[] expected)
    {
        await Seed(0);
        await using var db = database.CreateContext();
        // Full-text over name and description with a prefix last token: "Supermarioland" is not a "mario" match,
        // while a description mention is. Title 5 matches but is untracked.
        db.Titles.AddRange(
            new TitleEntity { Id = 1, PlatformId = 1, Name = "Super Mario World", NormalizedName = "SUPER MARIO WORLD", EnrichmentStatus = "None" },
            new TitleEntity { Id = 2, PlatformId = 1, Name = "Mario Kart", NormalizedName = "MARIO KART", EnrichmentStatus = "None", Description = "Racing" },
            new TitleEntity { Id = 3, PlatformId = 1, Name = "Zelda", NormalizedName = "ZELDA", EnrichmentStatus = "None", Description = "A plumber cameo" },
            new TitleEntity { Id = 4, PlatformId = 1, Name = "Supermarioland", NormalizedName = "SUPERMARIOLAND", EnrichmentStatus = "None" },
            new TitleEntity { Id = 5, PlatformId = 1, Name = "Mario Party", NormalizedName = "MARIO PARTY", EnrichmentStatus = "None" });
        db.TrackedTitles.AddRange(Enumerable.Range(1, 4).Select(id => new TrackedTitleEntity { TitleId = id }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]);
        var service = new Romd.Admin.Application.Artwork.ArtworkCompletionService(
            new Romd.Persistence.Artwork.ArtworkCompletionStore(db, clock: TimeProvider.System, identityAdapters: [adapter]));
        var filters = new Romd.Contracts.Management.Artwork.ArtworkCompletionFilter(Query: query, Tracked: "tracked");

        var counts = (await workbench.ReadCountsAsync(query, default)).Value;
        var page = (await workbench.ReadAsync("All", null, 50, query, default)).Value;
        var automatic = (await workbench.ReadAsync("Automatic", null, 50, query, default)).Value;
        var filtered = (await service.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Filtered", Filters: filters, Mode: "Enrich"), default)).Value;
        var eligible = (await service.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Automatic", Filters: filters, Mode: "Enrich"), default)).Value;

        page.Items.Select(x => x.TitleId).ShouldBe(expected.Select(IdCoder.Encode));
        (counts.Total, counts.Automatic).ShouldBe((filtered.Titles, eligible.Titles));
        page.Items.Count.ShouldBe(counts.Total);
        automatic.Items.Count.ShouldBe(eligible.Titles);
        eligible.Titles.ShouldBe(expected.Length);
    }

    [Theory]
    [InlineData("All")]
    [InlineData("Automatic")]
    [InlineData("NeedsReview")]
    [InlineData("Processing")]
    [InlineData("Blocked")]
    public async Task Read_PreservesSparseQueueMembershipAcrossCandidateBatches(string view)
    {
        await Seed(600);
        await using var db = database.CreateContext();
        await db.Titles.Where(x => x.Id < 300).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Description, "Complete").SetProperty(x => x.Genre, "Adventure")
            .SetProperty(x => x.ReleaseDate, new DateOnly(1990, 1, 1)));
        db.Add(new EnrichmentJobEntity
        {
            Id = Guid.NewGuid(),
            TitleId = 350,
            PlatformId = 1,
            Phase = "Enriching",
            SourceFilename = "Title 350",
            ErrorsJson = "[]",
            JobType = "enrichment"
        });
        await db.SaveChangesAsync();
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]);
        string? cursor = null;
        var ids = new List<string>();
        do
        {
            var page = (await workbench.ReadAsync(view, cursor, 100, "Title", default)).Value;
            page.Items.ShouldAllBe(item => view == "All" || view == "Automatic" && item.CanEnrichAutomatically ||
                view == "NeedsReview" && item.NeedsReview || view == "Processing" && item.HasActiveWork ||
                view == "Blocked" && item.HasOperationalBlocker);
            ids.AddRange(page.Items.Select(item => item.TitleId));
            cursor = page.NextCursor;
        } while (cursor is not null);
        ids.Distinct().Count().ShouldBe(ids.Count);
        var stats = (await workbench.ReadCountsAsync("Title", default)).Value;
        ids.Count.ShouldBe(view switch
        {
            "All" => stats.Total,
            "Automatic" => stats.Automatic,
            "NeedsReview" => stats.NeedsReview,
            "Processing" => stats.Processing,
            _ => stats.Blocked
        });
    }

    [Fact]
    public async Task Read_SparseQueueBeyondBatchCap_FindsLateMatchesWithoutSkippingCursor()
    {
        await Seed(4100);
        await using var db = database.CreateContext();
        await db.Titles.Where(x => x.Id < 4000).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Description, "Complete").SetProperty(x => x.Genre, "Adventure")
            .SetProperty(x => x.ReleaseDate, new DateOnly(1990, 1, 1)));
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]);
        var first = (await workbench.ReadAsync("Automatic", null, 100, null, default)).Value;
        first.Items.Select(x => x.TitleId)
            .ShouldBe(Enumerable.Range(4000, 100).Select(Romd.Application.Common.Ids.IdCoder.Encode));
        first.NextCursor.ShouldNotBeNull();
        var next = (await workbench.ReadAsync("Automatic", first.NextCursor, 100, null, default)).Value;
        next.Items.ShouldHaveSingleItem().TitleId.ShouldBe(Romd.Application.Common.Ids.IdCoder.Encode(4100));
        next.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Get_ConfirmedIdentityMissingMetadata_OffersAutomaticRefresh()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        db.TitleExternalIds.Add(new() { TitleId = 1, Provider = "igdb", ExternalId = "42", IsConfirmed = true });
        await db.SaveChangesAsync();
        var result = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]).GetAsync(1, default)).Value;
        result.CanEnrichAutomatically.ShouldBeTrue();
        result.NeedsReview.ShouldBeFalse();
        Open(result).ShouldContain(x => x.Subject == "Refresh" && x.Actions.Contains("Enrich"));
    }

    [Fact]
    public async Task Get_CurrentPartialMetadata_OffersDurableOmissionAndDoesNotLoopRefresh()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        await db.Titles.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.EnrichmentStatus, "Completed").SetProperty(x => x.LastEnrichedAt, DateTimeOffset.UtcNow));
        db.TitleExternalIds.Add(new() { TitleId = 1, Provider = "igdb", ExternalId = "42", IsConfirmed = true });
        db.TitleMetadataLayers.Add(new() { TitleId = 1, SourceId = "igdb", MetadataJson = "{\"providerGameId\":\"42\"}" });
        await db.SaveChangesAsync();
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]);
        var before = (await workbench.GetAsync(1, default)).Value;
        before.NeedsReview.ShouldBeTrue();
        before.CanEnrichAutomatically.ShouldBeFalse();
        foreach (var field in EnrichmentAssessmentPolicy.CoreMetadataFields)
        {
            var current = (await workbench.GetAsync(1, default)).Value;
            var saved = await workbench.SetCommittedAsync(db, 1, field, true, RevisionPrecondition.For(current.Revision.ToString()));
            saved.IsError.ShouldBeFalse();
        }
        var after = (await workbench.GetAsync(1, default)).Value;
        after.NeedsReview.ShouldBeFalse();
        after.CanEnrichAutomatically.ShouldBeFalse();
        (await db.Set<EnrichmentOmissionEntity>().CountAsync()).ShouldBe(3);
        var stale = await workbench.SetCommittedAsync(db, 1, "Genre", false, RevisionPrecondition.For(before.Revision.ToString()));
        stale.FirstError.Code.ShouldBe("Titles.PreconditionFailed");
        ((int)stale.FirstError.Type).ShouldBe(412);
    }

    [Fact]
    public async Task Get_ActiveWorkAndIndependentDecision_ExposesBothDimensions()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(s => s.SetProperty(x => x.FillBackdrops, true).SetProperty(x => x.ReviewBackdrops, true));
        db.TitleExternalIds.Add(new() { TitleId = 1, Provider = "igdb", ExternalId = "42", IsConfirmed = true });
        db.Set<EnrichmentJobEntity>().Add(new() { Id = Guid.NewGuid(), TitleId = 1, PlatformId = 1, Phase = "Enriching", SourceFilename = "Title 1", ErrorsJson = "[]", JobType = "enrichment" });
        await db.SaveChangesAsync();
        var result = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]).GetAsync(1, default)).Value;
        result.HasActiveWork.ShouldBeTrue();
        result.NeedsReview.ShouldBeTrue();
        result.CanEnrichAutomatically.ShouldBeFalse();
    }

    [Theory]
    [InlineData(ArtworkRole.Poster)]
    [InlineData(ArtworkRole.Hero)]
    [InlineData(ArtworkRole.Logo)]
    [InlineData(ArtworkRole.Backdrop)]
    public async Task Requirements_ArtworkDemand_UsesRoleProvidersAndEachTitlesOwnTrust(ArtworkRole role)
    {
        await Seed(2);
        await using var db = database.CreateContext();
        await db.Titles.ExecuteUpdateAsync(s => s.SetProperty(x => x.Description, "Complete")
            .SetProperty(x => x.Genre, "Adventure").SetProperty(x => x.ReleaseDate, new DateOnly(1990, 1, 1)));
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(s => s
            .SetProperty(x => x.FillPosters, role == ArtworkRole.Poster)
            .SetProperty(x => x.FillHeroes, role == ArtworkRole.Hero)
            .SetProperty(x => x.FillLogos, role == ArtworkRole.Logo)
            .SetProperty(x => x.FillBackdrops, role == ArtworkRole.Backdrop));
        db.TitleExternalIds.AddRange(
            new() { TitleId = 1, Provider = "igdb", ExternalId = "11", IsConfirmed = true },
            new() { TitleId = 2, Provider = "steamgriddb", ExternalId = "22", IsConfirmed = true });
        await db.SaveChangesAsync();
        var requirements = await new EnrichmentAssessmentQuery(db, providers: new(["igdb", "steamgriddb"], ["igdb", "steamgriddb"], []))
            .Requirements().Where(x => x.Kind == EnrichmentRequirementKind.Identity).ToListAsync();
        var expectedProviders = role switch
        {
            ArtworkRole.Logo => new[] { "steamgriddb" },
            ArtworkRole.Backdrop => new[] { "igdb" },
            _ => new[] { "igdb", "steamgriddb" }
        };
        foreach (var titleId in new[] { 1, 2 })
        {
            var identities = requirements.Where(x => x.TitleId == titleId).ToArray();
            identities.Select(x => x.Subject).Order().ShouldBe(expectedProviders.Order());
            foreach (var identity in identities)
            {
                var confirmed = titleId == 1 ? identity.Subject == "igdb" : identity.Subject == "steamgriddb";
                identity.State.ShouldBe(confirmed ? EnrichmentRequirementState.Satisfied : EnrichmentRequirementState.Automatic);
            }
        }
    }

    [Theory]
    [InlineData("Unavailable", EnrichmentRequirementState.Satisfied)]
    [InlineData("Failed", EnrichmentRequirementState.Blocked)]
    [InlineData("NeedsReview", EnrichmentRequirementState.Automatic)]
    public async Task Requirements_MissingBackdrop_DistinguishesEmptyFromFailure(string outcome, EnrichmentRequirementState expected)
    {
        await Seed(1);
        await using var db = database.CreateContext();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(s => s.SetProperty(x => x.FillBackdrops, true).SetProperty(x => x.ReviewBackdrops, false));
        db.TitleExternalIds.Add(new() { TitleId = 1, Provider = "igdb", ExternalId = "42", IsConfirmed = true });
        db.Set<ArtworkAcquisitionEntity>().Add(new() { TitleId = 1, Role = ArtworkRole.Backdrop, Status = outcome });
        await db.SaveChangesAsync();
        var requirement = await new EnrichmentAssessmentQuery(db, providers: new(["igdb"], ["igdb"], []))
            .Requirements().SingleAsync(x => x.TitleId == 1 && x.Kind == EnrichmentRequirementKind.Artwork && x.Subject == "Backdrop");
        requirement.State.ShouldBe(expected);
        (await db.ArtworkSelections.AnyAsync(x => x.TitleId == 1 && x.Role == ArtworkRole.Backdrop && x.Mode == ArtworkSelectionMode.Empty)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, EnrichmentRequirementState.Automatic)]
    [InlineData("Unavailable", EnrichmentRequirementState.Satisfied)]
    [InlineData("Failed", EnrichmentRequirementState.Blocked)]
    [InlineData("NeedsReview", EnrichmentRequirementState.NeedsReview)]
    public async Task Requirements_MissingLogo_OnlySuccessfulAbsenceIsComplete(string? outcome, EnrichmentRequirementState expected)
    {
        await Seed(1);
        await using var db = database.CreateContext();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(s => s.SetProperty(x => x.FillLogos, true));
        db.TitleExternalIds.Add(new() { TitleId = 1, Provider = "steamgriddb", ExternalId = "42", IsConfirmed = true });
        if (outcome is not null)
            db.Set<ArtworkAcquisitionEntity>().Add(new() { TitleId = 1, Role = ArtworkRole.Logo, Status = outcome });
        await db.SaveChangesAsync();
        var requirement = await new EnrichmentAssessmentQuery(db, providers: new(["steamgriddb"], ["steamgriddb"], []))
            .Requirements().SingleAsync(x => x.TitleId == 1 && x.Kind == EnrichmentRequirementKind.Artwork && x.Subject == "Logo");
        requirement.State.ShouldBe(expected);
        (await db.ArtworkSelections.AnyAsync(x => x.TitleId == 1 && x.Role == ArtworkRole.Logo && x.Mode == ArtworkSelectionMode.Empty)).ShouldBeFalse();
    }

    [Fact]
    public async Task Requirements_MissingLogo_DoesNotSuppressIdentityDemand()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(s => s.SetProperty(x => x.FillLogos, true));
        db.Set<ArtworkAcquisitionEntity>().Add(new() { TitleId = 1, Role = ArtworkRole.Logo, Status = "Unavailable" });
        await db.SaveChangesAsync();
        var identity = await new EnrichmentAssessmentQuery(db, providers: new(["steamgriddb"], ["steamgriddb"], []))
            .Requirements().SingleAsync(x => x.TitleId == 1 && x.Kind == EnrichmentRequirementKind.Identity && x.Subject == "steamgriddb");
        identity.State.ShouldBe(EnrichmentRequirementState.Automatic);
    }

    [Fact]
    public async Task Get_ArtworkEmpty_OverridesHistoricalUnavailableOutcome()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(s => s.SetProperty(x => x.FillBackdrops, true));
        db.ArtworkSelections.Add(new() { TitleId = 1, Role = ArtworkRole.Backdrop, Mode = ArtworkSelectionMode.Empty });
        db.Set<ArtworkAcquisitionEntity>().Add(new() { TitleId = 1, Role = ArtworkRole.Backdrop, Status = "Unavailable" });
        await db.SaveChangesAsync();
        var result = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]).GetAsync(1, default)).Value;
        Open(result).ShouldNotContain(x => x.Kind == "Artwork");
        SatisfiedBy(result, "Artwork").ShouldBe([
            ("Poster", EnrichmentRequirementSatisfiedBy.NotRequested), ("Backdrop", EnrichmentRequirementSatisfiedBy.LeftEmpty),
            ("Hero", EnrichmentRequirementSatisfiedBy.NotRequested), ("Logo", EnrichmentRequirementSatisfiedBy.NotRequested)]);
    }

    [Fact]
    public async Task Read_ProviderUnavailable_SeparatesBlockersFromManualDecisions()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, false));
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]);
        var result = (await workbench.ReadAsync("Blocked", null, 20, null, default)).Value;
        result.Items.ShouldHaveSingleItem().HasOperationalBlocker.ShouldBeTrue();
        var stats = (await workbench.ReadCountsAsync(null, default)).Value;
        stats.Blocked.ShouldBe(1);
        stats.NeedsReview.ShouldBe(0);
        stats.Automatic.ShouldBe(0);
    }

    [Fact]
    public async Task Get_SuppressedProvider_DoesNotOfferImpossibleAutomaticWork()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(s => s.SetProperty(x => x.FillPosters, true));
        db.Set<TitleProviderMatchStateEntity>().Add(new() { TitleId = 1, ProviderId = "igdb", SuppressAutomaticMatch = true });
        await db.SaveChangesAsync();
        var assessment = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]).GetAsync(1, default)).Value;
        assessment.CanEnrichAutomatically.ShouldBeFalse();
        assessment.NeedsReview.ShouldBeTrue();
        Open(assessment).ShouldContain(x => x.Subject == "Description" && x.Actions.Contains("AcceptOmission"));
        Open(assessment).ShouldContain(x => x.Subject == "Poster" && x.Actions.Contains("ReviewArtwork"));
    }

    [Fact]
    public async Task Get_ProviderWithoutBackdropCapability_DoesNotOfferBackdropReview()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(s => s.SetProperty(x => x.FillBackdrops, true));
        adapter.Id.Returns("steamgriddb");
        db.TitleExternalIds.Add(new() { TitleId = 1, Provider = "steamgriddb", ExternalId = "42", IsConfirmed = true });
        await db.SaveChangesAsync();
        var assessment = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]).GetAsync(1, default)).Value;
        assessment.NeedsReview.ShouldBeFalse();
        assessment.CanEnrichAutomatically.ShouldBeFalse();
        Open(assessment).ShouldNotContain(x => x.Subject == "Backdrop" && x.Actions.Contains("ReviewArtwork"));
        var backdrop = assessment.Requirements.Single(x => x.Key == "Artwork:Backdrop");
        (backdrop.State, backdrop.Cause).ShouldBe((EnrichmentIssueState.Blocked, EnrichmentRequirementCause.ProviderDisabled));
    }

    [Fact]
    public async Task Get_MediaAcquisitionDisabled_DoesNotCreateArtworkRequirements()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(s => s.SetProperty(x => x.FillBackdrops, true).SetProperty(x => x.FillLogos, true));
        var options = Microsoft.Extensions.Options.Options.Create(new Romd.Admin.Application.Configuration.EnrichmentOptions { DownloadMedia = false });
        var assessment = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), options, [adapter]).GetAsync(1, default)).Value;
        Open(assessment).ShouldNotContain(x => x.Kind == "Artwork");
        SatisfiedBy(assessment, "Artwork").ShouldAllBe(x => x.SatisfiedBy == EnrichmentRequirementSatisfiedBy.NotRequested);
    }

    [Fact]
    public async Task Get_ImportingArtwork_RemainsProcessingUntilItsCurrentRequestFinishes()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        var id = Guid.NewGuid();
        var job = ArtworkImportJobEntity.FromDomain(Romd.Domain.Jobs.ArtworkImportJob.Create(id, "Title 1", 1, 1, ArtworkRole.Backdrop, 1, "igdb", "42", "asset", "https://example.com/image", null, TimeProvider.System.GetUtcNow()));
        job.Phase = "Importing";
        db.Add(job);
        db.ArtworkSelections.Add(new() { TitleId = 1, Role = ArtworkRole.Backdrop, Mode = ArtworkSelectionMode.Automatic, Revision = 1, PendingRequestId = id });
        await db.SaveChangesAsync();
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]);
        (await workbench.GetAsync(1, default)).Value.HasActiveWork.ShouldBeTrue();
        await db.Set<ArtworkImportJobEntity>().Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Phase, "Completed"));
        (await workbench.GetAsync(1, default)).Value.HasActiveWork.ShouldBeFalse();
    }

    [Fact]
    public async Task SetOmission_ConcurrentRequests_OneWinsAndTheOtherFailsThePrecondition()
    {
        await Seed(1);
        Guid revision;
        await using (var db = database.CreateContext())
            revision = await db.Titles.Select(x => x.Revision).SingleAsync();
        async Task<ErrorOr.ErrorOr<ErrorOr.Updated>> Accept()
        {
            await using var db = database.CreateContext();
            return await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter])
                .SetCommittedAsync(db, 1, "Description", true, RevisionPrecondition.For(revision.ToString()));
        }
        var results = await Task.WhenAll(Accept(), Accept());
        results.Count(x => !x.IsError).ShouldBe(1);
        results.Single(x => x.IsError).FirstError.Code.ShouldBe("Titles.PreconditionFailed");
    }

    [Fact]
    public async Task SetOmission_WithoutACallerTransaction_Throws()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        var revision = await db.Titles.Select(x => x.Revision).SingleAsync();

        // The caller's transaction keeps the rotated revision locked while it reads the response.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter])
                .SetOmissionAsync(1, "Description", true, RevisionPrecondition.For(revision.ToString()), default));
        (await db.Titles.Select(x => x.Revision).SingleAsync()).ShouldBe(revision);
    }

    [Fact]
    public async Task Get_LegacyExplicitArtworkBatch_RecognizesActiveWorkWithoutPlatform()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        db.Add(new BulkEnrichmentJobEntity { Id = Guid.NewGuid(), Phase = "Pending", SourceFilename = "Artwork", Scope = "All", ArtworkTitleIdsJson = "[1]", JobType = "bulk_enrichment", ErrorsJson = "[]" });
        await db.SaveChangesAsync();
        var assessment = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]).GetAsync(1, default)).Value;
        assessment.HasActiveWork.ShouldBeTrue();
        assessment.CanEnrichAutomatically.ShouldBeFalse();
    }

    [Fact]
    public async Task Get_ManuallyCompleteTitle_DoesNotRequireUnusedProviderIdentities()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        await db.Titles.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Description, "Curated description").SetProperty(x => x.Genre, "Adventure").SetProperty(x => x.ReleaseDate, new DateOnly(1999, 1, 1)));
        var assessment = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]).GetAsync(1, default)).Value;
        assessment.NeedsReview.ShouldBeFalse();
        assessment.CanEnrichAutomatically.ShouldBeFalse();
        assessment.HasOperationalBlocker.ShouldBeFalse();
        Open(assessment).ShouldBeEmpty();
        assessment.Requirements.ShouldNotContain(x => x.Kind == "Identity");
        SatisfiedBy(assessment, "Metadata").ShouldBe([
            ("Description", EnrichmentRequirementSatisfiedBy.Present), ("Genre", EnrichmentRequirementSatisfiedBy.Present),
            ("ReleaseDate", EnrichmentRequirementSatisfiedBy.Present), ("Refresh", EnrichmentRequirementSatisfiedBy.Current)]);
        SatisfiedBy(assessment, "Artwork").ShouldAllBe(x => x.SatisfiedBy == EnrichmentRequirementSatisfiedBy.NotRequested);
        SatisfiedBy(assessment, "Operation").ShouldBe([("Processing", EnrichmentRequirementSatisfiedBy.Idle)]);
    }

    [Fact]
    public async Task Get_AmbiguousIdentity_DefersDependentFieldsWithoutOperationalBlocker()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        var fingerprint = await db.Titles.Where(x => x.Id == 1).Select(x => Romd.Persistence.MetadataProviders.ProviderIdentityDatabaseFunctions.Fingerprint(x.Id, ProviderIdentityPolicy.Version)).SingleAsync();
        db.Add(new TitleProviderMatchStateEntity { TitleId = 1, ProviderId = "igdb", MatcherVersion = ProviderIdentityPolicy.Version, InputFingerprint = fingerprint, DecisionJson = "{\"State\":\"NeedsReview\",\"Candidates\":[]}" });
        await db.SaveChangesAsync();
        var assessment = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]).GetAsync(1, default)).Value;
        assessment.NeedsReview.ShouldBeTrue();
        assessment.HasOperationalBlocker.ShouldBeFalse();
        Open(assessment).ShouldAllBe(x => x.Kind == "Identity");
        SatisfiedBy(assessment, "Metadata").ShouldBe([("Refresh", EnrichmentRequirementSatisfiedBy.Current)]);
    }

    [Fact]
    public async Task Search_CanonicalFilterAndSummary_AgreeWithWorkbenchMembership()
    {
        await Seed(3);
        await using var db = database.CreateContext();
        var repository = new Romd.Persistence.Repositories.SearchRepository(db, new Romd.Persistence.Repositories.ArtworkReader(db), identityAdapters: [adapter]);
        var page = (await repository.SearchTitlesAsync(null, new Romd.Admin.Application.Search.TitleSearchFilters(Enrichment: [Romd.Admin.Application.Search.EnrichmentFlagFilter.Automatic]), Romd.Admin.Application.Search.TitleSortField.Name, null, 50, null)).Value;
        var workbench = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]).ReadAsync("Automatic", null, 50, null, default)).Value;
        page.Items.Select(x => Romd.Application.Common.Ids.IdCoder.Encode(x.Id)).Order().ShouldBe(workbench.Items.Select(x => x.TitleId).Order());
        page.Items.ShouldAllBe(x => x.EnrichmentAssessment != null && x.EnrichmentAssessment.CanEnrichAutomatically);
    }

    [Fact]
    public async Task Search_TrackedFilter_ExcludesUntrackedFactsBeforeAssessment()
    {
        await Seed(2);
        await using var db = database.CreateContext();
        await db.TrackedTitles.Where(x => x.TitleId == 2).ExecuteDeleteAsync();
        // Invalid legacy provider evidence must not be evaluated outside the selected scope.
        db.Add(new TitleProviderMatchStateEntity { TitleId = 2, ProviderId = "igdb", DecisionJson = "invalid json" });
        await db.SaveChangesAsync();
        var repository = new Romd.Persistence.Repositories.SearchRepository(db,
            new Romd.Persistence.Repositories.ArtworkReader(db), identityAdapters: [adapter]);
        var result = (await repository.SearchTitlesAsync(null,
            new Romd.Admin.Application.Search.TitleSearchFilters(Tracked: Romd.Admin.Application.Search.TrackedFilter.Tracked, Enrichment: [Romd.Admin.Application.Search.EnrichmentFlagFilter.Automatic]),
            Romd.Admin.Application.Search.TitleSortField.Name, null, 50, null)).Value;
        result.Items.ShouldHaveSingleItem().Id.ShouldBe(1);
    }

    [Fact]
    public void Query_ScopeIds_StayInDatabase()
    {
        using var db = database.CreateContext();
        var query = new EnrichmentAssessmentQuery(db, db.Titles.Where(x => x.Id < 100), providers: new(["igdb"], ["igdb"], [])).Requirements().ToQueryString();
        output.WriteLine(query);
        query.ShouldContain("array_agg(");
        query.ShouldContain("enrichment_requirement_facts_set");
    }

    [Fact]
    public async Task Read_LargeCatalog_ScopesBeforeAssessmentAndReportsPerformance()
    {
        await Seed(1);
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO romd."Titles" ("Id","PlatformId","Name","NormalizedName","Revision","EnrichmentStatus","CreatedAt","CreatedByUserId","HasLocalPayload","RetainWithoutCatalog","FieldProvenanceJson")
            SELECT n,1,'Title '||n,'TITLE '||n,gen_random_uuid(),'None',0,'00000000-0000-0000-0000-000000000000'::uuid,false,false,json_build_object()::text FROM generate_series(2,20000) n;
            INSERT INTO romd."TrackedTitles" ("TitleId","CreatedAt","UpdatedAt") SELECT n,0,0 FROM generate_series(2,10000) n;
            INSERT INTO romd."TitleExternalIds" ("TitleId","Provider","ExternalId","IsConfirmed","MatchConfidence","CreatedAt","CreatedByUserId")
            SELECT n,'igdb',n::text,true,1,0,'00000000-0000-0000-0000-000000000000'::uuid FROM generate_series(2,10000,2) n;
            ANALYZE romd."Titles";
            ANALYZE romd."TrackedTitles";
            ANALYZE romd."TitleExternalIds";
            """);
        var steam = Substitute.For<IProviderIdentityAdapter>();
        steam.Id.Returns("steamgriddb");
        steam.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter, steam]).ReadAsync("Automatic", null, 50, null, default);
        watch.Stop();
        result.IsError.ShouldBeFalse();
        var stats = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter, steam]).ReadCountsAsync(null, default)).Value;
        stats.Total.ShouldBe(10000);
        stats.Automatic.ShouldBe(10000);
        result.Value.Items.Count.ShouldBe(50);
        output.WriteLine($"10,000 tracked + 10,000 untracked, two providers, 50-row page: {watch.ElapsedMilliseconds} ms");
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"""
            EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT)
            SELECT f."TitleId",count(*) FROM romd.enrichment_requirement_facts_set(
              ARRAY(SELECT t."Id" FROM romd."Titles" t WHERE EXISTS(SELECT 1 FROM romd."TrackedTitles" tracked WHERE tracked."TitleId"=t."Id")),
              '{ProviderIdentityPolicy.Version}',true,true,true,ARRAY['igdb','steamgriddb'],ARRAY['igdb','steamgriddb'],ARRAY[]::text[]) f GROUP BY f."TitleId"
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            output.WriteLine(reader.GetString(0));
    }

    private static ContractEnrichmentRequirement[] Open(EnrichmentAssessment assessment) =>
        assessment.Requirements.Where(x => x.State != EnrichmentIssueState.Satisfied).ToArray();

    private static (string Subject, EnrichmentRequirementSatisfiedBy? SatisfiedBy)[] SatisfiedBy(EnrichmentAssessment assessment, string kind) =>
        assessment.Requirements.Where(x => x.Kind == kind).Select(x => (x.Subject, x.SatisfiedBy)).ToArray();

    private async Task Seed(int count)
    {
        await using var db = database.CreateContext();
        db.Platforms.Add(new() { Id = 1, Name = "System", ShortName = "system" });
        for (var id = 1; id <= count; id++)
        {
            db.Titles.Add(new() { Id = id, PlatformId = 1, Name = $"Title {id}", NormalizedName = $"TITLE {id}", EnrichmentStatus = "None" });
            db.TrackedTitles.Add(new() { TitleId = id });
        }
        await db.SaveChangesAsync();
        await db.Set<MetadataProviderSettingsEntity>().Where(x => x.ProviderId == "igdb").ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, true).SetProperty(x => x.ClientId, "configured").SetProperty(x => x.ProtectedClientSecret, "protected"));
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(s => s.SetProperty(x => x.FillPosters, false).SetProperty(x => x.FillHeroes, false).SetProperty(x => x.FillLogos, false).SetProperty(x => x.FillBackdrops, false));
    }
}
