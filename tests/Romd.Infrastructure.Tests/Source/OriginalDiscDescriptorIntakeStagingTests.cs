using System.Text;
using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Domain.Storage;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Source;

public sealed class OriginalDiscDescriptorIntakeStagingTests
{
    private static readonly byte[] Cue = Encoding.UTF8.GetBytes("FILE \"track.bin\" BINARY\n TRACK 01 MODE2/2352\n");
    private static readonly FileStoreResult Stored = new(FileEntity.Rehydrate(5,
        Sha256.FromSpan(System.Security.Cryptography.SHA256.HashData(Cue)), Cue.Length, Cue.Length, false, DateTimeOffset.UnixEpoch), true, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntakeAsync_FilePublication_FlushesOnlyPendingIdentityBeforeDescriptorAndOutcome(bool pending)
    {
        var fixture = new Fixture(pending);
        using var bytes = new MemoryStream(Cue);

        var result = await fixture.Intake.IntakeAsync(bytes, fixture.JobId, "disc.cue", default);

        result.Id.ShouldBe(JobItem.UploadIdentity(fixture.JobId, JobItemKind.Rom, "disc.cue"));
        fixture.Events.ShouldBe(pending
            ? ["begin", "find-outcome", "publish", "flush", "read-file", "record", "outcome", "commit"]
            : ["begin", "find-outcome", "publish", "read-file", "record", "outcome", "commit"]);
        await fixture.Descriptors.Received(1).RecordStagedAsync(fixture.JobId, "disc.cue", "disc.cue", 5,
            Arg.Is<OriginalCueDescriptor>(descriptor => descriptor.Dependencies.SequenceEqual(new[] { "track.bin" })), default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntakeAsync_IdentityFlushInterrupted_DoesNotReadIdentityOrStageDescriptorOrOutcome(bool cancel)
    {
        var fixture = new Fixture(pending: true);
        using var cancellation = new CancellationTokenSource();
        fixture.Work.FlushAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (cancel)
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            }
            return Task.FromException(new IOException("File identity flush failed."));
        });
        using var bytes = new MemoryStream(Cue);

        if (cancel)
            (await Should.ThrowAsync<OperationCanceledException>(() => fixture.Intake.IntakeAsync(bytes, fixture.JobId, "disc.cue", cancellation.Token)))
                .CancellationToken.ShouldBe(cancellation.Token);
        else
            await Should.ThrowAsync<IOException>(() => fixture.Intake.IntakeAsync(bytes, fixture.JobId, "disc.cue", cancellation.Token));

        fixture.Publication.DidNotReceive().ReadAfterFlush();
        await fixture.Descriptors.DidNotReceive().RecordStagedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OriginalCueDescriptor>(), Arg.Any<CancellationToken>());
        await fixture.Outcomes.DidNotReceive().AddStagedAsync(Arg.Any<JobItem>(), Arg.Any<CancellationToken>());
        await fixture.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await fixture.Transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task IntakeAsync_ExistingOutcome_ReturnsItBeforePublicationAndCommit()
    {
        var fixture = new Fixture(pending: true);
        var prior = JobItem.ForRom(fixture.JobId, "disc.cue", Cue.Length, RomIngestOutcome.Ingested, null, [], null, null,
            DateTimeOffset.UnixEpoch, true).WithUploadIdentity();
        fixture.Outcomes.FindOutcomeAsync(fixture.JobId, JobItemKind.Rom, "disc.cue", default).Returns(prior);
        using var bytes = new MemoryStream(Cue);

        (await fixture.Intake.IntakeAsync(bytes, fixture.JobId, "disc.cue", default)).ShouldBeSameAs(prior);

        await fixture.Files.DidNotReceive().PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>());
        await fixture.Work.DidNotReceive().FlushAsync(Arg.Any<CancellationToken>());
        await fixture.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_FlushesBindingsBeforeStagingSortedDemandsInTheSameTransaction()
    {
        var fixture = new Fixture();
        fixture.Descriptors.ReconcileStagedAsync(fixture.JobId, default).Returns(_ => { fixture.Events.Add("bindings"); return new[] { 7, 2 }; });
        fixture.Requests.StageSubjectAsync(Arg.Any<MediaReconciliationDemand>(), default).Returns(call =>
        {
            var demand = call.Arg<MediaReconciliationDemand>();
            fixture.Events.Add("demand-" + demand.Subject.Id);
            var job = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SubjectWork(demand.Subject), "layout", null, null, DateTimeOffset.UnixEpoch);
            return new MediaReconciliationAcceptance(job, true, false);
        });

