using System.Net;
using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Artwork;
using Romd.Infrastructure.Tests.Enrichment.Helpers;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class AutomaticArtworkServiceTests
{
    private readonly IArtworkAcquisitionStore _acquisition = Substitute.For<IArtworkAcquisitionStore>();
    private readonly IArtworkCurationRepository _curation = Substitute.For<IArtworkCurationRepository>();
    private readonly IArtworkImageProcessor _processor = Substitute.For<IArtworkImageProcessor>();
    private readonly IFileStorageService _files = Substitute.For<IFileStorageService>();
    private readonly IContentAddressableStore _storage = Substitute.For<IContentAddressableStore>();
    private readonly IFileMutationLock _locks = Substitute.For<IFileMutationLock>();
    private readonly IEnrichmentContextFactory _contexts = Substitute.For<IEnrichmentContextFactory>();
    private readonly IHttpClientFactory _clients = Substitute.For<IHttpClientFactory>();
    private readonly Handler _http = new();
    private readonly Title _title = Title.Rehydrate(1, 10, "Game", "game", null, null, null, null,
        null, null, null, EnrichmentStatus.Completed, null, DateTimeOffset.UtcNow);
    private readonly Platform _platform = Platform.Rehydrate(10, "Super Nintendo", "snes", "Nintendo", DateTimeOffset.UtcNow);
    private readonly EnrichmentOptions _options = new();
    private readonly FakeMetadataProvider _provider = new("igdb", _ => EnrichmentResult.NotFound());

    public AutomaticArtworkServiceTests()
    {
        _acquisition.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new ArtworkEnrichmentSettingsDto(Guid.Empty, true, true, true, ["igdb"]));
        _acquisition.LockMissingAsync(1, Arg.Any<ArtworkRole>(), 0, Arg.Any<CancellationToken>()).Returns(true);
        _curation.GetStatesAsync(1, Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From<IReadOnlyList<ArtworkCurationState>>([
            new(ArtworkRole.Poster, ArtworkSelectionMode.Automatic, 0, null, null),
            new(ArtworkRole.Hero, ArtworkSelectionMode.Automatic, 0, null, null),
            new(ArtworkRole.Backdrop, ArtworkSelectionMode.Automatic, 0, null, null)]));
        _curation.GetGalleryAsync(1, Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From<IReadOnlyList<ArtworkAsset>>([]));
        _clients.CreateClient(AutomaticArtworkService.HttpClientName).Returns(_ => new HttpClient(_http, false));
        _processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ArtworkRole>(), Arg.Any<CancellationToken>())
            .Returns(call => new ProcessedArtworkImage("image/png", call.ArgAt<ArtworkRole>(1) == ArtworkRole.Poster ? 600 : 1920,
                call.ArgAt<ArtworkRole>(1) == ArtworkRole.Poster ? 900 : 620,
                [new("thumb", "image/png", 300, 300, [4, 5, 6])]));
        _storage.StoreAsync(Arg.Any<Stream>(), Arg.Any<IProgress<StoreProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(new StoreResult(StorageKey.FromHash(Sha256.FromSpan(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 }))), 3, 3, false, false));
    }

    [Fact]
    public async Task FillAsync_TrustedMatch_PublishesAutomaticArtworkForBothRoles()
    {
        await Fill(Result());
        await _curation.Received(2).StageLocalAssetAsync(1, Arg.Any<ArtworkRole>(), "igdb", Arg.Any<RetainedArtworkContent>(),
            Guid.Empty, Arg.Any<CancellationToken>(), "42", Arg.Any<string>());
        await _curation.DidNotReceiveWithAnyArgs().StagePinAsync(default, default, default, default, default);
        await _acquisition.Received(2).RecordAsync(1, Arg.Any<ArtworkRole>(), "Updated", "igdb", Arg.Any<CancellationToken>());
        _http.Urls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task FillAsync_LowConfidence_DoesNotDownload()
    {
        await Fill(Result() with { MatchConfidence = 0.2f });
        _http.Urls.ShouldBeEmpty();
        await _acquisition.Received(3).RecordAsync(1, Arg.Any<ArtworkRole>(), "NeedsMatch", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FillAsync_ConfirmedDifferentGame_DoesNotTrustResult()
    {
        _title.SetExternalIdManually("igdb", "other");
        await Fill(Result());
        _http.Urls.ShouldBeEmpty();
    }

    [Fact]
    public async Task FillAsync_SettingsDisabled_DoesNotDownload()
    {
        _acquisition.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new ArtworkEnrichmentSettingsDto(Guid.Empty, false, false, false, ["igdb"], FillBackdrops: false));
        await Fill(Result());
        _http.Urls.ShouldBeEmpty();
        await _acquisition.Received(3).RecordAsync(1, Arg.Any<ArtworkRole>(), "Disabled", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FillAsync_DisabledProvider_DoesNotDownload()
    {
        _provider.IsConfigured = false;
        await Fill(Result());
        _http.Urls.ShouldBeEmpty();
    }

    [Fact]
    public async Task FillAsync_ManualRequestPending_DoesNotDownload()
    {
        _curation.GetStatesAsync(1, Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From<IReadOnlyList<ArtworkCurationState>>([
            new(ArtworkRole.Poster, ArtworkSelectionMode.Automatic, 1, null, Guid.NewGuid()),
            new(ArtworkRole.Hero, ArtworkSelectionMode.Automatic, 1, null, Guid.NewGuid()),
            new(ArtworkRole.Backdrop, ArtworkSelectionMode.Automatic, 1, null, Guid.NewGuid())]));
        await Fill(Result());
        _http.Urls.ShouldBeEmpty();
    }

    [Fact]
    public async Task FillAsync_SelectionChangedDuringDownload_DoesNotStoreOrPin()
    {
        _acquisition.LockMissingAsync(1, Arg.Any<ArtworkRole>(), 0, Arg.Any<CancellationToken>()).Returns(false);
        await Fill(Result());
        _storage.ReceivedCalls().ShouldBeEmpty();
        await _acquisition.DidNotReceive().RecordAsync(1, Arg.Any<ArtworkRole>(), "Updated", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FillAsync_ProviderAssociationChangedDuringDownload_DoesNotPublishOldMatch()
    {
        _acquisition.GetProviderGameIdAsync(1, "igdb", Arg.Any<CancellationToken>()).Returns("42", "43", "43");
        await Fill(Result());
        _storage.ReceivedCalls().ShouldBeEmpty();
        await _acquisition.Received(2).RecordAsync(1, Arg.Any<ArtworkRole>(), "Superseded", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FillAsync_InvalidImage_RecordsFailureWithoutPublishing()
    {
        _processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ArtworkRole>(), Arg.Any<CancellationToken>())
            .Returns(ArtworkImageErrors.Invalid());
        await Fill(Result());
        _storage.ReceivedCalls().ShouldBeEmpty();
        await _acquisition.Received(2).RecordAsync(1, Arg.Any<ArtworkRole>(), "Failed", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FillAsync_NoSuitableHero_LeavesHeroEmpty()
    {
        await Fill(Result() with { Artwork = [new("portrait", ArtworkRole.Hero, Url("portrait"), 600, 900)] });
        _http.Urls.ShouldBeEmpty();
        await _acquisition.Received().RecordAsync(1, ArtworkRole.Hero, "Unavailable", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FillAsync_HeroCandidates_PrefersProportionsThenStableId()
    {
        await Fill(Result() with { Artwork = [new("square", ArtworkRole.Hero, Url("square"), 3000, 1800),
            new("b", ArtworkRole.Hero, Url("b"), 1920, 620), new("a", ArtworkRole.Hero, Url("a"), 1920, 620)] });
        _http.Urls.ShouldBe([Url("a")]);
    }

    [Fact]
    public async Task FillAsync_Cancelled_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => Service().FillAsync(_title, _platform,
            Context(cancellation.Token), cancellation.Token, [new("igdb", Result())]));
        _storage.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task FillAsync_UnexpectedImageHost_DoesNotMakeRequest()
    {
        await Fill(Result() with { Artwork = [new("a", ArtworkRole.Poster, "https://localhost/private.png", 600, 900)] });
        _http.Urls.ShouldBeEmpty();
    }

    [Fact]
    public async Task FillAsync_BackdropReviewDefault_ReportsCandidatesWithoutDownloading()
    {
        await Fill(Result() with { Artwork = [new("scene", ArtworkRole.Backdrop, Url("scene"), 1920, 1080)] });
        _http.Urls.ShouldBeEmpty();
        await _acquisition.Received().RecordAsync(1, ArtworkRole.Backdrop, "NeedsReview", null, Arg.Any<CancellationToken>());
        await _curation.DidNotReceiveWithAnyArgs().StagePinAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task FillAsync_BackdropAutomaticOptIn_PublishesMeasuredLandscapeWithoutPinning()
    {
        _acquisition.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(
            new ArtworkEnrichmentSettingsDto(Guid.Empty, true, true, true, ["igdb"], ReviewBackdrops: false));
        _processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), ArtworkRole.Backdrop, Arg.Any<CancellationToken>())
            .Returns(new ProcessedArtworkImage("image/jpeg", 1920, 1080, [new("backdrop", "image/webp", 1920, 1080, [4, 5, 6])]));
        await Fill(Result() with { Artwork = [new("banner", ArtworkRole.Backdrop, Url("banner"), 3840, 1240),
            new("scene", ArtworkRole.Backdrop, Url("scene"), 1920, 1080)] });
        _http.Urls.ShouldBe([Url("scene")]);
        await _acquisition.Received().RecordAsync(1, ArtworkRole.Backdrop, "Updated", "igdb", Arg.Any<CancellationToken>());
        await _curation.DidNotReceiveWithAnyArgs().StagePinAsync(default, default, default, default, default);
    }

    private AutomaticArtworkService Service() => new(_acquisition, _curation, _processor, _files, _storage, _locks,
        [_provider], _contexts, Options.Create(_options), _clients, NullLogger<AutomaticArtworkService>.Instance);
    private Task Fill(EnrichmentResult result) => Service().FillAsync(_title, _platform, Context(), CancellationToken.None, [new("igdb", result)]);
    private static JobContext Context(CancellationToken ct = default) => new(null, _ => Task.CompletedTask, ct);
    private static string Url(string id) => $"https://images.igdb.com/igdb/image/upload/t_original/{id}.jpg";
    private static EnrichmentResult Result() => EnrichmentResult.Found("42", 0.9f, new EnrichmentData()) with
    { Artwork = [new("poster", ArtworkRole.Poster, Url("poster"), 600, 900), new("hero", ArtworkRole.Hero, Url("hero"), 1920, 620)] };
    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }
    }
}
