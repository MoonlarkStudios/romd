using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Commands.BackfillBiosCatalog;
using Romd.Admin.Application.Taxonomy;
using Romd.Domain.Taxonomy;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class ApplicationTransactionSettlementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backfill_ActualSqlThenFailure_RollsBackAndPreservesPrimaryThroughProviderDisposal(bool cancelled)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using (var seed = database.CreateContext())
        {
            seed.Platforms.Add(new PlatformEntity { Id = 1, Name = "Original", ShortName = "original" });
            await seed.SaveChangesAsync();
        }
        await using var db = new RomdDbContext(database.CreateOptions(options =>
            options.ReplaceService<IRelationalTransactionFactory, ProviderTransactionFaults.Factory>()));
        var faults = new ProviderTransactionFaults();
        faults.Register(db);
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancelled ? new OperationCanceledException("Original BIOS cancellation", cancellation.Token)
            : new IOException("Original BIOS SQL failure");
        var reader = Substitute.For<IBiosRepository>();
        reader.GetUnmappedBiosGamesAsync(Arg.Any<CancellationToken>()).Returns(new List<UnmappedBiosGame> { new(1, 1, "[BIOS] Example") });
        var grouper = Substitute.For<IBiosGrouper>();
        async Task<int> StageAndFail()
        {
            (await db.Platforms.Where(row => row.Id == 1).ExecuteUpdateAsync(update => update.SetProperty(row => row.Name, "Uncommitted")))
                .ShouldBe(1);
            if (cancelled)
                cancellation.Cancel();
            throw primary;
        }
        grouper.GroupAsync(1, Arg.Any<IReadOnlyList<(int, string)>>(), Arg.Any<CancellationToken>()).Returns(_ => StageAndFail());
        var handler = new BackfillBiosCatalogCommandHandler(reader, grouper, new EfUnitOfWork(db), NullLogger<BackfillBiosCatalogCommandHandler>.Instance);
        Exception? observed = null;

        try
        {
            var result = await handler.HandleAsync(new(), cancellation.Token);
            cancelled.ShouldBeFalse();
            result.FirstError.Code.ShouldBe("Catalog.DatabaseFailed");
            result.FirstError.Description.ShouldContain(primary.Message);
            result.FirstError.Description.ShouldNotContain(faults.CleanupFailure.Message);
        }
        catch (Exception error) { observed = error; }

        if (cancelled)
        {
            observed.ShouldBeSameAs(primary);
            ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
        }
        else
            observed.ShouldBeNull();
        faults.Commits.ShouldBe(0);
        faults.Disposals.ShouldBe(1);
        db.Database.CurrentTransaction.ShouldBeNull();
        await using var verify = database.CreateContext();
        (await verify.Platforms.SingleAsync()).Name.ShouldBe("Original");
    }

    public static TheoryData<string, string, bool> TaxonomyCases
    {
        get
        {
            var cases = new TheoryData<string, string, bool>();
            foreach (string operation in new[] { "merge", "add", "remove" })
            {
                cases.Add(operation, "none", false);
                cases.Add(operation, "before", false);
                cases.Add(operation, "after", false);
                cases.Add(operation, "before", true);
                cases.Add(operation, "after", true);
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(TaxonomyCases))]
    public async Task Taxonomy_RealMutationCommitAndCleanup_PreserveAliasesAndOriginalOutcome(string operation, string fault, bool cancelled)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using (var seed = database.CreateContext())
        {
            seed.Regions.AddRange(new RegionEntity { Id = 1, Name = "Source", CanonicalKey = "dat-source" },
                new RegionEntity { Id = 2, Name = "Target", CanonicalKey = "dat-target" });
            seed.RegionAliases.Add(new RegionAliasEntity { Id = 1, RegionId = 1, NormalizedAlias = "existing" });
            await seed.SaveChangesAsync();
        }
        await using var db = new RomdDbContext(database.CreateOptions(options =>
            options.ReplaceService<IRelationalTransactionFactory, ProviderTransactionFaults.Factory>()));
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancelled ? new OperationCanceledException("Original taxonomy cancellation", cancellation.Token)
            : new IOException("Original taxonomy commit failure");
        var faults = new ProviderTransactionFaults(fault, primary, cancelled ? cancellation.Cancel : null);
        faults.Register(db);
        var service = new TaxonomyService<Region>(new RegionRepository(db), new EfUnitOfWork(db), NullLogger<TaxonomyService<Region>>.Instance);
        Exception? observed = null;
        int? createdAliasId = null;

        try
        {
            if (operation == "merge")
                (await service.MergeAsync("dat-source", "dat-target", cancellation.Token)).IsError.ShouldBeFalse();
            else if (operation == "remove")
                (await service.RemoveAliasAsync("dat-source", 1, cancellation.Token)).IsError.ShouldBeFalse();
            else
            {
                var result = await service.AddAliasAsync("dat-target", "  New Alias  ", cancellation.Token);
                result.IsError.ShouldBeFalse();
                createdAliasId = result.Value.Id;
                result.Value.Alias.ShouldBe("new alias");
            }
        }
        catch (Exception error) { observed = error; }

        if (fault == "none")
            observed.ShouldBeNull();
        else
            observed.ShouldBeSameAs(primary);
        if (observed is OperationCanceledException stopped)
            stopped.CancellationToken.ShouldBe(cancellation.Token);
        faults.Disposals.ShouldBe(1);
        faults.Commits.ShouldBe(fault == "before" ? 0 : 1);
        db.Database.CurrentTransaction.ShouldBeNull();
        await db.SaveChangesAsync();
        await using var verify = database.CreateContext();
        bool durable = fault != "before";
        if (operation == "merge")
        {
            (await verify.Regions.AnyAsync(row => row.Id == 1)).ShouldBe(!durable);
            (await verify.RegionAliases.SingleAsync()).RegionId.ShouldBe(durable ? 2 : 1);
        }
        else if (operation == "remove")
            (await verify.RegionAliases.AnyAsync(row => row.Id == 1)).ShouldBe(!durable);
        else
        {
            var alias = await verify.RegionAliases.SingleOrDefaultAsync(row => row.NormalizedAlias == "new alias");
            if (durable)
            {
                alias.ShouldNotBeNull().RegionId.ShouldBe(2);
                if (fault == "none")
                    alias.Id.ShouldBe(createdAliasId.ShouldNotBeNull());
            }
            else
                alias.ShouldBeNull();
        }
    }
}
