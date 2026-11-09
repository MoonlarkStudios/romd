using System.Text.Json;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ListJobs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Systems;
using Romd.Contracts.Management.Models;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

using ContractJob = Romd.Contracts.Management.Models.Job;
using ContractMediaReconciliationJob = Romd.Contracts.Management.Models.MediaReconciliationJob;

using Job = Romd.Domain.Jobs.Job;
using MediaReconciliationJob = Romd.Domain.Jobs.MediaReconciliationJob;
using MediaReconciliationSubject = Romd.Domain.Source.Rom.MediaReconciliationSubject;

namespace Romd.Application.Tests.Jobs;

public sealed class MediaReconciliationJobMappingTests
{
    private static readonly SystemKeys Keys = new(new Dictionary<int, string> { [7] = "gc" });
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task List_AdminFamilyFilter_ReturnsTheTypedMediaResource()
    {
        var job = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SubjectWork(new MediaReconciliationSubject.LogicalRom(7)), "subject", null, null, DateTimeOffset.UtcNow);
        var repository = Substitute.For<IJobRepository>();
        repository.ListAsync(Arg.Any<JobListFilter>(), Arg.Any<CancellationToken>()).Returns(new Job[] { job });
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.NewGuid());
        user.HasRole(Romd.Domain.Identity.RomdRoleType.Admin).Returns(true);
        var reference = Substitute.For<IReferenceCatalogService>();
        reference.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(Keys);
        var result = await new ListJobsQueryHandler(reference, repository, user).HandleAsync(new(JobType: "media-reconciliation"));
        result.IsError.ShouldBeFalse();
        result.Value.Items.ShouldHaveSingleItem().ShouldBeOfType<ContractMediaReconciliationJob>().Id.ShouldBe(job.Id);
        await repository.Received(1).ListAsync(Arg.Is<JobListFilter>(filter => filter.JobType == "media-reconciliation" && filter.IncludeMediaConvergence), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ToContract_Subject_UsesClosedFamilyAndNamespaceWithPublicIdentityAndOriginalSelectionOwner()
    {
        Guid owner = Guid.NewGuid();
        var job = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SubjectWork(new MediaReconciliationSubject.OriginalLayout(37)), "disc.cue", owner, null, DateTimeOffset.UtcNow);
        job.Start("transport", startedAt: DateTimeOffset.UtcNow);
        job.SetPhase(MediaReconciliationPhase.Verifying);
        job.RecordDeferral("Source custody remains active.", at: DateTimeOffset.UtcNow);
        job.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(30);
        var dto = ((Job)job).ToContract(Keys).ShouldBeOfType<ContractMediaReconciliationJob>();
        dto.RequestMode.ShouldBe("Subject");
        dto.Subject.ShouldBe(new("OriginalLayout", IdCoder.Encode(37)));
        dto.Selection.ShouldBeNull();
        dto.OriginSelectionJobId.ShouldBe(owner);
        dto.CorrelationId.ShouldBe(owner);
        dto.Phase.ShouldBe("Verifying");
        dto.Errors.Single().Message.ShouldBe("Source custody remains active.");
        string serialized = JsonSerializer.Serialize<ContractJob>(dto, Json);
        using var document = JsonDocument.Parse(serialized);
        document.RootElement.GetProperty("jobType").GetString().ShouldBe("media-reconciliation");
        document.RootElement.GetProperty("subject").GetProperty("id").GetString().ShouldBe(IdCoder.Encode(37));
        var replay = JsonSerializer.Deserialize<ContractJob>(serialized, Json).ShouldBeOfType<ContractMediaReconciliationJob>();
        replay.Subject.ShouldBe(dto.Subject);
        replay.OriginSelectionJobId.ShouldBe(owner);
        replay.NextAttemptAt.ShouldBe(dto.NextAttemptAt);
    }

    [Fact]
    public void ToContract_Selection_ReportsScopeAndAllDurableCountsWithoutInternalHighWatermarks()
    {
        var job = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SelectionWork(new MediaReconciliationFilter.KindSubjects("gamecube-disc", 7), new(100, 200, 300, 0), 10, true), "selection", null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        job.ApplySelectionProgress(new(new MediaReconciliationSubject.LogicalRom(5), 4, 1, 2, 1, 2, 1, true));
        var dto = ((Job)job).ToContract(Keys).ShouldBeOfType<ContractMediaReconciliationJob>();
        dto.RequestMode.ShouldBe("Selection");
        dto.Subject.ShouldBeNull();
        dto.SystemKey.ShouldBe("gc");
        dto.Selection.ShouldBe(new MediaReconciliationSelection
        {
            Filter = "KindSubjects",
            KindId = "gamecube-disc",
            SystemKey = "gc",
            Limit = 10,
            ConversionRequested = true,
            Selected = 4,
            Created = 1,
            Adopted = 2,
            Skipped = 1,
            Completed = 2,
            Failed = 1,
            EnumerationComplete = true
        });
        dto.HasErrors.ShouldBeTrue();
        dto.IsTerminal.ShouldBeFalse();
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize<ContractJob>(dto, Json));
        var selection = serialized.RootElement.GetProperty("selection");
        selection.TryGetProperty("cursor", out _).ShouldBeFalse();
        selection.TryGetProperty("bounds", out _).ShouldBeFalse();
    }

    [Fact]
    public void ToContract_TerminalSubject_ReportsExactJournalOutcomeAndNoStaleDeferral()
    {
        var job = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SubjectWork(new MediaReconciliationSubject.Representation(7)), "artifact", null, null, DateTimeOffset.UtcNow);
        job.RecordDeferral("Earlier retry.", at: DateTimeOffset.UtcNow);
        job.ApplyOutcome(MediaReconciliationOutcomeKind.Failed, "measurement-mismatch", job.CreatedAt.AddSeconds(1));
        var dto = ((Job)job).ToContract(Keys).ShouldBeOfType<ContractMediaReconciliationJob>();
        dto.Outcome.ShouldBe("Failed");
        dto.OutcomeCode.ShouldBe("measurement-mismatch");
        dto.HasErrors.ShouldBeTrue();
        dto.IsTerminal.ShouldBeTrue();
        dto.CompletedAt.ShouldBe(job.CompletedAt);
        dto.Errors.ShouldBeEmpty();
    }
}
