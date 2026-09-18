using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
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
        var result = await fixture.Service.ImportAsync(1, fixture.Actor, "reference", CancellationToken.None);
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
        fixture.Storage.StoreAsync(Arg.Any<Stream>(), ct: Arg.Any<CancellationToken>())
            .Returns(new FileStoreResult(FileEntity.CreateNew(Sha256.FromSpan(new byte[32]), 1, 1, false), true, false));
        var result = await fixture.Service.ImportAsync(1, fixture.Actor, "reference", CancellationToken.None);
        result.FirstError.Code.ShouldBe("Match.Changed");
        await fixture.Titles.Received().AppendGalleryMediaStagedAsync(Arg.Is<TitleMedia>(media =>
            media.Type == MediaType.Screenshot && media.SourceId == "gallery:igdb" && media.Attribution == "Artist"), Arg.Any<CancellationToken>());
        await fixture.Transaction.DidNotReceiveWithAnyArgs().CommitAsync();
        await fixture.Transaction.Received().DisposeAsync();
    }

    private sealed class Fixture
    {
        public Guid Actor { get; } = Guid.NewGuid();
        public IArtworkBrowsingService Browsing { get; } = Substitute.For<IArtworkBrowsingService>();
        public IArtworkAssetSource Source { get; } = Substitute.For<IArtworkAssetSource>();
        public IArtworkImageProcessor Processor { get; } = Substitute.For<IArtworkImageProcessor>();
        public IFileStorageService Storage { get; } = Substitute.For<IFileStorageService>();
        public ITitleRepository Titles { get; } = Substitute.For<ITitleRepository>();
        public IUnitOfWork UnitOfWork { get; } = Substitute.For<IUnitOfWork>();
        public ITransaction Transaction { get; } = Substitute.For<ITransaction>();
        public GalleryArtworkService Service { get; }
        public Fixture()
        {
            Titles.GetWithCollectionsAsync(1, Arg.Any<CancellationToken>()).Returns(Title.CreateNew(1, "Test", "test"));
            UnitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Transaction);
            Service = new(Browsing, Source, Processor, Storage, Titles, UnitOfWork, Substitute.For<IAdminEventOutbox>());
        }
    }
}
