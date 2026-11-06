using ErrorOr;
using NSubstitute;
using NSubstitute.Extensions;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class ArtworkImportJobExecutorTests
{
    private readonly IArtworkAssetSource _source = Substitute.For<IArtworkAssetSource>();
    private readonly IArtworkImageProcessor _processor = Substitute.For<IArtworkImageProcessor>();
    private readonly IFileStorageService _files = Substitute.For<IFileStorageService>();
    private readonly IArtworkCurationRepository _curation = Substitute.For<IArtworkCurationRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IJobRepository<ArtworkImportJob> _jobs = Substitute.For<IJobRepository<ArtworkImportJob>>();

    [Fact]
    public async Task ExecuteAsync_SupersededBeforeDownload_DoesNotFetchOrRetain()
    {
        var job = Job();
        await Executor().ExecuteAsync(job, Context());
        job.WasSuperseded.ShouldBeTrue();
        _source.ReceivedCalls().ShouldBeEmpty();
        _files.ShouldNotHavePublished();
        await _curation.Received(1).StageOutcomeAsync(job, null, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SupersededDuringDownload_DoesNotRetainDecodedBytes()
    {
        var job = Job();
        Ready(job);
        _curation.LockPendingAsync(job, Arg.Any<CancellationToken>()).Returns(true, false);
        await Executor().ExecuteAsync(job, Context());
        job.WasSuperseded.ShouldBeTrue();
        _files.ShouldNotHavePublished();
    }

    [Fact]
    public async Task ExecuteAsync_InvalidImage_DoesNotPublishOrRecordSuccess()
    {
        var job = Job();
        Ready(job);
        _processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), job.Role, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From<ProcessedArtworkImage>(ArtworkImageErrors.Invalid()));
        await Should.ThrowAsync<JobPermanentFailureException>(() => Executor().ExecuteAsync(job, Context()));
        job.RetainedAssetId.ShouldBeNull();
        job.WasSuperseded.ShouldBeFalse();
        _files.ShouldNotHavePublished();
        await _curation.DidNotReceive().StagePublicationAsync(job, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ProviderAssetGoneOrProviderNotConfigured_FailsPermanentlyWithoutRetaining(bool notConfigured)
    {
        var job = Job();
        Ready(job);
        _source.DownloadAsync(job.ProviderId, job.ProviderGameId, job.ProviderAssetId, job.Role,
                job.TrustedAssetUrl, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From<DownloadedArtworkAsset>(notConfigured
                ? ArtworkProviderErrors.NotConfigured()
                : ArtworkProviderErrors.NotFound()));

        await Should.ThrowAsync<JobPermanentFailureException>(() => Executor().ExecuteAsync(job, Context()));

        _processor.ReceivedCalls().ShouldBeEmpty();
        _files.ShouldNotHavePublished();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteAsync_TransientFailure_StopsOnThirdPersistedAttempt(int priorFailures)
    {
        var job = Job();
        for (var i = 0; i < priorFailures; i++)
            job.RecordError("attempt", "Earlier failed delivery", occurredAt: DateTimeOffset.UtcNow);
        job = ArtworkImportJobEntity.FromDomain(job).ToDomain();
        Ready(job);
        _source.DownloadAsync(job.ProviderId, job.ProviderGameId, job.ProviderAssetId, job.Role,
                job.TrustedAssetUrl, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From<DownloadedArtworkAsset>(ArtworkProviderErrors.Unavailable()));

        if (priorFailures == 2)
            await Should.ThrowAsync<JobPermanentFailureException>(() => Executor().ExecuteAsync(job, Context()));
        else
            await Should.ThrowAsync<InvalidOperationException>(() => Executor().ExecuteAsync(job, Context()));

        job.Errors.Count.ShouldBe(priorFailures); // Only the runner writes durable attempt errors.
        _files.ShouldNotHavePublished();
    }

    [Fact]
    public async Task ExecuteAsync_PreviouslyExhaustedDelivery_DoesNotDownloadAgain()
    {
        var job = Job();
        for (var i = 0; i < 3; i++)
            job.RecordError("attempt", "Earlier failed delivery", occurredAt: DateTimeOffset.UtcNow);
        job = ArtworkImportJobEntity.FromDomain(job).ToDomain();
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        await Should.ThrowAsync<JobPermanentFailureException>(() => Executor().ExecuteAsync(job, Context()));

        _source.ReceivedCalls().ShouldBeEmpty();
        _files.ShouldNotHavePublished();
    }

    [Fact]
    public async Task ExecuteAsync_ThirdAttemptInterruptedByShutdown_DoesNotBecomePermanentFailure()
    {
        var job = Job();
        Ready(job);
        job.RecordError("attempt", "First", occurredAt: DateTimeOffset.UtcNow);
        job.RecordError("attempt", "Second", occurredAt: DateTimeOffset.UtcNow);
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => Executor().ExecuteAsync(job,
            new JobContext(null, _ => Task.CompletedTask, shutdown.Token)));

        job.Errors.Count.ShouldBe(2);
        _source.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ThirdAttemptLosesOwnership_DoesNotBecomePermanentFailure()
    {
        var job = Job();
        Ready(job);
        job.RecordError("attempt", "First", occurredAt: DateTimeOffset.UtcNow);
        job.RecordError("attempt", "Second", occurredAt: DateTimeOffset.UtcNow);
        var context = new JobContext(null, _ => Task.CompletedTask, default,
            ownedMutationDelegate: (_, _) => throw new JobExecutionOwnershipLostException());

        await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => Executor().ExecuteAsync(job, context));

        job.Errors.Count.ShouldBe(2);
        _source.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_CommitFails_DoesNotLeakSuccessIntoFailureCheckpoint()
    {
        var job = Job();
        Ready(job);
        var mutations = 0;
        var context = new JobContext(null, _ => Task.CompletedTask, default,
            ownedMutationDelegate: async (mutation, ct) =>
            {
                await mutation(ct);
                if (++mutations == 2)
                    throw new IOException("Simulated transaction commit failure");
            });
        await Should.ThrowAsync<IOException>(() => Executor().ExecuteAsync(job, context));
        job.RetainedAssetId.ShouldBeNull();
        job.WasSuperseded.ShouldBeFalse();
        await _curation.Received(1).StageOutcomeAsync(job, 42, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ThirdAttemptCommitAcknowledgementFails_RecoversDurablePublishedOutcome()
    {
        var job = Job();
        Ready(job);
        job.RecordError("attempt", "First", occurredAt: DateTimeOffset.UtcNow);
        job.RecordError("attempt", "Second", occurredAt: DateTimeOffset.UtcNow);
        var persisted = ArtworkImportJobEntity.FromDomain(job).ToDomain();
        persisted.SetImportOutcome(42, false);
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(persisted);

        await Executor().ExecuteAsync(job, CommitAcknowledgementFailure());

        job.RetainedAssetId.ShouldBe(42);
        job.WasSuperseded.ShouldBeFalse();
        job.Complete(completedAt: DateTimeOffset.UtcNow);
        job.PhaseEnum.ShouldBe(ArtworkImportPhase.Completed);
        await _jobs.Received(1).GetByIdAsync(job.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ThirdAttemptCommitFailsAndNoDurableOutcome_FailsPermanently()
    {
        var job = Job();
        Ready(job);
        job.RecordError("attempt", "First", occurredAt: DateTimeOffset.UtcNow);
        job.RecordError("attempt", "Second", occurredAt: DateTimeOffset.UtcNow);
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ArtworkImportJobEntity.FromDomain(job).ToDomain());

        await Should.ThrowAsync<JobPermanentFailureException>(() => Executor().ExecuteAsync(job, CommitAcknowledgementFailure()));

        job.RetainedAssetId.ShouldBeNull();
        await _jobs.Received(1).GetByIdAsync(job.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ThirdAttemptOutcomeReadFails_PropagatesForRetryWithoutAssumingRollback()
    {
        var job = Job();
        Ready(job);
        job.RecordError("attempt", "First", occurredAt: DateTimeOffset.UtcNow);
        job.RecordError("attempt", "Second", occurredAt: DateTimeOffset.UtcNow);
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ArtworkImportJob?>(new IOException("Outcome read unavailable")));

        var error = await Should.ThrowAsync<IOException>(() => Executor().ExecuteAsync(job, CommitAcknowledgementFailure()));

        error.Message.ShouldBe("Outcome read unavailable");
        job.IsTerminal.ShouldBeFalse();
        job.RetainedAssetId.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyExhaustedButDurablyPublished_RecoversWithoutDownload()
    {
        var job = Job();
        for (var index = 0; index < 3; index++)
            job.RecordError("attempt", "Previous attempt", occurredAt: DateTimeOffset.UtcNow);
        var persisted = ArtworkImportJobEntity.FromDomain(job).ToDomain();
        persisted.SetImportOutcome(42, false);
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(persisted);

        await Executor().ExecuteAsync(job, Context());

        job.RetainedAssetId.ShouldBe(42);
        _source.ReceivedCalls().ShouldBeEmpty();
        _files.ShouldNotHavePublished();
    }

    private static JobContext CommitAcknowledgementFailure()
    {
        var mutations = 0;
        return new JobContext(null, _ => Task.CompletedTask, default,
            ownedMutationDelegate: async (mutation, ct) =>
            {
                await mutation(ct);
                if (++mutations == 2)
                    throw new IOException("Commit acknowledgement unavailable");
            });
    }

    [Fact]
    public async Task ExecuteAsync_Success_CommitsOnlySelectedOriginalAndBoundedVariants()
    {
        var job = Job();
        Ready(job);
        await Executor().ExecuteAsync(job, Context());
        job.RetainedAssetId.ShouldBe(42);
        job.WasSuperseded.ShouldBeFalse();
        await _files.Received(2).StageAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _files.Received(1).PublishStagedAsync(Arg.Is<IReadOnlyList<IStagedFile>>(files => files.Count == 2),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).FlushAsync(Arg.Any<CancellationToken>());
        await _curation.Received(1).StageAssetAsync(job,
            Arg.Is<RetainedArtworkContent>(content => content.Variants.Count == 1 && content.Attribution == "Artist"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_PublishedBeforeCrash_ReplayDoesNotDownloadOrRepin()
    {
        var job = Job();
        job.SetImportOutcome(42, false);
        await Executor().ExecuteAsync(job, Context());
        _source.ReceivedCalls().ShouldBeEmpty();
        _curation.ReceivedCalls().ShouldBeEmpty();
        job.Complete(completedAt: DateTimeOffset.UtcNow);
        job.PhaseEnum.ShouldBe(ArtworkImportPhase.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_Publication_PublishesEveryFileInOneBatchAfterLockingTheSelection()
    {
        var job = Job();
        Ready(job);
        var order = new List<string>();
        _curation.When(curation => curation.LockPendingAsync(job, Arg.Any<CancellationToken>())).Do(_ => order.Add("lock"));
        _files.When(files => files.PublishStagedAsync(Arg.Any<IReadOnlyList<IStagedFile>>(), Arg.Any<CancellationToken>()))
            .Do(call => order.Add($"publish {call.Arg<IReadOnlyList<IStagedFile>>().Count}"));
        _curation.When(curation => curation.StageAssetAsync(job, Arg.Any<RetainedArtworkContent>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("stage"));

        await Executor().ExecuteAsync(job, Context());

        // One batch lets the ledger lock every hash in its global order, after the selection lock.
        order.ShouldBe(["lock", "lock", "publish 2", "stage"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_Publication_ReadsIdentitiesAfterRequiredFlushAndFlushesTheAssetSeparately(bool requiresFlush)
    {
        var job = Job();
        Ready(job);
        var order = new List<string>();
        var publication = Substitute.For<IStagedFilePublicationBatch>();
        publication.RequiresFlush.Returns(requiresFlush);
        publication.ReadAfterFlush().Returns(_ =>
        {
            order.Add("read");
            return Enumerable.Range(0, 2).Select(index => new FileStoreResult(FileEntity.Rehydrate(10 + index,
                Sha256.FromBytes(new byte[Sha256.ByteLength]), 3, 3, false, DateTimeOffset.UnixEpoch), true, false)).ToArray();
        });
        _files.Configure().PublishStagedAsync(Arg.Any<IReadOnlyList<IStagedFile>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("publish");
            return publication;
        });
        _unitOfWork.When(work => work.FlushAsync(Arg.Any<CancellationToken>())).Do(_ => order.Add("flush"));
        _curation.When(curation => curation.StageAssetAsync(job, Arg.Any<RetainedArtworkContent>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("asset"));
        _curation.When(curation => curation.StagePublicationAsync(job, Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("pin"));
        _curation.When(curation => curation.StageOutcomeAsync(job, 42, false, Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("outcome"));

        await Executor().ExecuteAsync(job, Context());

        order.ShouldBe(requiresFlush
            ? ["publish", "flush", "read", "asset", "flush", "pin", "outcome"]
            : ["publish", "read", "asset", "flush", "pin", "outcome"]);
        job.RetainedAssetId.ShouldBe(42);
        await _files.Received(1).PublishStagedAsync(Arg.Any<IReadOnlyList<IStagedFile>>(), Arg.Any<CancellationToken>());
    }

    private void Ready(ArtworkImportJob job)
    {
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        _curation.LockPendingAsync(job, Arg.Any<CancellationToken>()).Returns(true);
        _source.DownloadAsync(job.ProviderId, job.ProviderGameId, job.ProviderAssetId, job.Role,
                job.TrustedAssetUrl, Arg.Any<CancellationToken>())
            .Returns(new DownloadedArtworkAsset([1, 2, 3], "image/png", null, "https://www.steamgriddb.com/grid/3"));
        _processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), job.Role, Arg.Any<CancellationToken>())
            .Returns(new ProcessedArtworkImage("image/png", 600, 900,
                [new ProcessedArtworkVariant("card", "image/png", 300, 450, [4, 5, 6])]));
        _files.PublishesEveryFile();
        _curation.StagePublicationAsync(job, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(42);
    }

    private ArtworkImportJobExecutor Executor() => new(_source, _processor, _files, _curation, _unitOfWork, _jobs);
    private static JobContext Context() => new(null, _ => Task.CompletedTask, default);
    private static ArtworkImportJob Job()
    {
        var job = ArtworkImportJob.Create(Guid.NewGuid(), "Game", 1, 1, ArtworkRole.Poster, 1,
            "steamgriddb", "2", "3", "https://cdn2.steamgriddb.com/grid/test.png", "Artist", TimeProvider.System.GetUtcNow());
        job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
        return job;
    }
}
