using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Romd.Admin.Application.Common.Persistence;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.MediaFacts;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class MediaFactPublisherTests
{
    [Theory]
    [InlineData("before-rollback")]
    [InlineData("after-rollback")]
    [InlineData("release")]
    public async Task Publish_CancelledAfterActualInsertAndSavepointRecoveryFails_PreservesCancellationAndOuterRollback(string failure)
    {
        using var cancellation = new CancellationTokenSource();
        var cancel = new CancelAfterMediaInsert("INSERT INTO romd.\"Representations\"",
            "SELECT count(*) FROM romd.\"Representations\"", cancellation);
        await using var db = new RomdDbContext(_database.CreateOptions(options => options
            .ReplaceService<IRelationalTransactionFactory, CatalogTransactionFaults.Factory>().AddInterceptors(cancel)));
        var faults = SavepointFaults(failure);
        faults.Register(db);
        Exception? observed = null;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var publisher = new MediaFactPublisher(db);
            var identity = Identity(100, 'a');
            int rom = await publisher.CreateLogicalAsync("disc.iso", identity, Guid.Empty, Observed);
            var container = Container("Rvz");
            try
            {
                await publisher.PublishAsync(1, container, RomReconstructionManifest.WholeFile.Instance,
                    Proof(container, [new(RomOutputSelector.Whole.Instance, identity)]),
                    [new(RomOutputSelector.Whole.Instance, rom, true)], [], cancellation.Token);
            }
            catch (Exception error) { observed = error; }
            observed.ShouldBeSameAs(cancel.Primary);
            cancel.Primary.CancellationToken.ShouldBe(cancellation.Token);
            cancel.Primary.Data["Romd.MediaFacts.SavepointRecoveryFailure"].ShouldBeSameAs(
                failure == "release" ? faults.SavepointReleaseFailure : faults.SavepointFailure);
            await transaction.RollbackAsync(CancellationToken.None);
        }

        cancel.Reached.ShouldBeTrue();
        cancel.InsertedRows.ShouldBe(1);
        faults.CommitAttempts.ShouldBe(0);
        faults.RollbackAttempts.ShouldBe(1);
        faults.SavepointRollbackAttempts.ShouldBe(1);
        faults.Disposals.ShouldBe(1);
        await AssertAbortedMediaPublicationAsync();
    }

    [Theory]
    [InlineData("before-rollback")]
    [InlineData("after-rollback")]
    [InlineData("release")]
    public async Task Publish_ConstraintAndSavepointRecoveryFail_PropagatesRecoverySignalAndAbortsOuterTransaction(string failure)
    {
        await using var db = new RomdDbContext(_database.CreateOptions(options =>
            options.ReplaceService<IRelationalTransactionFactory, CatalogTransactionFaults.Factory>()));
        var faults = SavepointFaults(failure);
        faults.Register(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var publisher = new MediaFactPublisher(db);
            var identity = Identity(100, 'a');
            int rom = await publisher.CreateLogicalAsync("disc.iso", identity, Guid.Empty, Observed);
            var container = Container("Rvz");
            var wrong = new RomRepresentationVerification(Guid.NewGuid(), Hash('d'), 512,
                new RomDecoderIdentity.Managed("bad"), "measured/test", "verifier/test", Observed,
                [new(RomOutputSelector.Whole.Instance, identity)], container);

            var observed = await Record.ExceptionAsync(() => publisher.PublishAsync(1, container,
                RomReconstructionManifest.WholeFile.Instance, wrong, [new(RomOutputSelector.Whole.Instance, rom, true)], []));

            observed.ShouldBeSameAs(failure == "release" ? faults.SavepointReleaseFailure : faults.SavepointFailure);
            var primary = observed!.Data["Romd.MediaFacts.PublicationFailure"].ShouldBeOfType<PostgresException>();
            primary.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            new EfUnitOfWork(db).ConflictOf(observed!).ShouldBe(TransactionConflict.None);
            // A recovery failure does not advertise the safe same-transaction reuse established
            // by Publish_ArtifactGuardRejects_RollsBackAttemptAndAllowsValidPublicationInSameTransaction.
            await transaction.RollbackAsync(CancellationToken.None);
        }

        faults.CommitAttempts.ShouldBe(0);
        faults.RollbackAttempts.ShouldBe(1);
        faults.SavepointRollbackAttempts.ShouldBe(1);
        await AssertAbortedMediaPublicationAsync();
    }

    private static CatalogTransactionFaults SavepointFaults(string failure) => new(
        failBeforeSavepointRollback: failure == "before-rollback", failSavepointRollback: failure == "after-rollback",
        failSavepointRelease: failure == "release", failDisposal: false);

    private async Task AssertAbortedMediaPublicationAsync()
    {
        await using var verify = _database.CreateContext();
        (await verify.RomFiles.CountAsync()).ShouldBe(0);
        (await verify.RomRepresentations.CountAsync()).ShouldBe(0);
        (await verify.RepresentationOutputs.CountAsync()).ShouldBe(0);
        (await verify.JobItems.CountAsync()).ShouldBe(0);
        // Only the fixture's existing immutable physical ledger remains.
        (await verify.Files.CountAsync()).ShouldBe(3);
    }
}
