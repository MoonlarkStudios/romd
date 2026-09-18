using ErrorOr;
using System.Security.Cryptography;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Persistence.Entities;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class ArtworkImportJobExecutorTests
{
    private readonly IArtworkAssetSource _source = Substitute.For<IArtworkAssetSource>();
    private readonly IArtworkImageProcessor _processor = Substitute.For<IArtworkImageProcessor>();
    private readonly IContentAddressableStore _store = Substitute.For<IContentAddressableStore>();
    private readonly IArtworkCurationRepository _curation = Substitute.For<IArtworkCurationRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IFileMutationLock _fileLocks = Substitute.For<IFileMutationLock>();
    private readonly IJobRepository<ArtworkImportJob> _jobs = Substitute.For<IJobRepository<ArtworkImportJob>>();

    [Fact]
    public async Task ExecuteAsync_SupersededBeforeDownload_DoesNotFetchOrRetain()
    {
        var job = Job();
        await Executor().ExecuteAsync(job, Context());
        job.WasSuperseded.ShouldBeTrue();
        _source.ReceivedCalls().ShouldBeEmpty();
        _store.ReceivedCalls().ShouldBeEmpty();
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
        _store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_InvalidImage_DoesNotPublishOrRecordSuccess()
    {
        var job = Job();
        Ready(job);
        _processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), job.Role, Arg.Any<CancellationToken>())
            .Returns(ErrorOr<ProcessedArtworkImage>.From([ArtworkImageErrors.Invalid()]));
        await Should.ThrowAsync<JobPermanentFailureException>(() => Executor().ExecuteAsync(job, Context()));
        job.RetainedAssetId.ShouldBeNull();
        job.WasSuperseded.ShouldBeFalse();
        _store.ReceivedCalls().ShouldBeEmpty();
        await _curation.DidNotReceive().StagePublicationAsync(job, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ProviderAssetGone_FailsPermanentlyWithoutRetaining()
    {
        var job = Job();
        Ready(job);
        _source.DownloadAsync(job.ProviderId, job.ProviderGameId, job.ProviderAssetId, job.Role,
                job.TrustedAssetUrl, Arg.Any<CancellationToken>())
            .Returns(ErrorOr<DownloadedArtworkAsset>.From([ArtworkProviderErrors.NotFound()]));

        await Should.ThrowAsync<JobPermanentFailureException>(() => Executor().ExecuteAsync(job, Context()));

        _processor.ReceivedCalls().ShouldBeEmpty();
        _store.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteAsync_TransientFailure_StopsOnThirdPersistedAttempt(int priorFailures)
    {
        var job = Job();
        for (var i = 0; i < priorFailures; i++) job.RecordError("attempt", "Earlier failed delivery");
        job = ArtworkImportJobEntity.FromDomain(job).ToDomain();
        Ready(job);
        _source.DownloadAsync(job.ProviderId, job.ProviderGameId, job.ProviderAssetId, job.Role,
                job.TrustedAssetUrl, Arg.Any<CancellationToken>())
            .Returns(ErrorOr<DownloadedArtworkAsset>.From([ArtworkProviderErrors.Unavailable()]));

        if (priorFailures == 2)
            await Should.ThrowAsync<JobPermanentFailureException>(() => Executor().ExecuteAsync(job, Context()));
        else
            await Should.ThrowAsync<InvalidOperationException>(() => Executor().ExecuteAsync(job, Context()));

        job.Errors.Count.ShouldBe(priorFailures); // Only the runner writes durable attempt errors.
        _store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_PreviouslyExhaustedDelivery_DoesNotDownloadAgain()
    {
        var job = Job();
        for (var i = 0; i < 3; i++) job.RecordError("attempt", "Earlier failed delivery");
        job = ArtworkImportJobEntity.FromDomain(job).ToDomain();
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        await Should.ThrowAsync<JobPermanentFailureException>(() => Executor().ExecuteAsync(job, Context()));

        _source.ReceivedCalls().ShouldBeEmpty();
        _store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ThirdAttemptInterruptedByShutdown_DoesNotBecomePermanentFailure()
    {
        var job = Job();
        Ready(job);
        job.RecordError("attempt", "First");
        job.RecordError("attempt", "Second");
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
        job.RecordError("attempt", "First");
        job.RecordError("attempt", "Second");
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
                if (++mutations == 2) throw new IOException("Simulated transaction commit failure");
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
        job.RecordError("attempt", "First");
        job.RecordError("attempt", "Second");
        var persisted = ArtworkImportJobEntity.FromDomain(job).ToDomain();
        persisted.SetImportOutcome(42, false);
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(persisted);

        await Executor().ExecuteAsync(job, CommitAcknowledgementFailure());

        job.RetainedAssetId.ShouldBe(42);
        job.WasSuperseded.ShouldBeFalse();
        job.Complete();
        job.PhaseEnum.ShouldBe(ArtworkImportPhase.Completed);
        await _jobs.Received(1).GetByIdAsync(job.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ThirdAttemptCommitFailsAndNoDurableOutcome_FailsPermanently()
    {
        var job = Job();
        Ready(job);
        job.RecordError("attempt", "First");
        job.RecordError("attempt", "Second");
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
        job.RecordError("attempt", "First");
        job.RecordError("attempt", "Second");
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
        for (var index = 0; index < 3; index++) job.RecordError("attempt", "Previous attempt");
        var persisted = ArtworkImportJobEntity.FromDomain(job).ToDomain();
        persisted.SetImportOutcome(42, false);
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(persisted);

        await Executor().ExecuteAsync(job, Context());

        job.RetainedAssetId.ShouldBe(42);
        _source.ReceivedCalls().ShouldBeEmpty();
        _store.ReceivedCalls().ShouldBeEmpty();
    }

    private static JobContext CommitAcknowledgementFailure()
    {
        var mutations = 0;
        return new JobContext(null, _ => Task.CompletedTask, default,
            ownedMutationDelegate: async (mutation, ct) =>
            {
                await mutation(ct);
                if (++mutations == 2) throw new IOException("Commit acknowledgement unavailable");
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
        await _store.Received(2).StoreAsync(Arg.Any<Stream>(), null, Arg.Any<CancellationToken>());
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
        job.Complete();
        job.PhaseEnum.ShouldBe(ArtworkImportPhase.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_Publication_AcquiresEveryHashInStableOrderBeforeStoring()
    {
        var job = Job();
        Ready(job);
        var expected = new[] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 } }
            .Select(bytes => Sha256.FromSpan(SHA256.HashData(bytes)).ToString())
            .Order(StringComparer.Ordinal).ToArray();
        var acquired = new List<string>();
        _fileLocks.AcquireAsync(Arg.Any<Sha256>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            acquired.Add(call.Arg<Sha256>().ToString());
            return Task.CompletedTask;
        });
        _store.StoreAsync(Arg.Any<Stream>(), null, Arg.Any<CancellationToken>()).Returns(call =>
        {
            acquired.ShouldBe(expected);
            using var buffer = new MemoryStream();
            call.Arg<Stream>().CopyTo(buffer);
            var hash = Sha256.FromSpan(SHA256.HashData(buffer.ToArray()));
            return new StoreResult(StorageKey.FromHash(hash), buffer.Length, buffer.Length, false, false);
        });
        await Executor().ExecuteAsync(job, Context());
        acquired.ShouldBe(expected);
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
        var hash = Sha256.Parse(new string('a', 64));
        _store.StoreAsync(Arg.Any<Stream>(), null, Arg.Any<CancellationToken>())
            .Returns(new StoreResult(StorageKey.FromHash(hash), 3, 3, false, false));
        _curation.StagePublicationAsync(job, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(42);
    }

    private ArtworkImportJobExecutor Executor() => new(_source, _processor, _store, _curation, _fileLocks, _unitOfWork, _jobs);
    private static JobContext Context() => new(null, _ => Task.CompletedTask, default);
    private static ArtworkImportJob Job()
    {
        var job = ArtworkImportJob.Create(Guid.NewGuid(), "Game", 1, 1, ArtworkRole.Poster, 1,
            "steamgriddb", "2", "3", "https://cdn2.steamgriddb.com/grid/test.png", "Artist", TimeProvider.System);
        job.Start("delivery");
        return job;
    }
}
