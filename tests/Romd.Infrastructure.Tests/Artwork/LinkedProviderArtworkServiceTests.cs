using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Contracts.Management.Artwork;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Artwork;
using Romd.Storage;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class LinkedProviderArtworkServiceTests
{
    [Fact]
    public async Task FillAsync_ConfirmedProviderWithLogoCapability_PublishesAutomaticLogoWithoutPinning()
    {
        var acquisition = Substitute.For<IArtworkAcquisitionStore>();
        acquisition.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(
            new ArtworkEnrichmentSettingsDto(Guid.Empty, true, true, true, ["steamgriddb"]));
        acquisition.LockMissingAsync(1, ArtworkRole.Logo, 0, Arg.Any<CancellationToken>()).Returns(true);
        var curation = Substitute.For<IArtworkCurationRepository>();
        curation.GetStatesAsync(1, Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From<IReadOnlyList<ArtworkCurationState>>([
            new(ArtworkRole.Poster, ArtworkSelectionMode.Automatic, 0, null, null),
            new(ArtworkRole.Hero, ArtworkSelectionMode.Automatic, 0, null, null),
            new(ArtworkRole.Logo, ArtworkSelectionMode.Automatic, 0, null, null),
            new(ArtworkRole.Backdrop, ArtworkSelectionMode.Automatic, 0, null, null)]));
        curation.GetGalleryAsync(1, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From<IReadOnlyList<ArtworkAsset>>([]));
        var revision = Guid.NewGuid();
        var matches = Substitute.For<ITitleProviderMatchService>();
        matches.GetAsync(1, Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From<IReadOnlyList<TitleProviderMatchDto>>([
            new("steamgriddb", "SteamGridDB", true, ["artwork"], revision, "Confirmed", "42",
                new("42", "Game", "https://www.steamgriddb.com/game/42"), null, null, false)]));
        matches.GetLinkRevisionAsync(1, "steamgriddb", "42", Arg.Any<CancellationToken>()).Returns(revision);
        var browser = Substitute.For<IArtworkProviderBrowser>();
        browser.Capabilities.Returns(new ArtworkProviderCapabilities("steamgriddb", "SteamGridDB",
            [new(ArtworkRole.Logo, [], ["official"])], MediaTypes: [MediaType.Logo]));
        var candidate = new ProviderArtworkCandidate("42", "7", ArtworkRole.Logo, 1200, 300, "official",
            new("https://cdn2.steamgriddb.com/logo_thumb/logo.png"),
            new("https://cdn2.steamgriddb.com/logo/logo.png"), "Artist", "https://www.steamgriddb.com/logo/7");
        browser.GetCandidatesAsync("42", Arg.Is<ProviderArtworkQuery>(query => query.Role == ArtworkRole.Logo), 0,
            Arg.Any<CancellationToken>()).Returns(new ProviderArtworkPage([candidate], null));
        var source = Substitute.For<IArtworkAssetSource>();
        source.DownloadAsync("steamgriddb", "42", "7", ArtworkRole.Logo, candidate.AssetUrl.AbsoluteUri,
            Arg.Any<CancellationToken>()).Returns(new DownloadedArtworkAsset([1, 2, 3], "image/png", "Artist", candidate.SourcePageUrl));
        var processor = Substitute.For<IArtworkImageProcessor>();
        processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), ArtworkRole.Logo, Arg.Any<CancellationToken>())
            .Returns(new ProcessedArtworkImage("image/png", 1200, 300,
                [new("thumb", "image/png", 480, 120, [4]), new("logo", "image/png", 960, 240, [5])]));
        var storage = Substitute.For<IContentAddressableStore>();
        storage.StoreAsync(Arg.Any<Stream>(), Arg.Any<IProgress<StoreProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(new StoreResult(StorageKey.FromHash(Hash()), 1, 1, false, false));
        var metadata = MetadataService(acquisition, curation, Options.Create(new EnrichmentOptions { DownloadMedia = false }));
        var service = new LinkedProviderArtworkService(metadata, [browser], source, matches, acquisition, curation,
            processor, storage, Substitute.For<IFileMutationLock>(), Options.Create(new EnrichmentOptions()));
        var title = Title.Rehydrate(1, 10, "Game", "game", null, null, null, null,
            null, null, null, EnrichmentStatus.Completed, null, DateTimeOffset.UtcNow);
        var platform = Platform.Rehydrate(10, "Super Nintendo", "snes", "Nintendo", DateTimeOffset.UtcNow);

        await service.FillAsync(title, platform, new JobContext(null, _ => Task.CompletedTask, CancellationToken.None),
            CancellationToken.None);

        await curation.Received(1).StageLocalAssetAsync(1, ArtworkRole.Logo, "steamgriddb",
            Arg.Is<RetainedArtworkContent>(content => content.Variants.Select(x => x.Name).SequenceEqual(new[] { "thumb", "logo" }) &&
                content.Attribution == "Artist"), Guid.Empty, Arg.Any<CancellationToken>(), "42", "7");
        await curation.DidNotReceiveWithAnyArgs().StagePinAsync(default, default, default, default, default);
        await acquisition.Received(1).RecordAsync(1, ArtworkRole.Logo, "Updated", "steamgriddb",
            Arg.Any<CancellationToken>());
    }

    private static AutomaticArtworkService MetadataService(IArtworkAcquisitionStore acquisition,
        IArtworkCurationRepository curation, IOptions<EnrichmentOptions> options) => new(acquisition, curation,
        Substitute.For<IArtworkImageProcessor>(), Substitute.For<IFileStorageService>(),
        Substitute.For<IContentAddressableStore>(), Substitute.For<IFileMutationLock>(), [],
        Substitute.For<IEnrichmentContextFactory>(), options, Substitute.For<IHttpClientFactory>(),
        NullLogger<AutomaticArtworkService>.Instance);

    private static Sha256 Hash() => Sha256.FromSpan(System.Security.Cryptography.SHA256.HashData([1]));
}
