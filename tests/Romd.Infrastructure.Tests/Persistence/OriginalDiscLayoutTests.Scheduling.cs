using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Catalog.Commands.DecideOriginalDiscLayout;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Ids;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Source;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class OriginalDiscLayoutTests
{
    [Fact]
    public async Task Requests_ConcurrentLayoutFactsAndImport_AdoptOneJobAndRetainBothRequests()
    {
        int layoutId = Decode((await Intake()).Id);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        async Task<MediaReconciliationAcceptance> AcceptAsync(MediaReconciliationCause cause)
        {
            await using var db = database.CreateContext();
            await using var transaction = await db.Database.BeginTransactionAsync();
            if (Interlocked.Increment(ref entered) == 2)
                ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var accepted = await MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(
                new(Guid.NewGuid(), new MediaReconciliationSubject.OriginalLayout(layoutId), false, cause, null, null));
            accepted.IsError.ShouldBeFalse();
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return accepted.Value;
        }

        var accepted = await Task.WhenAll(AcceptAsync(MediaReconciliationCause.Import), AcceptAsync(MediaReconciliationCause.Facts));

        accepted.Count(value => value.Created).ShouldBe(1);
        Guid jobId = accepted.Select(value => value.Job.Id).Distinct().ShouldHaveSingleItem();
        await using var observer = database.CreateContext();
        var subject = await observer.Set<MediaReconciliationSubjectEntity>().SingleAsync();
        subject.SubjectKind.ShouldBe("OriginalLayout");
        subject.SubjectId.ShouldBe(layoutId);
        subject.RequestedVersion.ShouldBe(2);
        subject.ConversionRequested.ShouldBeFalse();
        var audits = await observer.Set<MediaReconciliationRequestEntity>().OrderBy(row => row.Cause).ToArrayAsync();
        audits.Select(row => row.Cause).ShouldBe(["Facts", "Import"]);
        audits.ShouldAllBe(row => !row.ConversionRequested && row.ActorId == null && row.OriginSelectionJobId == null);
        (await observer.Set<MediaReconciliationJobEntity>().Select(row => row.Id).ToArrayAsync()).ShouldBe([jobId]);
        (await observer.JobDispatches.Select(row => row.JobId).ToArrayAsync()).ShouldBe([jobId]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Reconcile_NewBindingsStageSortedLayoutDemand_AndReplayKeepsOneRequestPerChange(int layoutCount)
    {
        var layouts = new List<int>();
        for (int i = 0; i < layoutCount; i++)
            layouts.Add(Decode((await Intake($"disc-{i}.cue")).Id));
        await using var db = database.CreateContext();
        var upload = await SeedSchedulingUploadAsync(db, layouts);
        var requests = MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System);
        var intake = new OriginalDiscDescriptorIntake(new OriginalDiscLayoutService(db, new FileMutationLock(db)),
            Substitute.For<IFileStorageService>(), Substitute.For<IJobItemRepository>(), new EfUnitOfWork(db), requests, clock: TimeProvider.System);

        await intake.ReconcileAsync(upload.Id, default);
        await intake.ReconcileAsync(upload.Id, default);

        await using var observer = database.CreateContext();
        var subjects = await observer.Set<MediaReconciliationSubjectEntity>().OrderBy(row => row.SubjectId).ToArrayAsync();
        subjects.Select(row => row.SubjectKind).ShouldBe(Enumerable.Repeat("OriginalLayout", layoutCount));
        subjects.Select(row => row.SubjectId).ShouldBe(layouts.Order());
        subjects.ShouldAllBe(row => row.RequestedVersion == 1 && !row.ConversionRequested);
        (await observer.Set<MediaReconciliationRequestEntity>().Select(row => row.Cause).ToArrayAsync())
            .ShouldBe(Enumerable.Repeat("Import", layoutCount));
        (await observer.Set<MediaReconciliationJobEntity>().CountAsync()).ShouldBe(layoutCount);
        var requestedJobs = await observer.Set<MediaReconciliationJobEntity>().Select(row => row.Id).OrderBy(id => id).ToArrayAsync();
        (await observer.JobDispatches.Where(row => row.JobId != upload.Id).Select(row => row.JobId).OrderBy(id => id).ToArrayAsync())
            .ShouldBe(requestedJobs);
        (await observer.JobDispatches.CountAsync(row => row.JobId == upload.Id)).ShouldBe(1);
        var bindings = await observer.OriginalDiscLayoutFiles.OrderBy(row => row.OriginalDiscLayoutId).ToArrayAsync();
        bindings.Select(row => row.OriginalDiscLayoutId).ShouldBe(layouts.Order());
        bindings.ShouldAllBe(row => row.RomId == 1 && row.FileId == 1 && row.BindingRevision == Guid.Empty);
    }

    [Fact]
    public async Task Reconcile_RequestFailureAfterBindingFlush_RollsBackBindingsAndDurableDemandTogether()
    {
        int layoutId = Decode((await Intake()).Id);
        await using var db = database.CreateContext();
        var upload = await SeedSchedulingUploadAsync(db, [layoutId]);
        var real = MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System);
        var requests = Substitute.For<IMediaReconciliationRequests>();
        requests.StageSubjectAsync(Arg.Any<MediaReconciliationDemand>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var staged = await real.StageSubjectAsync(call.Arg<MediaReconciliationDemand>(), call.Arg<CancellationToken>());
                staged.IsError.ShouldBeFalse();
                await db.SaveChangesAsync();
                await using var observer = database.CreateContext();
                (await observer.OriginalDiscLayoutFiles.CountAsync()).ShouldBe(0);
                (await observer.Set<MediaReconciliationJobEntity>().CountAsync()).ShouldBe(0);
                return (ErrorOr<MediaReconciliationAcceptance>)Error.Unexpected("Injected.Demand", "Reject staged demand after its durable rows are flushed.");
            });
        var intake = new OriginalDiscDescriptorIntake(new OriginalDiscLayoutService(db, new FileMutationLock(db)),
            Substitute.For<IFileStorageService>(), Substitute.For<IJobItemRepository>(), new EfUnitOfWork(db), requests, clock: TimeProvider.System);

        await Should.ThrowAsync<InvalidOperationException>(() => intake.ReconcileAsync(upload.Id, default));

        await using var after = database.CreateContext();
        (await after.OriginalDiscLayoutFiles.CountAsync()).ShouldBe(0);
        (await after.Set<MediaReconciliationSubjectEntity>().CountAsync()).ShouldBe(0);
        (await after.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(0);
        (await after.Set<MediaReconciliationJobEntity>().CountAsync()).ShouldBe(0);
        (await after.JobDispatches.Select(row => row.JobId).ToArrayAsync()).ShouldBe([upload.Id]);
        (await after.OriginalDiscLayouts.SingleAsync()).DescriptorFileId.ShouldBe(2);
        (await after.RomFiles.CountAsync()).ShouldBe(1);
        (await after.JobItems.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Decision_RequestFailureAfterSourceFlush_RollsBackRevisionAuditAndDemandTogether()
    {
        var original = await Intake();
        await using var db = database.CreateContext();
        var real = MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System);
        var requests = Substitute.For<IMediaReconciliationRequests>();
        requests.StageSubjectAsync(Arg.Any<MediaReconciliationDemand>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var demand = call.Arg<MediaReconciliationDemand>();
                demand.Subject.ShouldBeOfType<MediaReconciliationSubject.OriginalLayout>().Id.ShouldBe(Decode(original.Id));
                demand.ConversionRequested.ShouldBeFalse();
                demand.Cause.ShouldBe(MediaReconciliationCause.Facts);
                (await real.StageSubjectAsync(demand, call.Arg<CancellationToken>())).IsError.ShouldBeFalse();
                await db.SaveChangesAsync();
                await using var observer = database.CreateContext();
                (await observer.OriginalDiscLayouts.SingleAsync()).State.ShouldBe("pending");
                (await observer.Set<MediaReconciliationJobEntity>().CountAsync()).ShouldBe(0);
                return (ErrorOr<MediaReconciliationAcceptance>)Error.Unexpected("Injected.Demand", "Reject demand after the curation and job rows are flushed.");
            });
        var handler = new DecideOriginalDiscLayoutCommandHandler(new OriginalDiscLayoutService(db, new FileMutationLock(db)),
            new EfUnitOfWork(db), requests, Microsoft.Extensions.Logging.Abstractions.NullLogger<DecideOriginalDiscLayoutCommandHandler>.Instance);

        var result = await handler.HandleAsync(new(Decode(original.Id), new("confirm", IdCoder.Encode(1),
            [new("track.bin", IdCoder.Encode(1))], "Reviewed"), RevisionPrecondition.For(original.Revision.ToString()), actor));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Injected.Demand");
        await using var after = database.CreateContext();
        var retained = await after.OriginalDiscLayouts.SingleAsync();
        retained.State.ShouldBe("pending");
        retained.Revision.ShouldBe(original.Revision);
        (await after.OriginalDiscLayoutFiles.CountAsync()).ShouldBe(0);
        (await after.Set<OriginalDiscLayoutAuditEntity>().CountAsync()).ShouldBe(1, "only the prior intake audit remains");
        (await after.Set<MediaReconciliationSubjectEntity>().CountAsync()).ShouldBe(0);
        (await after.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(0);
        (await after.Set<MediaReconciliationJobEntity>().CountAsync()).ShouldBe(0);
        (await after.JobDispatches.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Decision_ReplacementStagesBothChangedLayouts_WithoutForcingOnRequestConversion()
    {
        var original = await Confirm(await Intake());
        var replacement = await Confirm(await Intake("replacement.cue", 3), original);

        await using var observer = database.CreateContext();
        var subjects = await observer.Set<MediaReconciliationSubjectEntity>().OrderBy(row => row.SubjectId).ToArrayAsync();
        subjects.Select(row => row.SubjectId).ShouldBe([Decode(original.Id), Decode(replacement.Id)]);
        subjects.Select(row => row.RequestedVersion).ShouldBe([2L, 1L]);
        subjects.ShouldAllBe(row => row.SubjectKind == "OriginalLayout" && !row.ConversionRequested);
        (await observer.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(3);
        (await observer.Set<MediaReconciliationJobEntity>().CountAsync()).ShouldBe(2);
        (await observer.JobDispatches.CountAsync()).ShouldBe(2);
        (await observer.OriginalDiscLayouts.SingleAsync(row => row.Id == Decode(original.Id))).State.ShouldBe("detached");
        (await observer.OriginalDiscLayouts.SingleAsync(row => row.Id == Decode(replacement.Id))).State.ShouldBe("accepted");
        (await new FileRepository(observer).IsReferencedAsync(1, default)).ShouldBeTrue();
    }

    [Fact]
    public async Task Decision_ReplacementFailureOnSecondDemand_RollsBackBothRevisionsAndRequestVersions()
    {
        var original = await Confirm(await Intake());
        var replacement = await Intake("replacement.cue", 3);
        await using var db = database.CreateContext();
        var real = MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System);
        var requests = Substitute.For<IMediaReconciliationRequests>();
        var order = new List<int>();
        requests.StageSubjectAsync(Arg.Any<MediaReconciliationDemand>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var demand = call.Arg<MediaReconciliationDemand>();
                order.Add(demand.Subject.Id);
                var staged = await real.StageSubjectAsync(demand, call.Arg<CancellationToken>());
                staged.IsError.ShouldBeFalse();
                await db.SaveChangesAsync();
                return order.Count == 1 ? staged : (ErrorOr<MediaReconciliationAcceptance>)Error.Unexpected("Injected.SecondDemand", "Fail the replacement's second demand.");
            });
        var handler = new DecideOriginalDiscLayoutCommandHandler(new OriginalDiscLayoutService(db, new FileMutationLock(db)),
            new EfUnitOfWork(db), requests, Microsoft.Extensions.Logging.Abstractions.NullLogger<DecideOriginalDiscLayoutCommandHandler>.Instance);

        var result = await handler.HandleAsync(new(Decode(replacement.Id), new("confirm", IdCoder.Encode(1),
            [new("track.bin", IdCoder.Encode(1))], "Replace", original.Id, original.Revision),
            RevisionPrecondition.For(replacement.Revision.ToString()), actor));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Injected.SecondDemand");
        order.ShouldBe([Decode(original.Id), Decode(replacement.Id)]);
        await using var after = database.CreateContext();
        var prior = await after.OriginalDiscLayouts.SingleAsync(row => row.Id == Decode(original.Id));
        prior.State.ShouldBe("accepted");
        prior.Revision.ShouldBe(original.Revision);
        var pending = await after.OriginalDiscLayouts.SingleAsync(row => row.Id == Decode(replacement.Id));
        pending.State.ShouldBe("pending");
        pending.Revision.ShouldBe(replacement.Revision);
        var subject = await after.Set<MediaReconciliationSubjectEntity>().SingleAsync();
        subject.SubjectId.ShouldBe(Decode(original.Id));
        subject.RequestedVersion.ShouldBe(1);
        (await after.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(1);
        (await after.Set<MediaReconciliationJobEntity>().CountAsync()).ShouldBe(1);
        (await after.JobDispatches.CountAsync()).ShouldBe(1);
    }

    private async Task<UploadJob> SeedSchedulingUploadAsync(RomdDbContext db, IReadOnlyList<int> layouts)
    {
        var upload = UploadJob.Create("cd-layout-scheduling", createdAt: DateTimeOffset.UtcNow);
        db.Set<UploadJobEntity>().Add(UploadJobEntity.FromDomain(upload));
        await db.OriginalDiscLayouts.Where(row => layouts.Contains(row.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.SourceJobId, upload.Id));
        db.JobItems.Add(JobItemEntity.FromDomain(JobItem.ForRom(upload.Id, "track.bin", 4,
            RomIngestOutcome.Ingested, 1, [], 1, null, createdAt: DateTimeOffset.UtcNow).WithUploadIdentity()));
        await db.SaveChangesAsync();
        return upload;
    }
}
