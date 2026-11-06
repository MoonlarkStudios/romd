using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Jobs;

public sealed class UploadJobTests
{
    [Theory]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void UploadOutcomeIdentity_EnforcesJournalPathLimit(int length, bool accepted)
    {
        Action identify = () => JobItem.UploadIdentity(Guid.NewGuid(), JobItemKind.Rom, new string('a', length));
        if (accepted)
            Should.NotThrow(identify);
        else
            Should.Throw<ArgumentException>(identify);
    }

    [Fact]
    public void RestoreIngestionProgress_PreservesDurableFailuresAndRejectsForeignOrDuplicateOutcomes()
    {
        var job = UploadJob.Create("files", createdAt: DateTimeOffset.UtcNow);
        job.Start("one", startedAt: DateTimeOffset.UtcNow);
        job.BeginClassification();
        job.SetDiscoveredFiles(1, 2);
        job.BeginDatIngestion();
        job.BeginRomIngestion();
        var dat = JobItem.ForDat(job.Id, "catalog.dat", 1, true, false, null, null, 1, null, createdAt: DateTimeOffset.UtcNow);
        var failed = JobItem.ForRom(job.Id, "bad.rom", 1, RomIngestOutcome.Failed, null, null, null, "corrupt", createdAt: DateTimeOffset.UtcNow);
        var rejected = JobItem.ForRom(job.Id, "unmatched.rom", 1, RomIngestOutcome.Rejected, null, null, null, null, createdAt: DateTimeOffset.UtcNow);
        job.RestoreIngestionProgress([dat, failed, rejected]);
        job.DatsSucceeded.ShouldBe(1);
        job.RomsProcessed.ShouldBe(2);
        job.RomsRejected.ShouldBe(1);
        job.Errors.ShouldHaveSingleItem().Message.ShouldBe("corrupt");
        Should.Throw<InvalidOperationException>(() => job.RestoreIngestionProgress([failed, failed]));
        Should.Throw<InvalidOperationException>(() => job.RestoreIngestionProgress([
            JobItem.ForRom(Guid.NewGuid(), "foreign", 1, RomIngestOutcome.Ingested, null, null, null, null, createdAt: DateTimeOffset.UtcNow)]));
        job.RomsProcessed.ShouldBe(2);
        job.Complete(completedAt: DateTimeOffset.UtcNow);
        job.PhaseEnum.ShouldBe(UploadPhase.CompletedWithErrors);
        Should.Throw<InvalidOperationException>(() => job.RestoreIngestionProgress([]));
    }

    [Fact]
    public void Create_DefaultsToCollectionImport()
    {
        var job = UploadJob.Create("library.zip", createdAt: DateTimeOffset.UtcNow);

        job.ArchiveOnly.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetArchiveOnly_StoresChoice(bool archiveOnly)
    {
        var job = UploadJob.Create("library.zip", createdAt: DateTimeOffset.UtcNow);

        job.SetArchiveOnly(archiveOnly);

        job.ArchiveOnly.ShouldBe(archiveOnly);
    }
}
