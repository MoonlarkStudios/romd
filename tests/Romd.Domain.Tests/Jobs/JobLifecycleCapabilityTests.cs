using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Jobs;

public sealed class JobLifecycleCapabilityTests
{
    public static TheoryData<Type, bool, bool> Capabilities => new()
    {
        { typeof(Job), false, false },
        { typeof(DirectLifecycleJob), true, false },
        { typeof(CancellableJob), true, true },
        { typeof(MediaReconciliationJob), false, false },
        { typeof(MetadataRematerializationJob), true, false },
        { typeof(UploadJob), true, true },
        { typeof(ReplaceDatJob), true, true },
        { typeof(ExportJob), true, true },
        { typeof(MaterializationJob), true, true },
        { typeof(EnrichmentJob), true, true },
        { typeof(BulkEnrichmentJob), true, true },
        { typeof(ArtworkImportJob), true, true },
        { typeof(ArtworkCompletionJob), true, true }
    };

    [Theory]
    [MemberData(nameof(Capabilities))]
    public void PublicTerminalCapabilities_MatchTheFamilyOwner(Type type, bool direct, bool cancellable)
    {
        (type.GetMethod(nameof(DirectLifecycleJob.Complete)) is not null).ShouldBe(direct);
        (type.GetMethod(nameof(DirectLifecycleJob.Fail)) is not null).ShouldBe(direct);
        (type.GetMethod(nameof(CancellableJob.Cancel)) is not null).ShouldBe(cancellable);
        typeof(Job).IsAssignableFrom(type).ShouldBeTrue();
        type.GetMethod(nameof(Job.Start)).ShouldNotBeNull();
    }

    [Fact]
    public void RequiredMetadataWork_StillCompletesOrFailsButCannotAcquireCancellation()
    {
        var at = DateTimeOffset.Parse("2026-11-05T12:00:00Z").AddTicks(1234);
        DirectLifecycleJob job = MetadataRematerializationJob.Create(1, "system", null, at);
        job.Start("delivery", at.AddTicks(1));
        job.SetCurrentItem("title");

        job.Fail("persistent failure", at.AddTicks(2));
        job.Fail("secondary failure", at.AddTicks(3));

        job.Phase.ShouldBe("Failed");
        job.CompletedAt.ShouldBe(at.AddTicks(2));
        job.Errors.ShouldHaveSingleItem().Message.ShouldBe("persistent failure");
        (job is CancellableJob).ShouldBeFalse();
    }
}
