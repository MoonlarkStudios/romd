using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Application.Common.Ids;
using Romd.Domain.Catalog;
using Romd.Domain.ReferenceData;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class DiscSetReadBatchTests
{
    [Theory]
    [InlineData(5, false)]
    [InlineData(20, false)]
    [InlineData(5, true)]
    [InlineData(20, true)]
    public async Task List_SharedOrDistinctContexts_UsesThreeQueriesForTheWholePage(int count, bool distinct)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await SeedAsync(database, count, distinct);
        var counter = new QueryCounter();
        await using var db = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(counter)));

        var result = await new DiscSetService(db).ListAsync(null, null, count, default);

        result.IsError.ShouldBeFalse();
        result.Value.Items.Count.ShouldBe(count);
        result.Value.HasNextPage.ShouldBeFalse();
        counter.Reads.ShouldBe(3); // One row scan, one exact-context claim batch, one system-key batch.
        for (int index = 0; index < count; index++)
        {
            int context = distinct ? index + 1 : 1;
            var row = result.Value.Items[index];
            row.Id.ShouldBe(IdCoder.Encode(index + 1));
            row.CatalogSourceId.ShouldBe(IdCoder.Encode(context));
            row.SystemKey.ShouldBe($"system-{context}");
            row.Proposal.Members.Single().ReleaseId.ShouldBe(IdCoder.Encode(context));
            row.Proposal.Members.Single().Issue.ShouldBeNull();
        }
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(20, false)]
    [InlineData(5, true)]
    [InlineData(20, true)]
    public async Task Options_SelectedAndCandidateRows_AreBatchedWithoutChangingSelectionOrContinuation(int count, bool distinct)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await SeedAsync(database, count, distinct);
        var counter = new QueryCounter();
        await using var db = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(counter)));
        var service = new DiscSetService(db);
        IReadOnlySet<int> eligible = Enumerable.Range(1, distinct ? count : 1).ToHashSet();
        int[] selected = [count + 100, count, 1];

        var result = await service.ListLibraryOptionsAsync(7, eligible, selected, "Game", null, 2, default);

        result.IsError.ShouldBeFalse();
        result.Value.SelectedIds.ShouldBe(selected.Select(IdCoder.Encode));
        result.Value.Selected.Select(row => row.Id).ShouldBe(selected.Select(IdCoder.Encode));
        result.Value.Selected[0].Issue.ShouldBe("MissingDiscSet");
        result.Value.Selected.Skip(1).ShouldAllBe(row => row.IsEligible && row.Issue == null);
        result.Value.Page.Items.Select(row => row.Id).ShouldBe([IdCoder.Encode(1), IdCoder.Encode(2)]);
        result.Value.Page.HasNextPage.ShouldBeTrue();
        counter.Reads.ShouldBe(6); // Row/claims/system batches for selected rows and for the candidate scan.
        counter.Reset();

        var next = await service.ListLibraryOptionsAsync(7, eligible, selected, "Game", result.Value.Page.NextCursor, 2, default);

        next.IsError.ShouldBeFalse();
        next.Value.Page.Items.Select(row => row.Id).ShouldBe([IdCoder.Encode(3), IdCoder.Encode(4)]);
        counter.Reads.ShouldBe(6);
    }

    [Fact]
    public async Task Options_IneligibleCandidatesAcrossScanBatches_KeepSelectedRowsAndSeekFromLastReturnedEligibleRow()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await SeedAsync(database, 205, distinct: true);
        var counter = new QueryCounter();
        await using var db = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(counter)));
        var service = new DiscSetService(db);
        IReadOnlySet<int> eligible = new HashSet<int> { 201, 202, 203, 204, 205 };

        var result = await service.ListLibraryOptionsAsync(7, eligible, [1, 205, 999], null, null, 2, default);

        result.IsError.ShouldBeFalse();
        result.Value.Selected[0].Issue.ShouldBe("OutsideEditableLibraryPolicy");
        result.Value.Selected[1].IsEligible.ShouldBeTrue();
        result.Value.Selected[2].Issue.ShouldBe("MissingDiscSet");
        result.Value.Page.Items.Select(row => row.Id).ShouldBe([IdCoder.Encode(201), IdCoder.Encode(202)]);
        result.Value.Page.HasNextPage.ShouldBeTrue();
        counter.Reads.ShouldBe(12); // One selected batch plus three bounded scan batches, never per candidate.
        counter.Reset();

        var next = await service.ListLibraryOptionsAsync(7, eligible, [1, 205, 999], null, result.Value.Page.NextCursor, 2, default);

        next.Value.Page.Items.Select(row => row.Id).ShouldBe([IdCoder.Encode(203), IdCoder.Encode(204)]);
        next.Value.Page.HasNextPage.ShouldBeTrue();
        counter.Reads.ShouldBe(6);
    }

    [Fact]
    public async Task List_MissingOrConflictingClaim_PreservesMemberIssuesAndDoesNotBorrowAnotherContext()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await SeedAsync(database, 5, distinct: true);
        await using (var seed = database.CreateContext())
        {
            await seed.CatalogReleaseSources.Where(row => row.SourceEntryId == 1).ExecuteDeleteAsync();
            await seed.CatalogReleases.Where(row => row.Id == 2).ExecuteUpdateAsync(update => update.SetProperty(row => row.HasTitleConflict, true));
        }
        var counter = new QueryCounter();
        await using var db = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(counter)));

        var result = await new DiscSetService(db).ListAsync(null, null, 5, default);

        result.Value.Items[0].Proposal.Members.Single().Issue.ShouldBe("SourceUnavailable");
        result.Value.Items[1].Proposal.Members.Single().Issue.ShouldBe("TitleConflict");
        result.Value.Items[2].Proposal.Members.Single().ReleaseId.ShouldBe(IdCoder.Encode(3));
        counter.Reads.ShouldBe(3);
    }

    [Fact]
    public async Task ReadBatches_HistoricalPlatformCannotBorrowCurrentClaimForSameSourceAndEntryKey()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await SeedAsync(database, 2, distinct: true);
        await using (var seed = database.CreateContext())
        {
            // There is exactly one source entry for this natural key, currently routed to platform2.
            await seed.CatalogReleaseSources.Where(row => row.SourceEntryId == 1).ExecuteDeleteAsync();
            await seed.SourceEntries.Where(row => row.Id == 1).ExecuteDeleteAsync();
            await seed.SourceEntries.Where(row => row.Id == 2).ExecuteUpdateAsync(update => update.SetProperty(row => row.CatalogSourceId, 1));
            var current = await seed.DiscSets.SingleAsync(row => row.Id == 2);
            await seed.DiscSets.ExecuteUpdateAsync(update => update.SetProperty(row => row.CatalogSourceId, 1)
                .SetProperty(row => row.ProposalJson, current.ProposalJson).SetProperty(row => row.CorrectionJson, current.CorrectionJson)
                .SetProperty(row => row.TitleIds, new[] { 2 }));
        }
        var counter = new QueryCounter();
        await using var db = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(counter)));
        var service = new DiscSetService(db);

        var list = await service.ListAsync(null, null, 5, default);

        var historical = list.Value.Items[0];
        historical.SystemKey.ShouldBe("system-1");
        historical.CatalogSourceId.ShouldBe(IdCoder.Encode(1));
        historical.Proposal.Members.Single().Issue.ShouldBe("SourceUnavailable");
        historical.Correction!.Members.Single().ReleaseId.ShouldBeNull();
        list.Value.Items[1].Proposal.Members.Single().ReleaseId.ShouldBe(IdCoder.Encode(2));
        list.Value.Items[1].Proposal.Members.Single().Issue.ShouldBeNull();
        counter.Reads.ShouldBe(3);
        counter.Reset();

        var options = await service.ListLibraryOptionsAsync(7, new HashSet<int> { 2 }, [1, 2], null, null, 5, default);

        options.Value.Selected[0].IsEligible.ShouldBeFalse();
        options.Value.Selected[0].Issue.ShouldBe("UnresolvedGrouping");
        options.Value.Selected[1].IsEligible.ShouldBeTrue();
        options.Value.Page.Items.Single().Id.ShouldBe(IdCoder.Encode(2));
        options.Value.Page.HasNextPage.ShouldBeFalse();
        counter.Reads.ShouldBe(6);
        (await db.SourceEntries.CountAsync(row => row.CatalogSourceId == 1 && row.EntryKey == "disc")).ShouldBe(1);
    }

    private static async Task SeedAsync(PostgreSqlTestDatabase database, int count, bool distinct)
    {
        await using var db = database.CreateContext();
        for (int id = 1; id <= (distinct ? count : 1); id++)
        {
            db.Platforms.Add(new PlatformEntity
            {
                Id = id,
                Name = $"Platform {id}",
                ShortName = $"p{id}",
                CanonicalKey = $"system-{id}",
                Ownership = ReferenceOwnership.Romd,
                BuiltInVersion = 1,
                BaseName = $"Platform {id}",
                BaseCompactLabel = $"P{id}"
            });
            db.Titles.Add(new TitleEntity { Id = id, PlatformId = id, Name = $"Title {id}", NormalizedName = $"title {id}", EnrichmentStatus = "Completed" });
            db.CatalogSources.Add(new CatalogSourceEntity { Id = id, Kind = "Dat", Status = "Active" });
            db.SourceEntries.Add(new SourceEntryEntity { Id = id, CatalogSourceId = id, PlatformId = id, EntryKey = "disc", Name = $"Disc {id}" });
            db.CatalogReleases.Add(new CatalogReleaseEntity { Id = id, PlatformId = id, CatalogTitleId = id, Fingerprint = $"fp:{id}", Name = $"Release {id}" });
            db.CatalogReleaseSources.Add(new CatalogReleaseSourceEntity { SourceEntryId = id, CatalogReleaseId = id, ProviderClaimKey = $"claim:{id}", AssertedTitleId = id });
        }
        for (int id = 1; id <= count; id++)
        {
            int context = distinct ? id : 1;
            var definition = new DiscSetDefinition($"Game {id}", 1, [new("disc", $"Disc {context}", $"fp:{context}", 1, context)], [], "manual-v1");
            string json = JsonSerializer.Serialize(definition);
            db.DiscSets.Add(new DiscSetEntity
            {
                Id = id,
                PlatformId = context,
                CatalogSourceId = context,
                ProposalKey = $"set-{id}",
                ProposalJson = json,
                CorrectionJson = json,
                Decision = "accepted",
                TitleIds = [context]
            });
        }
        await db.SaveChangesAsync();
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public int Reads { get; private set; }
        public void Reset() => Reads = 0;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Reads++; return ValueTask.FromResult(result); }
    }
}