        await fixture.Intake.ReconcileAsync(fixture.JobId, default);

        fixture.Events.ShouldBe(["begin", "bindings", "flush", "demand-2", "demand-7", "commit"]);
        await fixture.Work.Received(1).FlushAsync(default);
    }

    [Fact]
    public async Task ReconcileAsync_BindingFlushFails_StagesNoDemandAndDoesNotCommit()
    {
        var fixture = new Fixture();
        fixture.Descriptors.ReconcileStagedAsync(fixture.JobId, default).Returns([2]);
        fixture.Work.FlushAsync(default).Returns(Task.FromException(new IOException("Binding flush failed.")));

        await Should.ThrowAsync<IOException>(() => fixture.Intake.ReconcileAsync(fixture.JobId, default));

        await fixture.Requests.DidNotReceive().StageSubjectAsync(Arg.Any<MediaReconciliationDemand>(), Arg.Any<CancellationToken>());
        await fixture.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    private sealed class Fixture
    {
        internal Guid JobId { get; } = Guid.NewGuid();
        internal IOriginalDiscDescriptorRepository Descriptors { get; } = Substitute.For<IOriginalDiscDescriptorRepository>();
        internal IFileStorageService Files { get; } = Substitute.For<IFileStorageService>();
        internal IStagedFilePublication Publication { get; } = Substitute.For<IStagedFilePublication>();
        internal IJobItemRepository Outcomes { get; } = Substitute.For<IJobItemRepository>();
        internal IUnitOfWork Work { get; } = Substitute.For<IUnitOfWork>();
        internal ITransaction Transaction { get; } = Substitute.For<ITransaction>();
        internal IMediaReconciliationRequests Requests { get; } = Substitute.For<IMediaReconciliationRequests>();
        internal List<string> Events { get; } = [];
        internal OriginalDiscDescriptorIntake Intake { get; }
        internal Fixture(bool pending = false)
        {
            var staged = Substitute.For<IStagedFile>();
            Files.StageAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(staged);
            Files.PublishStagedAsync(staged, Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("publish"); return Publication; });
            Publication.RequiresFlush.Returns(pending);
            Publication.ReadAfterFlush().Returns(_ => { Events.Add("read-file"); return Stored; });
            Work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("begin"); return Transaction; });
            Work.When(work => work.FlushAsync(Arg.Any<CancellationToken>())).Do(_ => Events.Add("flush"));
            Transaction.When(transaction => transaction.CommitAsync(Arg.Any<CancellationToken>())).Do(_ => Events.Add("commit"));
            Outcomes.FindOutcomeAsync(JobId, JobItemKind.Rom, "disc.cue", Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("find-outcome"); return (JobItem?)null; });
            Descriptors.When(repository => repository.RecordStagedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OriginalCueDescriptor>(), Arg.Any<CancellationToken>())).Do(_ => Events.Add("record"));
            Outcomes.When(outcomes => outcomes.AddStagedAsync(Arg.Any<JobItem>(), Arg.Any<CancellationToken>())).Do(_ => Events.Add("outcome"));
            Descriptors.ReadUnattachedMatchedCuesAsync(JobId, Arg.Any<CancellationToken>()).Returns(Array.Empty<OriginalDiscDescriptorSource>());
            Intake = new(Descriptors, Files, Outcomes, Work, Requests, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        }
    }
}
