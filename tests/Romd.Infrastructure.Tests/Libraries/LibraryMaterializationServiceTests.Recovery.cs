using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Libraries;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Libraries;

public sealed partial class LibraryMaterializationServiceTests
{
    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(false, "fault")]
    [InlineData(true, "fault")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "fence")]
    [InlineData(true, "fence")]
    [InlineData(false, "commit-before")]
    [InlineData(true, "commit-before")]
    [InlineData(false, "commit-after")]
    [InlineData(true, "commit-after")]
    public async Task MaterializeAsync_RealSettlementCleanup_PreservesDurabilityAndPrimary(bool invalid, string failure)
    {
        await using var database = await CreateDatabaseAsync();
        var db = database.Context;
        var revision = Guid.NewGuid();
        db.Libraries.Add(new LibraryEntity
        {
            Id = 7,
            Name = "Settlement",
            ConfigurationJson = "{}",
            ConfigurationState = (invalid ? LibraryConfigurationState.Invalid : LibraryConfigurationState.Valid).ToString(),
            ConfigurationError = invalid ? "bad reference" : null,
            NeedsMaterialization = true,
            MaterializationRevision = revision,
            ItemCount = 3,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        using var cancellation = new CancellationTokenSource();
        Exception primary = failure switch
        {
            "cancel" => new OperationCanceledException("Original materialization cancellation.", null, cancellation.Token),
            "fence" => new JobExecutionOwnershipLostException(),
            _ => new IOException("Original materialization failure.")
        };
        var work = new OwnerTransactionFaults(new EfUnitOfWork(db),
            failure.StartsWith("commit-", StringComparison.Ordinal) ? failure : failure == "success" ? "none" : "flush",
            primary, beforeFault: failure == "cancel" ? cancellation.Cancel : null);
        var provider = Substitute.For<IMaterializationDataProvider>();
        provider.GetCandidatesAsync(Arg.Any<LibraryConfiguration>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<TitleCandidates>());
        var service = new LibraryMaterializationService(new LibraryRepository(db), provider, CleanCatalogProjection(),
            new AdminRealtimeOutbox(db, TimeProvider.System), work, NullLogger<LibraryMaterializationService>.Instance);

        if (failure == "success")
            (await service.MaterializeAsync(7, cancellation.Token)).Outcome.ShouldBe(MaterializationOutcome.Materialized);
        else
        {
            Exception? observed = null;
            try
            { await service.MaterializeAsync(7, cancellation.Token); }
            catch (Exception error) { observed = error; }
            observed.ShouldBeSameAs(primary);
            if (observed is OperationCanceledException cancelled)
                cancelled.CancellationToken.ShouldBe(cancellation.Token);
            work.Injected.ShouldBeTrue();
            work.RollbackAttempted.ShouldBeTrue();
        }
        work.Disposed.ShouldBeTrue();
        bool durable = failure is "success" or "commit-after";
        if (!durable)
            db.ChangeTracker.Entries().ShouldBeEmpty();
        await db.SaveChangesAsync();
        await using var verify = new RomdDbContext(new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(db.Database.GetConnectionString()!).Options);
        var row = await verify.Libraries.SingleAsync();
        row.NeedsMaterialization.ShouldBe(!durable);
        row.ItemCount.ShouldBe(durable ? 0 : 3);
        if (durable)
            row.MaterializationRevision.ShouldNotBe(revision);
        else
            row.MaterializationRevision.ShouldBe(revision);
        row.MaterializationGeneration.ShouldBe(durable && !invalid ? 1 : 0);
        (await verify.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(durable ? 1 : 0);
    }
}
