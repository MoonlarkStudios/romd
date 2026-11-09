using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NSubstitute;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.ReferenceData;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed class ReferenceMutationSettlementTests
{
    [Fact]
    public async Task Run_AcknowledgedCommitAndDisposalFault_ReportsWarningAndRetainsDurableSuccess()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await SeedAsync(database);
        await using var db = FaultedContext(database);
        var faults = new CatalogTransactionFaults();
        faults.Register(db);
        var logger = Substitute.For<ILogger<ReferenceMutationSession>>();
        var session = new ReferenceMutationSession(db, logger);

        var result = await session.RunAsync<bool>(async token =>
        {
            (await db.Companies.AsTracking().SingleAsync(token)).BaseName = "Renamed maker";
            await session.FlushAsync(token);
            return true;
        }, default);

        result.Value.ShouldBeTrue();
        logger.ReceivedCalls().ShouldContain(call => call.GetArguments().Contains(faults.CleanupFailure));
        faults.ProviderCommits.ShouldBe(1);
        faults.Disposals.ShouldBe(1);
        await AssertPublishedNameAsync(database, "Renamed maker");
    }

    [Fact]
    public async Task UpdateCompany_AcknowledgedCommitAndDisposalFault_ReturnsDurableFactsAndSnapshot()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await SeedAsync(database);
        await using var db = FaultedContext(database);
        var faults = new CatalogTransactionFaults();
        faults.Register(db);
        var commands = new TypedReferenceTestCommands(db);
        var original = (await commands.Company("local-maker"))!;

        var result = await commands.UpdateCompany("local-maker", new(Name: "Renamed maker"), original.Revision);

        result.IsError.ShouldBeFalse();
        result.Value.Resource.Name.ShouldBe("Renamed maker");
        result.Value.Revision.ShouldNotBe(original.Revision);
        faults.CommitAttempts.ShouldBe(1);
        faults.ProviderCommits.ShouldBe(1);
        faults.RollbackAttempts.ShouldBe(0);
        faults.Disposals.ShouldBe(1);
        db.Database.CurrentTransaction.ShouldBeNull();
        await AssertPublishedNameAsync(database, "Renamed maker");
    }

    [Theory]
    [InlineData("before", false)]
    [InlineData("after", false)]
    [InlineData("before", true)]
    [InlineData("after", true)]
    public async Task UpdateCompany_CommitOutcomeAndCleanupFaults_PreservePrimaryWithoutReplaying(string commitFault, bool cancelled)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await SeedAsync(database);
        await using var db = FaultedContext(database);
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancelled ? new OperationCanceledException("Original reference commit cancellation", cancellation.Token)
            : new IOException("Original reference commit failure");
        var faults = new CatalogTransactionFaults(commitFault, primary, cancelled ? cancellation.Cancel : null, failRollback: true);
        faults.Register(db);
        var commands = new TypedReferenceTestCommands(db);
        var original = (await commands.Company("local-maker"))!;

        var observed = await Record.ExceptionAsync(() => commands.UpdateCompany("local-maker", new(Name: "Renamed maker"), original.Revision, cancellation.Token));

        observed.ShouldBeSameAs(primary);
        if (cancelled)
            ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
        primary.Data["Romd.ReferenceMutation.RollbackFailure"].ShouldBeSameAs(faults.RollbackFailure);
        primary.Data["Romd.Transaction.CleanupFailure"].ShouldBeSameAs(faults.CleanupFailure);
        faults.CommitAttempts.ShouldBe(1);
        faults.ProviderCommits.ShouldBe(commitFault == "after" ? 1 : 0);
        faults.RollbackAttempts.ShouldBe(1);
        faults.Disposals.ShouldBe(1);
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertPublishedNameAsync(database, commitFault == "after" ? "Renamed maker" : "Maker");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_ActualWriteThenPrimaryAndRollbackFaults_PreservesCauseAndClearsTrackedState(bool cancelled)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await SeedAsync(database);
        await using var db = FaultedContext(database);
        var faults = new CatalogTransactionFaults(failRollback: true);
        faults.Register(db);
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancelled ? new OperationCanceledException("Original reference mutation cancellation", cancellation.Token)
            : new IOException("Original reference mutation failure");
        var session = new ReferenceMutationSession(db);

        var observed = await Record.ExceptionAsync(() => session.RunAsync<bool>(async token =>
        {
            db.Database.CurrentTransaction!.GetDbTransaction().IsolationLevel.ShouldBe(System.Data.IsolationLevel.Serializable);
            (await db.Platforms.Where(row => row.CanonicalKey == "local-machine")
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.Manufacturer, "Uncommitted"), token)).ShouldBe(1);
            (await db.Companies.AsTracking().SingleAsync(token)).BaseDescription = "Unflushed";
            if (cancelled)
                cancellation.Cancel();
            throw primary;
        }, cancellation.Token, protectDependencies: true));

        observed.ShouldBeSameAs(primary);
        if (cancelled)
            ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
        primary.Data["Romd.ReferenceMutation.RollbackFailure"].ShouldBeSameAs(faults.RollbackFailure);
        primary.Data["Romd.Transaction.CleanupFailure"].ShouldBeSameAs(faults.CleanupFailure);
        faults.CommitAttempts.ShouldBe(0);
        faults.RollbackAttempts.ShouldBe(1);
        faults.Disposals.ShouldBe(1);
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertPublishedNameAsync(database, "Maker");
        await using var verify = database.CreateContext();
        (await verify.Companies.SingleAsync()).BaseDescription.ShouldBeNull();
    }

    [Fact]
    public async Task Run_ActualForeignKeyFailureAndCleanupFaults_RetainsConflictTranslationAndRollsBack()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await SeedAsync(database);
        await using var db = FaultedContext(database);
        var faults = new CatalogTransactionFaults(failRollback: true);
        faults.Register(db);
        PostgresException? original = null;

        var result = await new ReferenceMutationSession(db).RunAsync<bool>(async token =>
        {
            (await db.Platforms.Where(row => row.CanonicalKey == "local-machine")
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.Manufacturer, "Uncommitted"), token)).ShouldBe(1);
            try
            {
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO romd."SystemCompanies" ("PlatformId", "CompanyKey")
                    SELECT "Id", 'local-missing' FROM romd."Platforms" WHERE "CanonicalKey" = 'local-machine'
                    """, token);
                throw new InvalidOperationException("The missing company must reject the relationship.");
            }
            catch (PostgresException error) { original = error; throw; }
        }, default, protectDependencies: true);

        result.FirstError.Code.ShouldBe("ReferenceResource.Conflict");
        original.ShouldNotBeNull();
        original.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        original.Data["Romd.ReferenceMutation.RollbackFailure"].ShouldBeSameAs(faults.RollbackFailure);
        original.Data["Romd.Transaction.CleanupFailure"].ShouldBeSameAs(faults.CleanupFailure);
        faults.CommitAttempts.ShouldBe(0);
        faults.RollbackAttempts.ShouldBe(1);
        faults.Disposals.ShouldBe(1);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertPublishedNameAsync(database, "Maker");
    }

    [Fact]
    public async Task Run_BusinessRejectionAndDisposalFault_DoesNotInventCommitOrSuppressCleanupOnlyFailure()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await SeedAsync(database);
        await using var db = FaultedContext(database);
        var faults = new CatalogTransactionFaults();
        faults.Register(db);

        var observed = await Record.ExceptionAsync(() => new ReferenceMutationSession(db).RunAsync<bool>(async token =>
        {
            (await db.Companies.AsTracking().SingleAsync(token)).BaseName = "Unflushed";
            return Error.Conflict("ReferenceResource.Conflict", "Rejected before publication.");
        }, default));

        observed.ShouldBeSameAs(faults.CleanupFailure);
        faults.CommitAttempts.ShouldBe(0);
        faults.RollbackAttempts.ShouldBe(1);
        faults.Disposals.ShouldBe(1);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertPublishedNameAsync(database, "Maker");
    }

    private static RomdDbContext FaultedContext(PostgreSqlTestDatabase database) => new(database.CreateOptions(options =>
        options.ReplaceService<IRelationalTransactionFactory, CatalogTransactionFaults.Factory>()));

    private static async Task SeedAsync(PostgreSqlTestDatabase database)
    {
        await using var db = database.CreateContext();
        var commands = new TypedReferenceTestCommands(db);
        (await commands.CreateCompany(new("local-maker", "Maker"))).IsError.ShouldBeFalse();
        (await commands.CreateSystem(new("local-machine", "Machine", "MCH", ManufacturerKeys: ["local-maker"]))).IsError.ShouldBeFalse();
    }

    private static async Task AssertPublishedNameAsync(PostgreSqlTestDatabase database, string expected)
    {
        await using var db = database.CreateContext();
        var commands = new TypedReferenceTestCommands(db);
        (await commands.Company("local-maker"))!.Resource.Name.ShouldBe(expected);
        (await commands.System("local-machine"))!.Resource.Manufacturers.ShouldHaveSingleItem().Name.ShouldBe(expected);
        (await db.Platforms.SingleAsync()).Manufacturer.ShouldBe(expected);
        var snapshot = JsonSerializer.Deserialize<ReferenceCatalog>((await db.ReferenceCatalogState.SingleAsync()).Json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        snapshot.Companies.ShouldHaveSingleItem().Name.ShouldBe(expected);
        snapshot.Systems.ShouldHaveSingleItem().Manufacturers.ShouldHaveSingleItem().Name.ShouldBe(expected);
    }
}
