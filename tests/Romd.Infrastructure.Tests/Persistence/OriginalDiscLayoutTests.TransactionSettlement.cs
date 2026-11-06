using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Application.Common.Ids;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class OriginalDiscLayoutTests
{
    [Theory]
    [InlineData("none", false)]
    [InlineData("before", false)]
    [InlineData("after", false)]
    [InlineData("before", true)]
    [InlineData("after", true)]
    public async Task Decision_ProviderCommitAndCleanup_PreserveKnownOutcomeAndAtomicReconciliation(string fault, bool cancelled)
    {
        var original = await Intake();
        await using var db = new RomdDbContext(database.CreateOptions(options =>
            options.ReplaceService<IRelationalTransactionFactory, ProviderTransactionFaults.Factory>()));
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancelled ? new OperationCanceledException("Original layout cancellation", cancellation.Token)
            : new IOException("Original layout commit failure");
        var faults = new ProviderTransactionFaults(fault, primary, cancelled ? cancellation.Cancel : null);
        faults.Register(db);
        Exception? observed = null;

        try
        {
            var result = await DecideAsync(db, Decode(original.Id), new(original.Revision, "confirm", IdCoder.Encode(1),
                [new("track.bin", IdCoder.Encode(1))], "Reviewed"), actor, cancellation.Token);
            result.IsError.ShouldBeFalse();
            result.Value.State.ShouldBe("accepted");
            result.Value.Revision.ShouldNotBe(original.Revision);
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
        await db.SaveChangesAsync(); // Disposed failure must not leave a later flush able to publish rejected staging.
        await using var read = database.CreateContext();
        var stored = await read.OriginalDiscLayouts.SingleAsync();
        bool durable = fault != "before";
        stored.State.ShouldBe(durable ? "accepted" : "pending");
        if (durable)
            stored.Revision.ShouldNotBe(original.Revision);
        else
            stored.Revision.ShouldBe(original.Revision);
        (await read.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(durable ? 1 : 0);
        (await read.Set<MediaReconciliationJobEntity>().CountAsync()).ShouldBe(durable ? 1 : 0);
        (await read.JobDispatches.CountAsync()).ShouldBe(durable ? 1 : 0);
        if (durable)
        {
            var audit = await read.Set<OriginalDiscLayoutAuditEntity>().SingleAsync(row => row.Revision == stored.Revision);
            audit.Actor.ShouldBe(actor);
            audit.Reason.ShouldBe("Reviewed");
            (await read.JobDispatches.Select(row => row.JobId).SingleAsync())
                .ShouldBe(await read.Set<MediaReconciliationJobEntity>().Select(row => row.Id).SingleAsync());
        }
    }

    [Fact]
    public async Task Decision_BusinessRejectionWithoutPrimary_StillPropagatesProviderCleanupFailure()
    {
        var original = await Intake();
        await using var db = new RomdDbContext(database.CreateOptions(options =>
            options.ReplaceService<IRelationalTransactionFactory, ProviderTransactionFaults.Factory>()));
        var faults = new ProviderTransactionFaults();
        faults.Register(db);
        Exception? observed = null;

        try
        { await DecideAsync(db, Decode(original.Id), new(original.Revision, "INVALID", null, null, null), actor, default); }
        catch (Exception error) { observed = error; }

        observed.ShouldBeSameAs(faults.CleanupFailure);
        faults.Commits.ShouldBe(0);
        faults.Disposals.ShouldBe(1);
        await using var read = database.CreateContext();
        (await read.OriginalDiscLayouts.SingleAsync()).Revision.ShouldBe(original.Revision);
        (await read.JobDispatches.CountAsync()).ShouldBe(0);
    }
}
