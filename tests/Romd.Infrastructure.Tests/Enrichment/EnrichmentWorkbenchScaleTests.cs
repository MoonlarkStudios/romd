using Romd.Infrastructure.Tests.Helpers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Enrichment;
using Romd.Admin.Application.Search;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Enrichment;
using Romd.Contracts.Common.Models;
using Romd.Application.Common.Pagination;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Tests.Persistence;
using Romd.Persistence;
using Romd.Persistence.Artwork;
using Romd.Persistence.Entities;
using Romd.Persistence.Enrichment;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using EnrichmentRequirementSatisfiedBy = Romd.Contracts.Management.Enrichment.EnrichmentRequirementSatisfiedBy;

using ContractEnrichmentRequirement = Romd.Contracts.Management.Enrichment.EnrichmentRequirement;

using ArtworkCompletionJob = Romd.Domain.Jobs.ArtworkCompletionJob;

namespace Romd.Infrastructure.Tests.Enrichment;

/// <summary>Observes real fingerprint/trust assessment cost without machine-dependent timing assertions.</summary>
[Collection(PostgreSqlScaleWorkloads.Name)]
public sealed class EnrichmentWorkbenchScaleTests(ITestOutputHelper output) : IDisposable
{
    private const int TrackedTitles = 10_000;
    private const int UntrackedTitles = 1_000;
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly Guid actor = Guid.NewGuid();

    [Fact]
    public async Task Read_RealCatalogEvidenceAndAutomaticTrust_ReportsFirstAndRepeatedRequestCosts()
    {
        await SeedAsync();
        await using var db = database.CreateContext();
        var adapter = Substitute.For<IProviderIdentityAdapter>();
        adapter.Id.Returns("igdb");
        adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: [adapter]);
        var trackedIds = Enumerable.Range(1, TrackedTitles).Select(IdCoder.Encode).ToHashSet();
        var automaticIds = Enumerable.Range(1, TrackedTitles).Where(id => id % 5 == 0).Select(IdCoder.Encode).ToHashSet();

        // "First" measures a cold application query path; seeding has already touched database
        // buffers. No database caches are flushed and no timing threshold is imposed.
        var watch = Stopwatch.StartNew();
        var first = await workbench.ReadAsync("All", null, 50, null, default);
        watch.Stop();
        output.WriteLine($"First workbench 50-row page: {watch.ElapsedMilliseconds} ms");
        first.IsError.ShouldBeFalse();
        AssertPage(first.Value);

        watch.Restart();
        var repeated = await workbench.ReadAsync("All", null, 50, null, default);
        watch.Stop();
        output.WriteLine($"Repeated workbench 50-row page: {watch.ElapsedMilliseconds} ms");
        repeated.IsError.ShouldBeFalse();
        AssertPage(repeated.Value);
        var stats = (await workbench.ReadCountsAsync(null, default)).Value;
        stats.Total.ShouldBe(TrackedTitles);
        stats.Automatic.ShouldBe(8_000);
        stats.NeedsReview.ShouldBe(2_000);
        stats.Processing.ShouldBe(0);
        repeated.Value.Items.Select(x => x.TitleId).ShouldBe(first.Value.Items.Select(x => x.TitleId));

        watch.Restart();
        var detail = await workbench.GetAsync(5, default);
        watch.Stop();
        output.WriteLine($"First automatically trusted title detail: {watch.ElapsedMilliseconds} ms");
        detail.IsError.ShouldBeFalse();
        detail.Value.CanEnrichAutomatically.ShouldBeTrue();
        detail.Value.NeedsReview.ShouldBeFalse();
        Open(detail.Value).ShouldNotContain(x => x.Kind == "Identity");
        TrustedIdentities(detail.Value).ShouldBe(["igdb"]);

