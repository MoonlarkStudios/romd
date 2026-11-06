using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Concurrency;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Artwork;

public sealed class GalleryArtworkServiceTests
{
    [Fact]
    public async Task ImportAsync_InvalidCandidate_DoesNotDownloadOrStore()
    {
        var fixture = new Fixture();
        fixture.Browsing.ValidateCandidateAsync(1, fixture.Actor, "reference", Arg.Any<CancellationToken>())
            .Returns(ArtworkProviderErrors.InvalidRequest());
        var result = await fixture.Service.ImportAsync(1, fixture.Actor, fixture.CandidateId, "reference", fixture.IfMatch, CancellationToken.None);
        result.IsError.ShouldBeTrue();
        await fixture.Source.DidNotReceiveWithAnyArgs().DownloadAsync(default!, default!, default!, default, default!);
        await fixture.UnitOfWork.DidNotReceiveWithAnyArgs().BeginTransactionAsync();
    }

    [Fact]
    public async Task ImportAsync_MatchChangesDuringDownload_DoesNotCommitGalleryAppend()
    {
        var fixture = new Fixture();
        var candidate = new TrustedArtworkCandidate("igdb", "42", "screen", ArtworkRole.Hero,
            "https://images.igdb.com/screen.jpg", "https://images.igdb.com/preview.jpg", "Artist", "https://www.igdb.com/games/test", MediaType.Screenshot);
        fixture.Browsing.ValidateCandidateAsync(1, fixture.Actor, "reference", Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(candidate), Error.Conflict("Match.Changed", "Match changed"));
        fixture.Source.DownloadAsync("igdb", "42", "screen", ArtworkRole.Hero, candidate.TrustedAssetUrl, Arg.Any<CancellationToken>())
            .Returns(new DownloadedArtworkAsset([1], "image/jpeg", "Artist", candidate.SourcePageUrl));
        fixture.Processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), ArtworkRole.Hero, Arg.Any<CancellationToken>())
            .Returns(new ProcessedArtworkImage("image/jpeg", 640, 480, []));
        var publication = Publication();
        fixture.Storage.PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>())
            .Returns(publication);
        var result = await fixture.Service.ImportAsync(1, fixture.Actor, fixture.CandidateId, "reference", fixture.IfMatch, CancellationToken.None);
        result.FirstError.Code.ShouldBe("Match.Changed");
        await fixture.Titles.Received().AppendGalleryMediaStagedAsync(Arg.Is<TitleMedia>(media =>
            media.Type == MediaType.Screenshot && media.SourceId == "gallery:igdb" && media.Attribution == "Artist"), Arg.Any<CancellationToken>());
        await fixture.Transaction.DidNotReceiveWithAnyArgs().CommitAsync();
        Received.InOrder(() =>
        {
            fixture.TitleLock.LockRevisionAsync(1, Arg.Any<CancellationToken>());
            fixture.Storage.PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>());
        });
        await fixture.Transaction.Received().DisposeAsync();
    }

    [Fact]
    public async Task ImportAsync_StaleTitleRevision_FailsThePreconditionBeforeValidatingOrDownloading()
    {
        var fixture = new Fixture();

        var result = await fixture.Service.ImportAsync(1, fixture.Actor, fixture.CandidateId, "reference",
            RevisionPrecondition.For(Guid.NewGuid().ToString()), CancellationToken.None);

        result.FirstError.Code.ShouldBe("Titles.PreconditionFailed");
        await fixture.Browsing.DidNotReceiveWithAnyArgs().ValidateCandidateAsync(default, default, default!, default);
        await fixture.Source.DidNotReceiveWithAnyArgs().DownloadAsync(default!, default!, default!, default, default!);
        await fixture.UnitOfWork.DidNotReceiveWithAnyArgs().BeginTransactionAsync();
    }

    [Fact]
    public async Task ImportAsync_TitleChangedDuringTheDownload_FailsThePreconditionUnderTheLockBeforePublishing()
    {
        var fixture = new Fixture();
        fixture.ImportableCandidate();
        fixture.TitleLock.LockRevisionAsync(1, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());

        var result = await fixture.Service.ImportAsync(1, fixture.Actor, fixture.CandidateId, "reference", fixture.IfMatch,
            CancellationToken.None);

        result.FirstError.Code.ShouldBe("Titles.PreconditionFailed");
        await fixture.Storage.DidNotReceiveWithAnyArgs().PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>());
        await fixture.Titles.DidNotReceiveWithAnyArgs().AppendGalleryMediaStagedAsync(default!, default);
        await fixture.Transaction.DidNotReceiveWithAnyArgs().CommitAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportAsync_AnnouncesAStorageChangeOnlyForANewItem(bool added)
    {
        var fixture = new Fixture();
        fixture.ImportableCandidate();
        fixture.Titles.AppendGalleryMediaStagedAsync(Arg.Any<TitleMedia>(), Arg.Any<CancellationToken>())
            .Returns(new StagedMediaAppend(fixture.Revision, added));
        fixture.TitleMedia.ListAsync(1, Arg.Any<CancellationToken>()).Returns([
            new TitleMediaSummary(7, MediaType.Screenshot, 5, "gallery:igdb", "https://images.igdb.com/screen.jpg",
                "Artist", null, false)
        ]);

        var result = await fixture.Service.ImportAsync(1, fixture.Actor, fixture.CandidateId, "reference", fixture.IfMatch,
            CancellationToken.None);

        result.Value.Created.ShouldBe(added);
        await fixture.Transaction.Received().CommitAsync(Arg.Any<CancellationToken>());
        await fixture.Outbox.Received(added ? 1 : 0)
            .EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_UnknownTitle_IsNotFoundBeforeValidating()
    {
        var fixture = new Fixture();
        fixture.TitleReader.GetRevisionAsync(1, Arg.Any<CancellationToken>()).Returns((Guid?)null);

        var result = await fixture.Service.ImportAsync(1, fixture.Actor, fixture.CandidateId, "reference", fixture.IfMatch,
            CancellationToken.None);

        result.FirstError.Code.ShouldBe("Catalog.TitleNotFound");
        await fixture.Browsing.DidNotReceiveWithAnyArgs().ValidateCandidateAsync(default, default, default!, default);
    }

    [Fact]
    public async Task ImportAsync_ReferenceForAnotherCandidate_IsRejectedBeforeDownloading()
    {
        var fixture = new Fixture();
        fixture.Browsing.ValidateCandidateAsync(1, fixture.Actor, "reference", Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(new TrustedArtworkCandidate("igdb", "42", "screen", ArtworkRole.Hero,
                "https://images.igdb.com/screen.jpg", "https://images.igdb.com/preview.jpg", "Artist",
                "https://www.igdb.com/games/test", MediaType.Screenshot)));

        var result = await fixture.Service.ImportAsync(1, fixture.Actor, ArtworkCandidateIds.Of("https://images.igdb.com/other.jpg"),
            "reference", fixture.IfMatch, CancellationToken.None);

        result.FirstError.Code.ShouldBe("Artwork.CandidateMismatch");
        await fixture.Source.DidNotReceiveWithAnyArgs().DownloadAsync(default!, default!, default!, default, default!);
        await fixture.UnitOfWork.DidNotReceiveWithAnyArgs().BeginTransactionAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAsync_Publication_FlushesOnlyPendingFileIdentityBeforeMediaReferences(bool pending)
    {
        var fixture = new Fixture();
        fixture.ImportableCandidate();
        var order = new List<string>();
        var publication = Publication(pending);
        publication.ReadAfterFlush().Returns(_ =>
        {
            order.Add("resolve-file");
            return FileResult();
        });
        fixture.Storage.PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("publish"); return publication; });
        fixture.UnitOfWork.When(work => work.FlushAsync(Arg.Any<CancellationToken>())).Do(_ => order.Add("flush"));
        fixture.Titles.When(titles => titles.AppendGalleryMediaStagedAsync(Arg.Any<TitleMedia>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("append"));
        fixture.TitleMedia.ListAsync(1, Arg.Any<CancellationToken>()).Returns([
            new TitleMediaSummary(7, MediaType.Screenshot, 5, "gallery:igdb", null, null, null, false)]);

        var result = await fixture.Service.ImportAsync(1, fixture.Actor, fixture.CandidateId, "reference", fixture.IfMatch, CancellationToken.None);

        result.IsError.ShouldBeFalse();
        order.ShouldBe(pending
            ? ["publish", "flush", "resolve-file", "append", "flush"]
            : ["publish", "resolve-file", "append", "flush"]);
    }

    [Fact]
    public async Task ImportAsync_FileIdentityFlushFails_DoesNotStageMediaOrCommit()
    {
        var fixture = new Fixture();
        fixture.ImportableCandidate();
        fixture.UnitOfWork.FlushAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("File flush failed.")));

        await Should.ThrowAsync<IOException>(() => fixture.Service.ImportAsync(1, fixture.Actor, fixture.CandidateId,
            "reference", fixture.IfMatch, CancellationToken.None));

        await fixture.Titles.DidNotReceive().AppendGalleryMediaStagedAsync(Arg.Any<TitleMedia>(), Arg.Any<CancellationToken>());
        await fixture.Transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("append", false)]
    [InlineData("append", true)]
    [InlineData("response", false)]
    [InlineData("response", true)]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    [InlineData("acknowledged", false)]
    public async Task ImportAsync_TransactionAndStagingCleanupFail_PreservesPrimaryOrAcknowledgedResponse(string phase, bool canceled)
    {
        var fixture = new Fixture();
        fixture.ImportableCandidate();
        var staged = Substitute.For<IStagedFile>();
        fixture.Storage.StageAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(staged);
        fixture.TitleMedia.ListAsync(1, Arg.Any<CancellationToken>()).Returns([
            new TitleMediaSummary(7, MediaType.Screenshot, 5, "gallery:igdb", null, "Artist", "https://source", false)]);
        fixture.Transaction.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("transaction cleanup")));
        staged.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("staging cleanup")));
        using var cancellation = new CancellationTokenSource();
        Exception primary = canceled ? new OperationCanceledException(cancellation.Token) : new IOException("primary media failure");
        if (phase == "append")
            fixture.Titles.AppendGalleryMediaStagedAsync(Arg.Any<TitleMedia>(), Arg.Any<CancellationToken>())
                .Returns<StagedMediaAppend>(_ => throw primary);
        if (phase == "response")
            fixture.TitleMedia.ListAsync(1, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TitleMediaSummary>>(_ => throw primary);
        if (phase == "commit")
            fixture.Transaction.When(value => value.CommitAsync(Arg.Any<CancellationToken>())).Do(_ => throw primary);

        if (phase == "acknowledged")
        {
            var result = await fixture.Service.ImportAsync(1, fixture.Actor, fixture.CandidateId, "reference", fixture.IfMatch, cancellation.Token);
            result.Value.Media.Attribution.ShouldBe("Artist");
            result.Value.Media.SourcePageUrl.ShouldBe("https://source");
        }
        else
        {
            Exception? observed = null;
            try
            { await fixture.Service.ImportAsync(1, fixture.Actor, fixture.CandidateId, "reference", fixture.IfMatch, cancellation.Token); }
            catch (Exception error) { observed = error; }
            observed.ShouldBeSameAs(primary);
        }

        await fixture.Transaction.Received(1).DisposeAsync();
        await staged.Received(1).DisposeAsync();
        await fixture.Transaction.Received(phase is "commit" or "acknowledged" ? 1 : 0).CommitAsync(Arg.Any<CancellationToken>());
    }

    private static FileStoreResult FileResult() => new(FileEntity.Rehydrate(5, Sha256.FromSpan(new byte[32]), 1, 1, false,
        DateTimeOffset.UnixEpoch), true, false);

    private static IStagedFilePublication Publication(bool pending = true)
    {
        var publication = Substitute.For<IStagedFilePublication>();
        publication.RequiresFlush.Returns(pending);
        publication.ReadAfterFlush().Returns(FileResult());
        return publication;
    }

    private sealed class Fixture
    {
        public Guid Actor { get; } = Guid.NewGuid();
        public string CandidateId { get; } = ArtworkCandidateIds.Of("https://images.igdb.com/screen.jpg");
        public Guid Revision { get; } = Guid.NewGuid();
        public RevisionPrecondition IfMatch => RevisionPrecondition.For(Revision.ToString());
        public IArtworkBrowsingService Browsing { get; } = Substitute.For<IArtworkBrowsingService>();
        public IArtworkAssetSource Source { get; } = Substitute.For<IArtworkAssetSource>();
        public IArtworkImageProcessor Processor { get; } = Substitute.For<IArtworkImageProcessor>();
        public IFileStorageService Storage { get; } = Substitute.For<IFileStorageService>();
        public ITitleRowLock TitleLock { get; } = Substitute.For<ITitleRowLock>();
        public ITitleMediaStore Titles { get; } = Substitute.For<ITitleMediaStore>();
        public ITitleReader TitleReader { get; } = Substitute.For<ITitleReader>();
        public ITitleMediaReader TitleMedia { get; } = Substitute.For<ITitleMediaReader>();
        public IUnitOfWork UnitOfWork { get; } = Substitute.For<IUnitOfWork>();
        public ITransaction Transaction { get; } = Substitute.For<ITransaction>();
        public IAdminEventOutbox Outbox { get; } = Substitute.For<IAdminEventOutbox>();
        public GalleryArtworkService Service { get; }
        public Fixture()
        {
            TitleReader.GetRevisionAsync(1, Arg.Any<CancellationToken>()).Returns(Revision);
            TitleLock.LockRevisionAsync(1, Arg.Any<CancellationToken>()).Returns(Revision);
            Titles.AppendGalleryMediaStagedAsync(Arg.Any<TitleMedia>(), Arg.Any<CancellationToken>())
                .Returns(new StagedMediaAppend(Guid.NewGuid(), true));
            UnitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Transaction);
            Service = new(Browsing, Source, Processor, Storage, TitleLock, Titles, TitleReader, TitleMedia, UnitOfWork, Outbox, TimeProvider.System, NullLogger<GalleryArtworkService>.Instance);
        }

        /// <summary>A valid Hero screenshot candidate that downloads, processes and publishes.</summary>
        public void ImportableCandidate()
        {
            var candidate = new TrustedArtworkCandidate("igdb", "42", "screen", ArtworkRole.Hero,
                "https://images.igdb.com/screen.jpg", "https://images.igdb.com/preview.jpg", "Artist",
                "https://www.igdb.com/games/test", MediaType.Screenshot);
            Browsing.ValidateCandidateAsync(1, Actor, "reference", Arg.Any<CancellationToken>())
                .Returns(ErrorOrFactory.From(candidate));
            Source.DownloadAsync("igdb", "42", "screen", ArtworkRole.Hero, candidate.TrustedAssetUrl, Arg.Any<CancellationToken>())
                .Returns(new DownloadedArtworkAsset([1], "image/jpeg", "Artist", candidate.SourcePageUrl));
            Processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), ArtworkRole.Hero, Arg.Any<CancellationToken>())
                .Returns(new ProcessedArtworkImage("image/jpeg", 640, 480, []));
            var publication = Publication();
            Storage.PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>())
                .Returns(publication);
        }
    }
}
