using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Jobs;

public sealed class JobTimeTests
{
    private static readonly DateTimeOffset CreatedAt = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.FromHours(5.5)).AddTicks(1234560);
    private static readonly DateTimeOffset StartedAt = CreatedAt.AddSeconds(1);
    private static readonly DateTimeOffset FinishedAt = StartedAt.AddMinutes(2).AddTicks(10);

    [Fact]
    public void Complete_SuppliedTime_PreservesExactCompletionAndDuration()
    {
        var job = BulkEnrichmentJob.Create(1, "platform", CreatedAt);
        job.Start("delivery", StartedAt);
        job.SetCurrentItem("title");
        job.RecordError("title", "first failure", StartedAt.AddSeconds(1));
        job.RecordFailed(7, "title", "second failure", JobErrorReason.ProviderError, StartedAt.AddSeconds(2));

        job.Complete(FinishedAt);

        job.CreatedAt.ShouldBe(CreatedAt);
        job.CreatedAt.Offset.ShouldBe(CreatedAt.Offset);
        job.StartedAt.ShouldBe(StartedAt);
        job.CompletedAt.ShouldBe(FinishedAt);
        job.CompletedAt.ShouldNotBeNull().Offset.ShouldBe(FinishedAt.Offset);
        job.Duration.ShouldBe(FinishedAt - StartedAt);
        job.CurrentItem.ShouldBeNull();
        job.Errors.Select(error => error.OccurredAt).ShouldBe([StartedAt.AddSeconds(1), StartedAt.AddSeconds(2)]);
        job.Errors.Select(error => error.Message).ShouldBe(["first failure", "second failure"]);
        job.PhaseEnum.ShouldBe(BulkEnrichmentPhase.CompletedWithErrors);
    }

    [Fact]
    public void Fail_SuppliedTime_UsesOneInstantForTerminalStateAndErrorAndIgnoresLaterTermination()
    {
        var job = BulkEnrichmentJob.Create(1, "platform", CreatedAt);
        job.Start("delivery", StartedAt);

        job.Fail("failure", FinishedAt);
        job.Fail("later failure", FinishedAt.AddDays(1));
        job.Cancel(FinishedAt.AddDays(2));

        job.CompletedAt.ShouldBe(FinishedAt);
        job.Errors.ShouldHaveSingleItem().OccurredAt.ShouldBe(FinishedAt);
        job.Errors[0].Message.ShouldBe("failure");
        job.PhaseEnum.ShouldBe(BulkEnrichmentPhase.Failed);
    }

    [Fact]
    public void CancelAndArchive_SuppliedTimes_PreserveTerminalInstantAndArchiveState()
    {
        var job = BulkEnrichmentJob.Create(1, "platform", CreatedAt);
        var archivedAt = FinishedAt.AddHours(1);

        Should.Throw<InvalidOperationException>(() => job.Archive(archivedAt));
        job.IsArchived.ShouldBeFalse();
        job.ArchivedAt.ShouldBeNull();
        job.Cancel(FinishedAt);
        job.Cancel(FinishedAt.AddDays(1));
        job.Archive(archivedAt);

        job.CompletedAt.ShouldBe(FinishedAt);
        job.ArchivedAt.ShouldBe(archivedAt);
        job.IsArchived.ShouldBeTrue();
        job.Errors.ShouldBeEmpty();
        job.Unarchive();
        job.IsArchived.ShouldBeFalse();
        job.ArchivedAt.ShouldBeNull();
        job.CompletedAt.ShouldBe(FinishedAt);
    }

    [Fact]
    public void RecordDeferral_ExplicitTimes_KeepsFirstInstantAcrossProgressAndBudgetReset()
    {
        var job = BulkEnrichmentJob.Create(1, "platform", CreatedAt);

        job.RecordDeferral("first", StartedAt);
        job.RecordDeferral("second", FinishedAt);
        job.DeferralCount.ShouldBe(2);
        job.RecordDeferral("progress", FinishedAt.AddMinutes(1), madeProgress: true);

        job.DeferralCount.ShouldBe(0);
        job.FirstDeferralAt.ShouldBe(StartedAt);
        job.LastDeferralAt.ShouldBe(FinishedAt.AddMinutes(1));
        job.LastDeferralError.ShouldBe("progress");
        job.RecordDeferral("next", FinishedAt.AddMinutes(2));
        job.ResetDeferralBudget();
        job.FirstDeferralAt.ShouldBe(StartedAt);
        job.LastDeferralAt.ShouldBe(FinishedAt.AddMinutes(2));
    }

    [Fact]
    public void RecordUploadResults_ExplicitTimes_PreservesOrderedFailureFacts()
    {
        var job = UploadJob.Create("archive", CreatedAt);
        job.Start("delivery", StartedAt);
        job.BeginClassification();
        job.BeginDatIngestion();
        job.SetCurrentItem("catalog.dat");
        job.RecordDatResult(false, StartedAt.AddSeconds(1), "DAT failure");
        job.BeginRomIngestion();
        job.SetCurrentItem("game.rom");
        job.RecordRomResult(RomIngestOutcome.Failed, StartedAt.AddSeconds(2), "ROM failure");

        job.Errors.Select(error => error.Item).ShouldBe(["catalog.dat", "game.rom"]);
        job.Errors.Select(error => error.OccurredAt).ShouldBe([StartedAt.AddSeconds(1), StartedAt.AddSeconds(2)]);
        var originalErrors = job.Errors.ToArray();

        job.RestoreIngestionProgress([
            JobItem.ForDat(job.Id, "catalog.dat", 128, false, false, null, null, null, "DAT failure", StartedAt.AddSeconds(1)),
            JobItem.ForRom(job.Id, "game.rom", 256, RomIngestOutcome.Failed, null, null, null, "ROM failure", StartedAt.AddSeconds(2))
        ]);

        job.Errors.ShouldBe(originalErrors);
    }

    [Fact]
    public void RecordProcessed_ExplicitTime_RecordsFallbackAndRejectsRepeatedCursorWithoutChangingIt()
    {
        var job = MetadataRematerializationJob.Create(1, "platform", null, CreatedAt);
        job.RecordProcessed(7, FinishedAt, "fallback");

        Should.Throw<InvalidOperationException>(() => job.RecordProcessed(7, FinishedAt.AddDays(1), "repeated"));

        job.CursorTitleId.ShouldBe(7);
        job.ProcessedCount.ShouldBe(1);
        job.DeferredCount.ShouldBe(1);
        job.Errors.ShouldHaveSingleItem().OccurredAt.ShouldBe(FinishedAt);
    }

    [Fact]
    public void JobItem_ExplicitTime_PreservesMicrosecondsThroughIdentityAndRehydration()
    {
        var item = JobItem.ForRom(Guid.NewGuid(), "game.rom", 128, RomIngestOutcome.Failed, null, [7], 1, "failure", FinishedAt);
        var dat = JobItem.ForDat(item.JobId, "catalog.dat", 256, false, false, null, null, null, "failure", FinishedAt);

        item.CreatedAt.ShouldBe(FinishedAt);
        dat.CreatedAt.ShouldBe(FinishedAt);
        item.WithUploadIdentity().CreatedAt.ShouldBe(FinishedAt);
        item.CreatedAt.Ticks.ShouldBe(FinishedAt.Ticks);
        item.CreatedAt.Offset.ShouldBe(FinishedAt.Offset);
    }
}