        watch.Restart();
        var repeatedDetail = await workbench.GetAsync(5, default);
        watch.Stop();
        output.WriteLine($"Repeated automatically trusted title detail: {watch.ElapsedMilliseconds} ms");
        repeatedDetail.IsError.ShouldBeFalse();
        repeatedDetail.Value.Requirements.Select(x => (x.Kind, x.Subject, x.State, x.Cause, x.SatisfiedBy, Actions: string.Join(",", x.Actions)))
            .ShouldBe(detail.Value.Requirements.Select(x => (x.Kind, x.Subject, x.State, x.Cause, x.SatisfiedBy, Actions: string.Join(",", x.Actions))));
        output.WriteLine("Fixture: 10,000 tracked (2,000 automatic identities, 6,000 confirmed, 2,000 no-match), " +
            "1,000 untracked controls, 22,000 active non-BIOS DAT releases with local names and years.");

        void AssertPage(Page<EnrichmentAssessment> page)
        {
            page.Items.Count.ShouldBe(50);
            page.NextCursor.ShouldNotBeNull();
            page.Items.ShouldAllBe(item => trackedIds.Contains(item.TitleId));
            var automaticallyTrusted = page.Items.Where(item => automaticIds.Contains(item.TitleId)).ToArray();
            automaticallyTrusted.Length.ShouldBe(10);
            automaticallyTrusted.ShouldAllBe(item => Open(item).All(issue => issue.Kind != "Identity"));
            automaticallyTrusted.ShouldAllBe(item => TrustedIdentities(item).SequenceEqual(new[] { "igdb" }));
        }
    }

    [Fact]
    public async Task Read_RepresentativeWorkbench_ReportsLatencyAndPreservesPages()
    {
        await SeedAsync(untrackedTitles: 10_000);
        await SeedArtworkAndHistoryAsync();
        var providers = new[] { AvailableProvider("igdb"), AvailableProvider("steamgriddb") };
        await using var db = database.CreateContext();
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: providers);
        var watch = Stopwatch.StartNew();
        var page = await workbench.ReadAsync("All", null, 50, null, default);
        page.IsError.ShouldBeFalse();
        var initialStats = (await workbench.ReadCountsAsync(null, default)).Value;
        initialStats.Total.ShouldBe(TrackedTitles);
        initialStats.Processing.ShouldBe(500);
        output.WriteLine($"Representative first count + page: {watch.ElapsedMilliseconds} ms");
        var wire = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        output.WriteLine($"MEASURE workbench 50-row page JSON bytes: {JsonSerializer.SerializeToUtf8Bytes(page.Value, wire).Length}");

        watch.Restart();
        var next = await workbench.ReadAsync("All", page.Value.NextCursor, 50, null, default);
        next.IsError.ShouldBeFalse();
        next.Value.Items.Count.ShouldBe(50);
        next.Value.Items.Select(x => x.TitleId).Intersect(page.Value.Items.Select(x => x.TitleId)).ShouldBeEmpty();
        output.WriteLine($"Representative next page without counts: {watch.ElapsedMilliseconds} ms");

        watch.Restart();
        var automaticPage = await workbench.ReadAsync("Automatic", null, 50, null, default);
        automaticPage.IsError.ShouldBeFalse();
        automaticPage.Value.Items.Count.ShouldBe(50);
        output.WriteLine($"Representative automatic first page without counts: {watch.ElapsedMilliseconds} ms");
        watch.Restart();
        var independentCounts = await workbench.ReadCountsAsync(null, default);
        independentCounts.IsError.ShouldBeFalse();
        independentCounts.Value.ShouldBe(initialStats);
        output.WriteLine($"Representative independent counts: {watch.ElapsedMilliseconds} ms");

        watch.Restart();
        var sparse = await workbench.ReadAsync("Processing", KeysetCursor.Encode(new IntegerPosition(450), KeysetCursor.Fingerprint("enrichment-workbench", "manager-tracked-titles", "Processing", null, "id", "asc")), 50, null, default);
        sparse.IsError.ShouldBeFalse();
        sparse.Value.Items.Count.ShouldBe(50);
        sparse.Value.NextCursor.ShouldBeNull();
        output.WriteLine($"Representative sparse terminal page without counts (remaining 50 active of 9550 candidates): {watch.ElapsedMilliseconds} ms");

        // Inspect the whole-scope assessment plan separately from service timings. This
        // records buffer/spill evidence without assuming a portable latency threshold.
        await using (var command = new EnrichmentAssessmentQuery(db,
            db.Titles.Where(title => db.TrackedTitles.Any(tracked => tracked.TitleId == title.Id)),
            providers: new EnrichmentProviderAvailability(["igdb", "steamgriddb"], ["igdb", "steamgriddb"], []))
            .Summaries().CreateDbCommand())
        {
            command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + command.CommandText;
            await db.Database.OpenConnectionAsync();
            try
            {
                await using var reader = await command.ExecuteReaderAsync();
                (await reader.ReadAsync()).ShouldBeTrue();
                using var plan = JsonDocument.Parse(reader.GetString(0));
                var execution = plan.RootElement[0];
                var root = execution.GetProperty("Plan");
                output.WriteLine($"Whole-scope assessment EXPLAIN: execution {execution.GetProperty("Execution Time")} ms, " +
                    $"shared hits {root.GetProperty("Shared Hit Blocks")}, shared reads {root.GetProperty("Shared Read Blocks")}, " +
                    $"temp reads {root.GetProperty("Temp Read Blocks")}, temp writes {root.GetProperty("Temp Written Blocks")}");
            }
            finally { await db.Database.CloseConnectionAsync(); }
        }
    }

    [Fact]
    public async Task Read_RepresentativeHistory_ReportsCountsAndPreservesPages()
    {
        await SeedAsync(untrackedTitles: 10_000);
        var (historyId, _) = await SeedArtworkAndHistoryAsync();
        var providers = new[] { AvailableProvider("igdb"), AvailableProvider("steamgriddb") };
        await using var db = database.CreateContext();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System, identityAdapters: providers);
        var watch = Stopwatch.StartNew();
        watch.Restart();
        var history = await store.ItemsAsync(historyId, 5000, "Failed", default);
        history.IsError.ShouldBeFalse();
        history.Value.Items.Count.ShouldBe(50);
        history.Value.Items.ShouldAllBe(item => item.Roles.Any(role => role.Status == "Failed"));
        output.WriteLine($"Representative filtered history page after title 5000: {watch.ElapsedMilliseconds} ms");
        watch.Restart();
        (await store.RecentAsync(default)).Count.ShouldBe(3);
        output.WriteLine($"Representative recent history summaries: {watch.ElapsedMilliseconds} ms");
        // Run panel reads over the 10,000-item history run. Counts evaluate current facts over the run's titles
        // for Review; only the Review page does so for its candidates, other pages only for their 50 titles.
        for (var repeat = 0; repeat < 2; repeat++)
        {
            watch.Restart();
            var run = (await store.GetAsync(historyId, default)).Value;
            output.WriteLine($"MEASURE[{repeat}] run job DTO with counts: {watch.ElapsedMilliseconds} ms " +
                $"(review {run.Review}, failed {run.Failed}, unfinished {run.Unfinished}, working {run.Working})");
            (run.Failed, run.Unfinished, run.Working).ShouldBe((TrackedTitles / 3, 0, 0));
            foreach (var status in new string?[] { null, "Review", "Failed", "Unfinished" })
            {
                watch.Restart();
                var first = (await store.ItemsAsync(historyId, 0, status, default)).Value;
                output.WriteLine($"MEASURE[{repeat}] run first page status={status ?? "(all)"}: {watch.ElapsedMilliseconds} ms ({first.Items.Count} items)");
                first.Items.Count.ShouldBe(status switch { "Review" => Math.Min(run.Review, 50), "Unfinished" => 0, _ => 50 });
                first.Items.ShouldAllBe(item => item.Now != null);
            }
        }
    }

    [Fact]
    public async Task Preview_RepresentativeScopes_AgreeWithWorkbenchCounts()
    {
        await SeedAsync(untrackedTitles: 10_000);
        await SeedArtworkAndHistoryAsync();
        var providers = new[] { AvailableProvider("igdb"), AvailableProvider("steamgriddb") };
        await using var db = database.CreateContext();
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: providers);
        var initialStats = (await workbench.ReadCountsAsync(null, default)).Value;
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System, identityAdapters: providers);
        var watch = Stopwatch.StartNew();
        watch.Restart();
        var preview = await store.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Automatic", Mode: "Enrich"), default);
        preview.IsError.ShouldBeFalse();
        preview.Value.Titles.ShouldBeGreaterThan(0);
        output.WriteLine($"Representative automatic preview: {watch.ElapsedMilliseconds} ms ({preview.Value.Titles} eligible)");

        for (var repeat = 0; repeat < 2; repeat++)
        {
            watch.Restart();
            var filteredPreview = await store.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Filtered", Mode: "Enrich",
                Filters: new ArtworkCompletionFilter(Tracked: "tracked", Enrichment: ["NeedsReview"])), default);
            output.WriteLine($"MEASURE[{repeat}] filtered preview enrichment=NeedsReview: {watch.ElapsedMilliseconds} ms ({filteredPreview.Value.Titles} titles)");
            filteredPreview.Value.Titles.ShouldBe(initialStats.NeedsReview);
        }
    }

    [Fact]
    public async Task Read_RepresentativeCatalogWithConcurrentMutations_ReportsLatencyAndPreservesPages()
    {
        await SeedAsync(untrackedTitles: 10_000);
        var (historyId, activeId) = await SeedArtworkAndHistoryAsync();
        var providers = new[] { AvailableProvider("igdb"), AvailableProvider("steamgriddb") };
        await using var diagnostics = new ScaleQueryDiagnostics(database.ConnectionString, output);
        // Four independent DbContexts model simultaneous service requests. Database mutations
        // model publication traffic; this is not an HTTP/provider-worker throughput test.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var elapsed = new ConcurrentBag<double>();
        var pageElapsed = new ConcurrentBag<double>();
        var readers = Enumerable.Range(0, 4).Select(async reader =>
        {
            await start.Task;
            var commands = diagnostics.Reader(reader);
            await using var db = new RomdDbContext(database.CreateOptions(builder => builder.AddInterceptors(commands)));
            var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: providers);
            try
            {
                for (var request = 0; request < 5; request++)
                {
                    commands.Phase = $"{request}:page";
                    var watch = Stopwatch.StartNew();
                    var result = await workbench.ReadAsync(reader % 2 == 0 ? "Automatic" : "NeedsReview", null, 50, null, default);
                    elapsed.Add(watch.Elapsed.TotalMilliseconds);
                    result.IsError.ShouldBeFalse();
                    commands.Phase = $"{request}:counts";
                    (await workbench.ReadCountsAsync(null, default)).Value.Total.ShouldBe(TrackedTitles);
                    result.Value.Items.Count.ShouldBeLessThanOrEqualTo(50);
                    result.Value.Items.Select(x => x.TitleId).Distinct().Count().ShouldBe(result.Value.Items.Count);
                    watch.Restart();
                    commands.Phase = $"{request}:bounded-page";
                    var bounded = await workbench.ReadAsync(reader % 2 == 0 ? "Automatic" : "NeedsReview", null, 50, null, default);
                    pageElapsed.Add(watch.Elapsed.TotalMilliseconds);
                    bounded.IsError.ShouldBeFalse();
                    bounded.Value.Items.Count.ShouldBe(50);
                }
            }
            catch
            {
                await diagnostics.SnapshotAsync($"reader-{reader}-failure");
                throw;
            }
        }).ToArray();
        var writer = Task.Run(async () =>
        {
            await start.Task;
            await using var db = database.CreateContext();
            for (var batch = 0; batch < 10; batch++)
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                var first = batch * 50 + 1;
                var last = first + 49;
                await db.Titles.Where(x => x.Id >= first && x.Id <= last)
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.Description, "Published metadata"));
                await db.Set<ArtworkCompletionItemEntity>().Where(x => x.JobId == activeId && x.TitleId >= first && x.TitleId <= last)
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow));
                await transaction.CommitAsync();
                await Task.Delay(100);
            }
            await db.Jobs.Where(x => x.Id == activeId).ExecuteUpdateAsync(set => set.SetProperty(x => x.Phase, "Completed"));
        });
        start.SetResult();
        await Task.WhenAll(readers.Append(writer));
        var timings = elapsed.Order().ToArray();
        output.WriteLine($"Concurrent initial page reads, 4 readers / {timings.Length} requests with 10 mutation batches: " +
            $"p50 {timings[(timings.Length - 1) / 2]:F0} ms, p95 {timings[(int)Math.Ceiling(timings.Length * .95) - 1]:F0} ms, max {timings[^1]:F0} ms");

        var pageTimings = pageElapsed.Order().ToArray();
        output.WriteLine($"Concurrent page-only reads interleaved with counted reads, {pageTimings.Length} requests: " +
            $"p50 {pageTimings[(pageTimings.Length - 1) / 2]:F0} ms, p95 {pageTimings[(int)Math.Ceiling(pageTimings.Length * .95) - 1]:F0} ms, max {pageTimings[^1]:F0} ms");

        // Both requests capture explicit titles, so both require execution even if a title is
        // subsequently untracked. An AllTracked owner can legitimately coexist with a later
        // Selected request because its tracked-only work may skip that title at execution.
        var launches = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task LaunchAsync(ArtworkCompletionRequest request, string label)
        {
            await launches.Task;
            await using var db = database.CreateContext();
            var watch = Stopwatch.StartNew();
            var result = await new ArtworkCompletionStore(db, clock: TimeProvider.System, identityAdapters: providers).AcceptCompletionAsync(db, request, actor, default);
            result.IsError.ShouldBeFalse();
            output.WriteLine($"Concurrent acceptance {label}: {watch.ElapsedMilliseconds} ms");
        }
        var bulk = LaunchAsync(new(Guid.NewGuid(), Enumerable.Range(1, TrackedTitles).Select(IdCoder.Encode).ToArray(), Mode: "Enrich"), "10,000 selected tracked titles");
        var single = LaunchAsync(new(Guid.NewGuid(), [IdCoder.Encode(10_000)], Mode: "Enrich"), "single title");
        launches.SetResult();
        await Task.WhenAll(bulk, single);
        await using (var db = database.CreateContext())
        {
            var owners = await db.Set<ArtworkCompletionItemEntity>()
                .Where(x => x.CompletedAt == null && x.FollowUpJobId == null)
                .GroupBy(x => x.TitleId).Select(group => group.Count()).ToListAsync();
            owners.Count.ShouldBe(TrackedTitles);
            owners.ShouldAllBe(count => count == 1);
        }
        await using (var db = database.CreateContext())
        {
            // The concurrent Selected run still owns every title, so retry acceptance also exercises sharing.
            var watch = Stopwatch.StartNew();
            var retry = await new ArtworkCompletionStore(db, clock: TimeProvider.System, identityAdapters: providers)
                .AcceptCompletionAsync(db, new(Guid.NewGuid(), Scope: "Retry", RetryJobId: historyId, RetryStatus: "Failed"), actor, default);
            long acceptanceMilliseconds = watch.ElapsedMilliseconds;
            var summary = (await new ArtworkCompletionStore(db, clock: TimeProvider.System, identityAdapters: providers).GetAsync(retry.Value, default)).Value;
            output.WriteLine($"MEASURE retry acceptance status=Failed: {acceptanceMilliseconds} ms ({summary.Total} titles)");
            summary.Total.ShouldBe(TrackedTitles / 3);
        }
        output.WriteLine("Representative fixture: 10,000 tracked + 10,000 untracked, two providers, 40,000 DAT rows, " +
            "20,000 artwork assets, 20,000 historical items / 80,000 role outcomes, and 500 initially active items. " +
            "Timings are observations, not machine-dependent pass thresholds; provider availability is stubbed.");
    }

    public static TheoryData<EnrichmentFlagFilter[]> RepresentativeCatalogFlags => new()
    {
        Array.Empty<EnrichmentFlagFilter>(),
        new[] { EnrichmentFlagFilter.NeedsReview },
        new[] { EnrichmentFlagFilter.Automatic },
        new[] { EnrichmentFlagFilter.Blocked },
        new[] { EnrichmentFlagFilter.NeedsReview, EnrichmentFlagFilter.Blocked },
        new[] { EnrichmentFlagFilter.Settled }
    };

    // Each filter is an independent workload. Keep both measurements and the full fixture,
    // while letting the unchanged CI inactivity guard distinguish progress from a hung query.
    [Theory]
    [MemberData(nameof(RepresentativeCatalogFlags))]
    public async Task Read_RepresentativeCatalogFilter_ReportsLatencyAndPreservesAssessments(EnrichmentFlagFilter[] flags)
    {
        await SeedAsync(untrackedTitles: 10_000);
        await SeedArtworkAndHistoryAsync();
        var providers = new[] { AvailableProvider("igdb"), AvailableProvider("steamgriddb") };
        await using var db = database.CreateContext();
        var search = new SearchRepository(db, new ArtworkReader(db), identityAdapters: providers);
        // Labels match the pre-change enrichmentStatus=<flag> baseline for comparison.
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var watch = Stopwatch.StartNew();
            var catalog = (await search.SearchTitlesAsync(null, new TitleSearchFilters(Tracked: TrackedFilter.Tracked, Enrichment: flags.Length == 0 ? null : flags),
                TitleSortField.Name, null, 50, null, default)).Value;
            output.WriteLine($"MEASURE[{repeat}] catalog tracked page enrichment={(flags.Length == 0 ? "(none)" : string.Join("+", flags))}: " +
                $"{watch.ElapsedMilliseconds} ms ({catalog.Items.Count} items)");
            catalog.Items.All(item => flags.All(flag => Carries(item.EnrichmentAssessment.ShouldNotBeNull(), flag))).ShouldBeTrue(string.Join("+", flags));
        }
    }

    private static ContractEnrichmentRequirement[] Open(EnrichmentAssessment assessment) =>
        assessment.Requirements.Where(x => x.State != EnrichmentIssueState.Satisfied).ToArray();

    private static bool Carries(EnrichmentSummary summary, EnrichmentFlagFilter flag) => flag switch
    {
        EnrichmentFlagFilter.NeedsReview => summary.NeedsReview,
        EnrichmentFlagFilter.Blocked => summary.HasOperationalBlocker,
        EnrichmentFlagFilter.Processing => summary.HasActiveWork,
        EnrichmentFlagFilter.Automatic => summary.CanEnrichAutomatically,
        _ => !summary.NeedsReview && !summary.HasOperationalBlocker && !summary.HasActiveWork && !summary.CanEnrichAutomatically
    };

    private static string[] TrustedIdentities(EnrichmentAssessment assessment) =>
        assessment.Requirements.Where(x => x.Kind == "Identity" && x.SatisfiedBy == EnrichmentRequirementSatisfiedBy.Trusted)
            .Select(x => x.Subject).ToArray();

    private static IProviderIdentityAdapter AvailableProvider(string id)
    {
        var provider = Substitute.For<IProviderIdentityAdapter>();
        provider.Id.Returns(id);
        provider.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        return provider;
    }

    private async Task<(Guid HistoryId, Guid ActiveId)> SeedArtworkAndHistoryAsync()
    {
        await using var db = database.CreateContext();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(set => set.SetProperty(x => x.FillPosters, true)
            .SetProperty(x => x.FillHeroes, true).SetProperty(x => x.FillLogos, true).SetProperty(x => x.FillBackdrops, true));
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO romd."TitleExternalIds" ("TitleId","Provider","ExternalId","IsConfirmed","MatchConfidence","CreatedAt","CreatedByUserId")
            SELECT "Id",'steamgriddb',"Id"::text,true,1,0,'00000000-0000-0000-0000-000000000000'::uuid FROM romd."Titles";
            INSERT INTO romd."ArtworkAssets" ("TitleId","Role","SourceId","ProviderGameId","ProviderAssetId","OriginalFileId","ContentVersion","ContentType","Width","Height","IsRejected","IsEligible","CreatedAt")
            SELECT t."Id",r.role,'steamgriddb',t."Id"::text,t."Id"::text,1,'v1','image/png',600,900,false,true,0
            FROM romd."Titles" t CROSS JOIN (VALUES ('Poster'),('Hero'),('Logo'),('Backdrop')) r(role)
            WHERE t."Id" <= 10000 AND t."Id" % 2 = 0;
            INSERT INTO romd."ArtworkVariants" ("AssetId","Name","FileId","ContentVersion","ContentType","Width","Height")
            SELECT "Id",'thumbnail',"OriginalFileId",'v1','image/png',120,180 FROM romd."ArtworkAssets";
            INSERT INTO romd."ArtworkSelections" ("TitleId","Role","Revision","Mode","FocalX","FocalY")
            SELECT "Id",'Logo',0,'Empty',50,50 FROM romd."Titles" WHERE "Id" <= 10000 AND "Id" % 7 = 0;
            INSERT INTO romd."ArtworkAcquisitions" ("TitleId","Role","Status","UpdatedAt")
            SELECT "Id",'Poster','Unavailable',0 FROM romd."Titles" WHERE "Id" <= 10000 AND "Id" % 3 = 0;
            """);
        // Explicit confirmation of the second provider changes identity evidence.
        for (var first = 1; first <= 20_000; first += 500)
        {
            var last = first + 499;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE romd."TitleProviderMatchStates" SET "InputFingerprint" = romd.provider_identity_fingerprint("TitleId",{ProviderIdentityPolicy.Version})
                WHERE "TitleId" BETWEEN {first} AND {last};
                """);
        }
        var histories = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var active = Guid.NewGuid();
        foreach (var id in histories.Append(active))
        {
            var row = ArtworkCompletionJobEntity.FromDomain(ArtworkCompletionJob.Create(id, id == active ? 500 : TrackedTitles, null, createdAt: DateTimeOffset.UtcNow));
            row.ScopeJson = JsonSerializer.Serialize(new ArtworkCompletionRequest(Guid.Empty, Scope: "AllTracked", Mode: "Enrich"));
            row.Phase = id == active ? "Enriching" : "CompletedWithErrors";
            row.Processed = id == active ? 0 : TrackedTitles;
            db.Add(row);
        }
        await db.SaveChangesAsync();
        foreach (var id in histories)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."ArtworkCompletionItems" ("JobId","TitleId","TitleName","RequireTracked","CompletedAt")
                SELECT {id},"Id","Name",true,0 FROM romd."Titles" WHERE "Id" <= {TrackedTitles};
                INSERT INTO romd."ArtworkCompletionResults" ("JobId","TitleId","Role","Outcome")
                SELECT {id},t."Id",r.role,CASE WHEN t."Id" % 3 = 0 THEN 'Failed' ELSE 'Updated' END
                FROM romd."Titles" t CROSS JOIN (VALUES ('Poster'),('Hero'),('Logo'),('Backdrop')) r(role) WHERE t."Id" <= {TrackedTitles};
                """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."ArtworkCompletionItems" ("JobId","TitleId","TitleName","RequireTracked")
            SELECT {active},"Id","Name",true FROM romd."Titles" WHERE "Id" <= 500;
            ANALYZE romd."ArtworkCompletionItems";
            ANALYZE romd."ArtworkCompletionResults";
            ANALYZE romd."ArtworkAssets";
            ANALYZE romd."ArtworkSelections";
            ANALYZE romd."ArtworkAcquisitions";
            ANALYZE romd."Jobs";
            ANALYZE romd."TitleExternalIds";
            ANALYZE romd."TitleProviderMatchStates";
            """);
        return (histories[0], active);
    }

    private async Task SeedAsync(int untrackedTitles = UntrackedTitles)
    {
        await using var db = database.CreateContext();
        db.Users.Add(new Romd.Persistence.Identity.RomdUser { Id = actor, UserName = "benchmark-curator", CreatedAt = DateTimeOffset.UtcNow });
        db.Platforms.Add(new PlatformEntity { Id = 1, Name = "Test system", ShortName = "test" });
        db.Files.Add(new FileEntityPersistence { Id = 1, Sha256 = Sha256.FromBytes(new byte[Sha256.ByteLength]), Size = 1, SizeOnDisk = 1 });
        db.DatSources.Add(new DatSourceEntity { Id = 1, CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" } });
        db.DatFiles.Add(new DatFileEntity
        {
            Id = 1,
            DatSourceId = 1,
            Name = "Local catalog",
            Description = "Identity evidence",
            Type = "NoIntro",
            PlatformId = 1,
            OriginalFilename = "local.dat",
            FileId = 1
        });
        await db.SaveChangesAsync();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(s => s.SetProperty(x => x.FillPosters, false)
            .SetProperty(x => x.FillHeroes, false).SetProperty(x => x.FillLogos, false).SetProperty(x => x.FillBackdrops, false));
        var total = TrackedTitles + untrackedTitles;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."Titles" ("Id","PlatformId","Name","NormalizedName","Revision","EnrichmentStatus","CreatedAt","CreatedByUserId","HasLocalPayload","RetainWithoutCatalog","FieldProvenanceJson")
            SELECT n,1,'Game '||n,'GAME '||n,gen_random_uuid(),'None',0,'00000000-0000-0000-0000-000000000000'::uuid,false,false,json_build_object()::text
            FROM generate_series(1,{total}) n;
            INSERT INTO romd."TrackedTitles" ("TitleId","CreatedAt","UpdatedAt")
            SELECT n,0,0 FROM generate_series(1,{TrackedTitles}) n;
            INSERT INTO romd."SourceEntries" ("Id","CatalogSourceId","EntryKey","Name","PlatformId","HasLocalPayload","CreatedAt","CreatedByUserId")
            SELECT n,1,'release-'||n,'Game '||((n-1)%{total}+1)||CASE WHEN n>{total} THEN ' (USA)' ELSE '' END,1,false,0,'00000000-0000-0000-0000-000000000000'::uuid
            FROM generate_series(1,{total * 2}) n;
            INSERT INTO romd."DatGames" ("Id","DatFileId","SourceEntryId","Name","Year","IsBios","CreatedAt","CreatedByUserId")
            SELECT "Id",1,"Id","Name",(1990+("Id"-1)%25)::text,false,0,'00000000-0000-0000-0000-000000000000'::uuid FROM romd."SourceEntries";
            INSERT INTO romd."TitleSourceLinks" ("SourceEntryId","TitleId","CreatedAt","CreatedByUserId")
            SELECT n,(n-1)%{total}+1,0,'00000000-0000-0000-0000-000000000000'::uuid FROM generate_series(1,{total * 2}) n;
            INSERT INTO romd."TitleExternalIds" ("TitleId","Provider","ExternalId","IsConfirmed","MatchConfidence","CreatedAt","CreatedByUserId")
            SELECT n,'igdb',n::text,n%5<>0,1,0,'00000000-0000-0000-0000-000000000000'::uuid
            FROM generate_series(1,{total}) n WHERE n%5<>1;
            ANALYZE romd."Titles";
            ANALYZE romd."TrackedTitles";
            ANALYZE romd."SourceEntries";
            ANALYZE romd."DatGames";
            ANALYZE romd."TitleSourceLinks";
            ANALYZE romd."TitleExternalIds";
            """);
        // Bound each seed statement without changing command timeouts. The canonical SQL
        // fingerprint, rather than a test reimplementation, supplies the persisted evidence.
        for (var first = 1; first <= total; first += 500)
        {
            var last = Math.Min(first + 499, total);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."TitleProviderMatchStates" ("TitleId","ProviderId","Revision","GameJson","DecisionJson","InputFingerprint","MatcherVersion","AutomaticallyTrusted","SuppressAutomaticMatch","IdentityChanged","LastAttemptAt","Evidence")
                SELECT n,'igdb',gen_random_uuid(),CASE WHEN n%5=0 THEN jsonb_build_object('Id',n::text,'Name','Game '||n)::text ELSE NULL END,
                  jsonb_build_object('State',CASE WHEN n%5=0 THEN 'AutoMatched' ELSE 'NoMatch' END,'Explanation','Local catalog evidence','Candidates',jsonb_build_array(),'Dependencies',jsonb_build_array())::text,
                  romd.provider_identity_fingerprint(n,{ProviderIdentityPolicy.Version}),{ProviderIdentityPolicy.Version},n%5=0,false,false,0,'Local names, year, and system'
                FROM generate_series({first},{last}) n WHERE n%5 IN (0,1);
                """);
        }
        await db.Database.ExecuteSqlRawAsync("ANALYZE romd.\"TitleProviderMatchStates\";");
        (await db.Set<TitleProviderMatchStateEntity>().CountAsync(x => x.TitleId <= TrackedTitles && x.AutomaticallyTrusted)).ShouldBe(2_000);
        (await db.DatGames.CountAsync(x => !x.IsBios)).ShouldBe(total * 2);
    }

    public void Dispose() => database.Dispose();
}
