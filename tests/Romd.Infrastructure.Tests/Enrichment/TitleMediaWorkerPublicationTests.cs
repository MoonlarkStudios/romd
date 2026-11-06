using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Dashboard;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Platform;
using Romd.Domain.Storage;
using Romd.Infrastructure.Jobs.Executors;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class TitleMediaWorkerPublicationTests
{
    [Theory]
    [InlineData("single", true)]
    [InlineData("single", false)]
    [InlineData("bulk-enriched", true)]
    [InlineData("bulk-enriched", false)]
    [InlineData("bulk-not-found", true)]
    [InlineData("bulk-not-found", false)]
    public async Task ExistingOwner_PublishesUnderTitleLockAndFlushesOnlyNewIdentitiesBeforeAttaching(string path, bool requiresFlush)
    {
        var fixture = new Fixture(path, requiresFlush);

        await fixture.RunAsync();

        fixture.Events.Where(value => value is "title-lock" or "publish" or "flush" or "read" or "media-time" or "metadata")
            .ShouldBe(requiresFlush
                ? new[] { "title-lock", "publish", "flush", "read", "media-time", "metadata" }
                : ["title-lock", "publish", "read", "media-time", "metadata"]);
        fixture.Title.Media.Select(media => (media.Type, media.FileId)).ShouldBe([
            (MediaType.Screenshot, 17), (MediaType.Logo, requiresFlush ? 18 : 17)]);
        fixture.Title.Media.ShouldAllBe(media => media.CreatedAt == Fixture.Now && media.CreatedAt.Offset == Fixture.Now.Offset);
        fixture.FlushCount.ShouldBe(requiresFlush ? 1 : 0);
        fixture.MetadataCalls.ShouldBe(1);
        fixture.Downloads.ShouldAllBe(download => download.DisposeCount == 1);
        foreach (var file in fixture.Files)
            _ = file.Received(1).DisposeAsync();
        await fixture.Storage.Received(1).PublishStagedAsync(Arg.Any<IReadOnlyList<IStagedFile>>(), Arg.Any<CancellationToken>());
        await fixture.Storage.DidNotReceiveWithAnyArgs().PublishStagedAsync(default(IStagedFile)!, default);
        await fixture.Work.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("bulk-enriched")]
    [InlineData("bulk-not-found")]
    public async Task ExistingOwner_EmptyMediaDoesNotLockPublishFlushOrReadFileReceipts(string path)
    {
        var fixture = new Fixture(path, requiresFlush: true, empty: true);

        await fixture.RunAsync();

        fixture.Events.ShouldNotContain("title-lock");
        fixture.Events.ShouldNotContain("publish");
        fixture.Events.ShouldNotContain("read");
        fixture.FlushCount.ShouldBe(0);
        fixture.MetadataCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData("single", false)]
    [InlineData("single", true)]
    [InlineData("bulk-enriched", false)]
    [InlineData("bulk-enriched", true)]
    [InlineData("bulk-not-found", false)]
    [InlineData("bulk-not-found", true)]
    public async Task ExistingOwner_IdentityFlushFaultOrCancellationNeverAttachesAndReleasesPreparedInputs(string path, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture(path, requiresFlush: true);
        var failure = new IOException("identity flush failed");
        fixture.InterruptFlush = () =>
        {
            if (cancel)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            throw failure;
        };

        if (cancel)
            (await Should.ThrowAsync<OperationCanceledException>(() => fixture.RunAsync(cancellation.Token)))
                .CancellationToken.ShouldBe(cancellation.Token);
        else
            (await Should.ThrowAsync<IOException>(() => fixture.RunAsync())).ShouldBeSameAs(failure);

        fixture.Events.ShouldNotContain("read");
        fixture.Title.Media.ShouldBeEmpty();
        fixture.MetadataCalls.ShouldBe(0);
        fixture.Downloads.ShouldAllBe(download => download.DisposeCount == 1);
        foreach (var file in fixture.Files)
            _ = file.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData("single")]
    [InlineData("bulk-enriched")]
    [InlineData("bulk-not-found")]
    public async Task ExistingOwner_RemovedTitleRejectsBeforeFilePublication(string path)
    {
        var fixture = new Fixture(path, requiresFlush: true) { TitleExists = false };

        (await Should.ThrowAsync<InvalidOperationException>(() => fixture.RunAsync()))
            .Message.ShouldBe("Title was removed during enrichment.");

        fixture.Events.ShouldNotContain("publish");
        fixture.FlushCount.ShouldBe(0);
        fixture.Title.Media.ShouldBeEmpty();
        fixture.Downloads.ShouldAllBe(download => download.DisposeCount == 1);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("bulk-enriched")]
    [InlineData("bulk-not-found")]
    public async Task ExistingOwner_LostFenceBeforeCallbackDoesNotPublishPreparedMedia(string path)
    {
        var fixture = new Fixture(path, requiresFlush: true) { RejectMutation = true };

        await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => fixture.RunAsync());

        fixture.Events.ShouldNotContain("title-lock");
        fixture.Events.ShouldNotContain("publish");
        fixture.FlushCount.ShouldBe(0);
        fixture.Downloads.ShouldAllBe(download => download.DisposeCount == 1);
    }

    private sealed class Fixture
    {
        internal static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 4, 10, 9, 8, TimeSpan.FromHours(3)).AddTicks(1234567);
        private readonly string _path;
        private readonly bool _requiresFlush;
        private readonly ITitleAggregateReader _reader = Substitute.For<ITitleAggregateReader>();
        private readonly ITitleMetadataStore _metadata = Substitute.For<ITitleMetadataStore>();
        private readonly ITitleRowLock _titleLock = Substitute.For<ITitleRowLock>();
        private readonly IPlatformRepository _platforms = Substitute.For<IPlatformRepository>();
        private readonly IEnrichmentOrchestrator _orchestrator = Substitute.For<IEnrichmentOrchestrator>();
        private readonly IAutomaticArtworkService _artwork = Substitute.For<IAutomaticArtworkService>();
        private readonly IStagedFilePublicationBatch _publication = Substitute.For<IStagedFilePublicationBatch>();
        private readonly OrchestrationResult _result;
        private bool _insideMutation;
        private bool _flushed;
        public List<string> Events { get; } = [];
        public IFileStorageService Storage { get; } = Substitute.For<IFileStorageService>();
        public IUnitOfWork Work { get; } = Substitute.For<IUnitOfWork>();
        public IStagedFile[] Files { get; } = [Substitute.For<IStagedFile>(), Substitute.For<IStagedFile>()];
        public Download[] Downloads { get; } = [new(), new()];
        public Title Title { get; } = Title.Rehydrate(7, 3, "Snapshot", "snapshot", null, null, null, null, null, null, null,
            EnrichmentStatus.Pending, null, Now);
        private Platform Platform { get; } = Platform.Rehydrate(3, "SNES", "snes", "Nintendo", Now);
        public bool TitleExists { get; init; } = true;
        public bool RejectMutation { get; init; }
        public Action? InterruptFlush { get; set; }
        public int FlushCount { get; private set; }
        public int MetadataCalls { get; private set; }

        public Fixture(string path, bool requiresFlush, bool empty = false)
        {
            _path = path;
            _requiresFlush = requiresFlush;
            _result = (path == "bulk-not-found" ? OrchestrationResult.NotFound : OrchestrationResult.Found(1)) with
            {
                StagedMedia = empty ? [] : [
                    new(MediaType.Screenshot, "provider", "image/png", "https://images/first", Files[0], Downloads[0]),
                    new(MediaType.Logo, "provider", "image/png", "https://images/second", Files[1], Downloads[1])]
            };
            _reader.GetAggregateAsync(7, Arg.Any<CancellationToken>()).Returns(Title);
            _platforms.GetByIdAsync(3, Arg.Any<CancellationToken>()).Returns(Platform);
            _orchestrator.EnrichTitleAsync(Title, Platform, Arg.Any<CancellationToken>()).Returns(_result);
            _titleLock.LockAsync(7, Arg.Any<CancellationToken>()).Returns(_ =>
            {
                _insideMutation.ShouldBeTrue();
                Events.Add("title-lock");
                return TitleExists;
            });
            Storage.PublishStagedAsync(Arg.Any<IReadOnlyList<IStagedFile>>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                _insideMutation.ShouldBeTrue();
                Events.Last().ShouldBe("title-lock");
                call.Arg<IReadOnlyList<IStagedFile>>().ShouldBe(Files);
                Events.Add("publish");
                return _publication;
            });
            _publication.RequiresFlush.Returns(requiresFlush);
            _publication.ReadAfterFlush().Returns(_ =>
            {
                _insideMutation.ShouldBeTrue();
                (_flushed || !_requiresFlush).ShouldBeTrue("new file identities must be flushed by this owner before reading");
                Events.Add("read");
                return new[] { Published(17), Published(_requiresFlush ? 18 : 17) };
            });
            Work.FlushAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                _insideMutation.ShouldBeTrue();
                Events.Last().ShouldBe("publish");
                FlushCount++;
                Events.Add("flush");
                InterruptFlush?.Invoke();
                _flushed = true;
                return Task.CompletedTask;
            });
            _metadata.ApplyEnrichmentStagedAsync(Title, Arg.Any<CancellationToken>()).Returns(_ =>
            {
                _insideMutation.ShouldBeTrue();
                MetadataCalls++;
                Events.Add("metadata");
                Title.Media.Count.ShouldBe(empty ? 0 : 2);
                return Task.CompletedTask;
            });
        }

        public async Task RunAsync(CancellationToken ct = default)
        {
            var clock = new RecordingClock(this);
            var context = new JobContext(null, _ => Task.CompletedTask, ct, ownedMutationDelegate: async (mutation, token) =>
            {
                if (RejectMutation)
                    throw new JobExecutionOwnershipLostException();
                _insideMutation.ShouldBeFalse();
                _insideMutation = true;
                try
                { await mutation(token); }
                finally { _insideMutation = false; }
            });
            var options = Options.Create(new EnrichmentOptions());
            if (_path == "single")
            {
                var pipeline = new TitleEnrichmentPipeline(_reader, _metadata, _titleLock, _platforms, _orchestrator,
                    Storage, Work, Substitute.For<IStatsNotifier>(), Substitute.For<ILibraryMaterializationService>(), options,
                    NullLogger<TitleEnrichmentPipeline>.Instance, _artwork, Substitute.For<IArtworkCompletionStore>(), clock);
                await pipeline.EnrichAsync(7, 3, context);
            }
            else
            {
                await using var result = _result;
                var processor = new BulkEnrichmentTitleProcessor(_reader, _metadata, _titleLock, _platforms, Storage, Work,
                    Substitute.For<IJobNotifier>(), Substitute.For<ILibraryMaterializationService>(), options,
                    NullLogger<BulkEnrichmentTitleProcessor>.Instance, _artwork, clock);
                var job = BulkEnrichmentJob.Create(3, "snes", Now);
                job.Start("title-media-publication-fixture", Now);
                await processor.ProcessResultAsync(job, context, Title, Platform, result, new HashSet<int>());
            }
        }

        private FileStoreResult Published(int id) => new(
            FileEntity.Rehydrate(id, Sha256.FromSpan(new byte[32]), 1, 1, false, Now), _requiresFlush, !_requiresFlush);

        private sealed class RecordingClock(Fixture fixture) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow()
            {
                if (fixture._insideMutation)
                    fixture.Events.Add("media-time");
                return Now;
            }
        }
    }

    public sealed class Download : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
}
