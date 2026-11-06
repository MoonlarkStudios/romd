using System.Text.Json;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Jobs;

public sealed class JobStartTests
{
    private static readonly DateTimeOffset At = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234560);
    private static readonly string[] GuardedFamilies =
        ["upload", "replace-dat", "export", "materialization", "enrichment", "bulk-enrichment", "artwork-import", "media-subject", "media-selection"];

    public static IEnumerable<object[]> GuardedStates() =>
        from family in GuardedFamilies
        from state in new[] { "active", "completed", "failed-before-start", "cancelled-before-start" }
        select new object[] { family, state };

    public static IEnumerable<object[]> PendingFamilies() =>
        GuardedFamilies.Concat(["artwork-completion", "metadata-rematerialization"])
            .Select(family => new object[] { family });

    [Theory]
    [MemberData(nameof(GuardedStates))]
    public void Start_RejectedPhase_LeavesAllObservableStateUnchanged(string family, string state)
    {
        var job = Create(family);
        job.SetHangfireJobId("original-delivery");
        if (state is "active" or "completed")
            job.Start("original-delivery", startedAt: At);
        job.RecordError("retained-item", "retained-error", occurredAt: At);
        job.RecordDeferral("retained-deferral", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        if (state == "completed")
            Complete(job);
        else if (state == "failed-before-start")
            Terminate(job, MediaReconciliationOutcomeKind.Failed);
        else if (state == "cancelled-before-start")
            Terminate(job, MediaReconciliationOutcomeKind.Cancelled);
        if (job.IsTerminal)
            job.Archive(archivedAt: At);
        job.SetCurrentItem("retained-current-item");
        var before = Snapshot(job);

        Should.Throw<InvalidOperationException>(() => job.Start("rejected-delivery", startedAt: At.AddYears(1)));

        Snapshot(job).ShouldBe(before);
    }

    [Theory]
    [MemberData(nameof(PendingFamilies))]
    public void Start_PendingJob_RecordsDeliveryAndEntersItsFirstActivePhase(string family)
    {
        var job = Create(family);
        job.CreatedAt.ShouldBe(At);

        job.Start("accepted-delivery", startedAt: At.AddMinutes(1));

        job.HangfireJobId.ShouldBe("accepted-delivery");
        job.StartedAt.ShouldNotBeNull();
        job.StartedAt.Value.ShouldBe(At.AddMinutes(1));
        job.CompletedAt.ShouldBeNull();
        job.IsTerminal.ShouldBeFalse();
        job.Phase.ShouldBe(family switch
        {
            "upload" => "Extracting",
            "replace-dat" => "Ingesting",
            "export" => "Collecting",
            "materialization" => "Materializing",
            "artwork-import" => "Importing",
            "media-subject" => "Planning",
            "media-selection" => "Selecting",
            "metadata-rematerialization" => "Rematerializing",
            _ => "Enriching"
        });
    }

    [Theory]
    [InlineData("artwork-completion", false)]
    [InlineData("artwork-completion", true)]
    [InlineData("metadata-rematerialization", false)]
    [InlineData("metadata-rematerialization", true)]
    public void Start_ResumableFamily_AcceptsRedeliveryAndPreservesRecordedProgress(string family, bool completed)
    {
        var job = Create(family);
        job.Start("first-delivery", startedAt: At);
        if (job is ArtworkCompletionJob artwork)
            artwork.SetProgress(1);
        else if (job is MetadataRematerializationJob metadata)
            metadata.RecordProcessed(7, occurredAt: At);
        if (completed)
            ((DirectLifecycleJob)job).Complete(completedAt: At);
        job.SetCurrentItem("retained-current-item");
        var completedAt = job.CompletedAt;

        job.Start("redelivery", startedAt: At.AddMinutes(3));

        job.StartedAt.ShouldBe(At.AddMinutes(3));

        job.HangfireJobId.ShouldBe("redelivery");
        job.Phase.ShouldBe(family == "artwork-completion" ? "Enriching" : "Rematerializing");
        job.CompletedAt.ShouldBe(completedAt);
        job.CurrentItem.ShouldBe("retained-current-item");
        if (job is ArtworkCompletionJob resumedArtwork)
            resumedArtwork.Processed.ShouldBe(1);
        else if (job is MetadataRematerializationJob resumedMetadata)
        {
            resumedMetadata.ProcessedCount.ShouldBe(1);
            resumedMetadata.CursorTitleId.ShouldBe(7);
        }
    }

    private static Job Create(string family) => family switch
    {
        "upload" => UploadJob.Create("archive.zip", createdAt: At, 1, Guid.NewGuid()),
        "replace-dat" => ReplaceDatJob.Create(1, "catalog.dat", createdAt: At, 1, Guid.NewGuid()),
        "export" => ExportJob.CreateLibrary(1, 7, createdAt: At, Guid.NewGuid()),
        "materialization" => MaterializationJob.Create(1, "library", createdAt: At, Guid.NewGuid()),
        "enrichment" => EnrichmentJob.Create("title", 1, 1, At),
        "bulk-enrichment" => BulkEnrichmentJob.Create(1, "platform", createdAt: At),
        "artwork-import" => ArtworkImportJob.Create(Guid.NewGuid(), "title", 1, 1, ArtworkRole.Poster, 1,
            "igdb", "7", "cover", "https://example.test/cover", null, At),
        "artwork-completion" => ArtworkCompletionJob.Create(Guid.NewGuid(), 1, Guid.NewGuid(), createdAt: At),
        "metadata-rematerialization" => MetadataRematerializationJob.Create(1, "platform", Guid.NewGuid(), createdAt: At),
        "media-subject" or "media-selection" => MediaReconciliationJob.Create(Guid.NewGuid(), family == "media-subject"
            ? new MediaReconciliationRequest.SubjectWork(new MediaReconciliationSubject.Representation(7))
            : new MediaReconciliationRequest.SelectionWork(new MediaReconciliationFilter.AllSubjects(), new(1, 0, 0, 0), 1, false),
            "media", null, Guid.NewGuid(), At),
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };

    private static void Complete(Job job)
    {
        switch (job)
        {
            case UploadJob upload:
                upload.BeginClassification();
                upload.BeginDatIngestion();
                upload.BeginRomIngestion();
                break;
            case ReplaceDatJob replace:
                replace.SetNewDatId(2);
                replace.BeginReplacing();
                break;
            case ArtworkImportJob artwork:
                artwork.SetImportOutcome(7, false);
                break;
            case MediaReconciliationJob media:
                if (media.Request is MediaReconciliationRequest.SelectionWork)
                    media.ApplySelectionProgress(new(null, 0, 0, 0, 0, 0, 0, true));
                media.ApplyOutcome(MediaReconciliationOutcomeKind.Completed, "retained-outcome", At);
                return;
        }
        ((DirectLifecycleJob)job).Complete(completedAt: At);
    }

    private static void Terminate(Job job, MediaReconciliationOutcomeKind outcome)
    {
        if (job is MediaReconciliationJob media)
            media.ApplyOutcome(outcome, "retained-outcome", At);
        else if (outcome == MediaReconciliationOutcomeKind.Failed)
            ((DirectLifecycleJob)job).Fail("retained-failure", failedAt: At);
        else
            ((CancellableJob)job).Cancel(cancelledAt: At);
    }

    private static string Snapshot(Job job) => JsonSerializer.Serialize(job, job.GetType());
}
