using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Jobs;

public sealed class MetadataRematerializationJobTests
{
    [Fact]
    public void Lifecycle_RecordsProgressAndDeferredErrors_AndCompletesWithErrors()
    {
        var actor = Guid.NewGuid();
        var job = MetadataRematerializationJob.Create(17, "snes", actor, createdAt: DateTimeOffset.UtcNow);
        job.Phase.ShouldBe("Pending");
        job.CreatedByUserId.ShouldBe(actor);
        job.PlatformId.ShouldBe(17);
        job.SourceFilename.ShouldBe("snes");
        job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
        job.Phase.ShouldBe("Rematerializing");
        job.SetTotalTitles(2);
        job.RecordProcessed(100, occurredAt: DateTimeOffset.UtcNow);
        job.ProgressPercent.ShouldBe(50);
        job.RecordProcessed(101, occurredAt: DateTimeOffset.UtcNow, "retained for retry");
        job.DeferredCount.ShouldBe(1);
        job.Errors.Single().Item.ShouldBe("101");
        job.Complete(completedAt: DateTimeOffset.UtcNow);
        job.Phase.ShouldBe("CompletedWithErrors");
        job.IsTerminal.ShouldBeTrue();
        job.ProgressPercent.ShouldBe(100);
        job.Archive(archivedAt: DateTimeOffset.UtcNow);
        job.IsArchived.ShouldBeTrue();
        job.Unarchive();
        job.IsArchived.ShouldBeFalse();
    }
    [Fact]
    public void Lifecycle_EmptyPassCompletes_AndCursorCannotRepeatOrMoveBackwards()
    {
        var job = MetadataRematerializationJob.Create(1, "empty", null, createdAt: DateTimeOffset.UtcNow);
        job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
        job.Complete(completedAt: DateTimeOffset.UtcNow);
        job.Phase.ShouldBe("Completed");
        job.ProgressPercent.ShouldBe(100);
        var active = MetadataRematerializationJob.Create(1, "system", null, createdAt: DateTimeOffset.UtcNow);
        active.Start("delivery", startedAt: DateTimeOffset.UtcNow);
        active.RecordProcessed(5, occurredAt: DateTimeOffset.UtcNow);
        Should.Throw<InvalidOperationException>(() => active.RecordProcessed(5, occurredAt: DateTimeOffset.UtcNow));
        Should.Throw<InvalidOperationException>(() => active.RecordProcessed(4, occurredAt: DateTimeOffset.UtcNow));
        typeof(MetadataRematerializationJob).GetMethod(nameof(CancellableJob.Cancel)).ShouldBeNull();
        active.Fail("represented historical failure", failedAt: DateTimeOffset.UtcNow);
        active.Phase.ShouldBe("Failed");
        active.IsTerminal.ShouldBeTrue();
    }
}
