using ErrorOr;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Contracts.Management.Artwork;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Artwork;
using Xunit;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class LinkedProviderArtworkServiceTests
{
    [Theory]
    [InlineData("Confirmed")]
    [InlineData("AutoMatched")]
    public async Task FillAsync_TrustedProviderWithLogoCapability_PublishesAutomaticLogoWithoutPinning(string state)
    {
        var acquisition = Substitute.For<IArtworkAcquisitionStore>();
        acquisition.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(
            new ArtworkEnrichmentSettingsDto(Guid.Empty, true, true, true, ["steamgriddb"]));
        acquisition.LockMissingAsync(1, ArtworkRole.Logo, 0, Arg.Any<CancellationToken>()).Returns(true);
        var curation = Substitute.For<IArtworkCurationRepository>();
        curation.GetStatesAsync(1, Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From<IReadOnlyList<ArtworkCurationState>>(value: [
            new(ArtworkRole.Poster, ArtworkSelectionMode.Automatic, 0, null, null),
            new(ArtworkRole.Hero, ArtworkSelectionMode.Automatic, 0, null, null),
            new(ArtworkRole.Logo, ArtworkSelectionMode.Automatic, 0, null, null),
            new(ArtworkRole.Backdrop, ArtworkSelectionMode.Automatic, 0, null, null)]));
        curation.GetGalleryAsync(1, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From<IReadOnlyList<ArtworkAsset>>(value: []));
        var revision = Guid.NewGuid();
        var matches = Substitute.For<ITitleProviderMatchService>();
        matches.GetAsync(1, Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From<IReadOnlyList<TitleProviderMatchDto>>(value: [
            new("steamgriddb", "SteamGridDB", true, ["artwork"], revision, state, "42",
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
        var files = Substitute.For<IFileStorageService>().PublishesEveryFile();
        var service = new LinkedProviderArtworkService([browser], source, matches, acquisition, curation,
            processor, files, Substitute.For<IUnitOfWork>(), Options.Create(new EnrichmentOptions()));
        var title = Title.Rehydrate(1, 10, "Game", "game", null, null, null, null,
            null, null, null, EnrichmentStatus.Completed, null, DateTimeOffset.UtcNow);
        var platform = Platform.Rehydrate(10, "Super Nintendo", "snes", "Nintendo", DateTimeOffset.UtcNow);

        await service.FillAsync(title, platform, new JobContext(null, _ => Task.CompletedTask, CancellationToken.None),
            CancellationToken.None);

        await curation.Received(1).StageLocalAssetAsync(1, ArtworkRole.Logo, "steamgriddb",
            Arg.Is<RetainedArtworkContent>(content => content.Variants.Select(x => x.Name).SequenceEqual(new[] { "thumb", "logo" }) &&
                content.Attribution == "Artist"), Arg.Any<CancellationToken>(), "42", "7");
        await curation.DidNotReceiveWithAnyArgs().StagePinAsync(default, default, default, default!, default);
        await acquisition.Received(1).RecordAsync(1, ArtworkRole.Logo, "Updated", "steamgriddb",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FillAsync_FirstProviderHasUnsuitableCandidate_FallsThroughInRolePolicyOrder()
    {
        var acquisition = Substitute.For<IArtworkAcquisitionStore>();
        acquisition.GetSettingsAsync(default).ReturnsForAnyArgs(new ArtworkEnrichmentSettingsDto(Guid.Empty, true, false, false, [], false));
        acquisition.GetProviderOrderAsync(ArtworkRole.Poster, default).ReturnsForAnyArgs(new[] { "steamgriddb", "igdb" });
        var curation = Substitute.For<IArtworkCurationRepository>();
        curation.GetStatesAsync(1, default).ReturnsForAnyArgs(ErrorOrFactory.From<IReadOnlyList<ArtworkCurationState>>(
            Enum.GetValues<ArtworkRole>().Select(x => new ArtworkCurationState(x, ArtworkSelectionMode.Automatic, 0, null, null)).ToArray()));
        curation.GetGalleryAsync(1, default).ReturnsForAnyArgs(ErrorOrFactory.From<IReadOnlyList<ArtworkAsset>>(value: []));
        var matches = Substitute.For<ITitleProviderMatchService>();
        matches.GetAsync(1, default).ReturnsForAnyArgs(ErrorOrFactory.From<IReadOnlyList<TitleProviderMatchDto>>(
            new[] { "steamgriddb", "igdb" }.Select(x => new TitleProviderMatchDto(x, x, true, ["artwork"], Guid.NewGuid(), "Confirmed", "42", null, null, null, false)).ToArray()));
        var calls = new List<string>();
        var browsers = new[] { "igdb", "steamgriddb" }.Select(id =>
        {
            var browser = Substitute.For<IArtworkProviderBrowser>();
            browser.Capabilities.Returns(new ArtworkProviderCapabilities(id, id, [new(ArtworkRole.Poster, [], [])]));
            browser.GetCandidatesAsync("42", Arg.Any<ProviderArtworkQuery>(), 0, default).ReturnsForAnyArgs(_ =>
            {
                calls.Add(id);
                return new ProviderArtworkPage([new("42", "1", ArtworkRole.Poster, 10, 10, null,
                    new("https://example.test/thumb"), new("https://example.test/image"), null, "https://example.test")], null);
            });
            return browser;
        }).ToArray();
        using var emptyCache = new MemoryCache(new MemoryCacheOptions());
        var service = new LinkedProviderArtworkService(browsers, Substitute.For<IArtworkAssetSource>(), matches,
            acquisition, curation, Substitute.For<IArtworkImageProcessor>(), Substitute.For<IFileStorageService>(), Substitute.For<IUnitOfWork>(),
            Options.Create(new EnrichmentOptions()), candidateCache: emptyCache);
        var title = Title.Rehydrate(1, 10, "Game", "game", null, null, null, null,
            null, null, null, EnrichmentStatus.Completed, null, DateTimeOffset.UtcNow);
        var platform = Platform.Rehydrate(10, "Super Nintendo", "snes", "Nintendo", DateTimeOffset.UtcNow);
        await service.FillAsync(title, platform, new JobContext(null, _ => Task.CompletedTask, default), default);
        calls.ShouldBe(["steamgriddb", "igdb"]);
        await acquisition.Received(1).RecordAsync(1, ArtworkRole.Poster, "Unavailable", "steamgriddb", default, null);

        calls.Clear();
        await service.FillAsync(title, platform, new JobContext(null, _ => Task.CompletedTask, default), default);
        calls.ShouldBeEmpty();
        // A changed association must immediately search again, even within the empty-result cache window.
        matches.GetAsync(1, default).ReturnsForAnyArgs(ErrorOrFactory.From<IReadOnlyList<TitleProviderMatchDto>>(
            new[] { "steamgriddb", "igdb" }.Select(x => new TitleProviderMatchDto(x, x, true, ["artwork"], Guid.NewGuid(), "Confirmed", "43", null, null, null, false)).ToArray()));
        await service.FillAsync(title, platform, new JobContext(null, _ => Task.CompletedTask, default), default);
        calls.ShouldBe(["steamgriddb", "igdb"]);
        calls.Clear();
        curation.GetStatesAsync(1, default).ReturnsForAnyArgs(ErrorOrFactory.From<IReadOnlyList<ArtworkCurationState>>(
            Enum.GetValues<ArtworkRole>().Select(x => new ArtworkCurationState(x, ArtworkSelectionMode.Empty, 1, null, null)).ToArray()));
        matches.ClearReceivedCalls();
        await service.FillAsync(title, platform, new JobContext(null, _ => Task.CompletedTask, default), default);
        calls.ShouldBeEmpty();
        await matches.DidNotReceiveWithAnyArgs().DiscoverAsync(default, default!, default);
    }

    [Fact]
    public async Task FillAsync_UnassociatedProviders_UsesSharedIdentityDiscovery()
    {
        var acquisition = Substitute.For<IArtworkAcquisitionStore>();
        acquisition.GetSettingsAsync(default).ReturnsForAnyArgs(new ArtworkEnrichmentSettingsDto(Guid.Empty, true, false, false, [], false));
        var curation = Substitute.For<IArtworkCurationRepository>();
        curation.GetStatesAsync(1, default).ReturnsForAnyArgs(ErrorOrFactory.From<IReadOnlyList<ArtworkCurationState>>(
            Enum.GetValues<ArtworkRole>().Select(x => new ArtworkCurationState(x, ArtworkSelectionMode.Automatic, 0, null, null)).ToArray()));
        curation.GetGalleryAsync(1, default).ReturnsForAnyArgs(ErrorOrFactory.From<IReadOnlyList<ArtworkAsset>>(value: []));
        var matches = Substitute.For<ITitleProviderMatchService>();
        matches.GetAsync(1, default).ReturnsForAnyArgs(ErrorOrFactory.From<IReadOnlyList<TitleProviderMatchDto>>(value: []));
        var service = new LinkedProviderArtworkService([], Substitute.For<IArtworkAssetSource>(), matches,
            acquisition, curation, Substitute.For<IArtworkImageProcessor>(), Substitute.For<IFileStorageService>(), Substitute.For<IUnitOfWork>(),
            Options.Create(new EnrichmentOptions()));
        var title = Title.Rehydrate(1, 10, "Game", "game", null, null, null, null,
            null, null, null, EnrichmentStatus.Completed, null, DateTimeOffset.UtcNow);
        var platform = Platform.Rehydrate(10, "Super Nintendo", "snes", "Nintendo", DateTimeOffset.UtcNow);
        await service.FillAsync(title, platform, new JobContext(null, _ => Task.CompletedTask, default), default);
        await matches.Received(1).DiscoverAsync(1, Arg.Any<JobContext>(), default);
    }
}
