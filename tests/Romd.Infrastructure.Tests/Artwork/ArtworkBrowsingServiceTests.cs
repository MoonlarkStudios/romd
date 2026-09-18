using ErrorOr;
using Microsoft.AspNetCore.DataProtection;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Artwork;
using Shouldly;
using SkiaSharp;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class ArtworkBrowsingServiceTests
{
    private static readonly Guid User = Guid.Parse("cf013187-0c06-4693-8b57-48631933b85d");

    [Theory]
    [InlineData(ArtworkRole.Poster, 300, 150)]
    [InlineData(ArtworkRole.Hero, 620, 310)]
    [InlineData(ArtworkRole.Logo, 480, 240)]
    [InlineData(ArtworkRole.Backdrop, 480, 240)]
    public async Task PreviewAsync_RealProcessor_ReturnsSmallPreviewForEveryRole(ArtworkRole role, int width, int height)
    {
        using var bitmap = new SKBitmap(1600, 800);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var source = new Source { PreviewBytes = encoded.ToArray() };
        var time = new ManualTime();
        var service = new ArtworkBrowsingService([new Browser()], source, new ArtworkImageProcessor(),
            new EphemeralDataProtectionProvider(), new ArtworkPreviewCache(time), time);
        var candidates = await service.BrowseAsync(1, User, "steamgriddb", "42", new(role));

        var result = await service.PreviewAsync(1, User, candidates.Value.Items[0].Reference);

        result.IsError.ShouldBeFalse();
        using var preview = SKBitmap.Decode(result.Value.Bytes);
        preview.Width.ShouldBe(width);
        preview.Height.ShouldBe(height);
        source.Downloads.ShouldBe(0);
    }

    [Fact]
    public async Task BrowseAsync_MultipleProviders_DispatchesByIdentity()
    {
        var first = new Browser();
        var second = new Browser("igdb");
        var time = new ManualTime();
        var service = new ArtworkBrowsingService([first, second], new Source(), new Processor(),
            new EphemeralDataProtectionProvider(), new ArtworkPreviewCache(time), time);
        (await service.GetCapabilitiesAsync()).Count.ShouldBe(2);
        var result = await service.BrowseAsync(1, User, "igdb", "42", new(ArtworkRole.Poster));
        result.IsError.ShouldBeFalse();
        second.BrowseCalls.ShouldBe(1);
        first.BrowseCalls.ShouldBe(0);
        (await service.ValidateCandidateAsync(1, User, result.Value.Items[0].Reference)).Value.ProviderId.ShouldBe("igdb");
        (await service.BrowseAsync(1, User, "unknown", "42", new(ArtworkRole.Poster))).IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task BrowseAsync_References_RoundTripTrustedIdentityWithoutDownloading()
    {
        var fixture = new Fixture();
        var page = await fixture.BrowseAsync();

        var result = await fixture.Service.ValidateCandidateAsync(1, User, page.Items[0].Reference);

        result.IsError.ShouldBeFalse();
        result.Value.TrustedAssetUrl.ShouldBe("https://cdn2.steamgriddb.com/grid/a.png");
        result.Value.Attribution.ShouldBe("Artist");
        page.Items[0].Reference.ShouldNotContain("cdn2");
        fixture.Source.Downloads.ShouldBe(0);
        fixture.Source.Previews.ShouldBe(0);
    }

    [Fact]
    public async Task ValidateCandidateAsync_TamperedOrCrossScope_Rejects()
    {
        var fixture = new Fixture();
        var page = await fixture.BrowseAsync();
        var reference = page.Items[0].Reference;
        var tampered = reference[..20] + (reference[20] == 'a' ? 'b' : 'a') + reference[21..];

        (await fixture.Service.ValidateCandidateAsync(1, User, tampered)).IsError.ShouldBeTrue();
        (await fixture.Service.ValidateCandidateAsync(2, User, reference)).IsError.ShouldBeTrue();
        (await fixture.Service.ValidateCandidateAsync(1, Guid.NewGuid(), reference)).IsError.ShouldBeTrue();
        (await fixture.Service.ValidateCandidateAsync(1, User, page.NextCursor!)).IsError.ShouldBeTrue();
        fixture.Source.Previews.ShouldBe(0);
    }

    [Fact]
    public async Task ValidateCandidateAsync_ExpiredOrDisabled_Rejects()
    {
        var fixture = new Fixture();
        var page = await fixture.BrowseAsync();
        fixture.Browser.Available = false;
        (await fixture.Service.ValidateCandidateAsync(1, User, page.Items[0].Reference)).FirstError.Code
            .ShouldBe("Artwork.ProviderNotConfigured");
        fixture.Browser.Available = true;
        fixture.Time.Advance(TimeSpan.FromMinutes(10));

        (await fixture.Service.ValidateCandidateAsync(1, User, page.Items[0].Reference)).FirstError.Code
            .ShouldBe("Artwork.ProviderInvalidRequest");
    }

    [Fact]
    public async Task BrowseAsync_Cursor_BindsTitleUserGameAndFilters()
    {
        var fixture = new Fixture();
        var page = await fixture.BrowseAsync();
        var query = new ProviderArtworkQuery(ArtworkRole.Poster);

        var next = await fixture.Service.BrowseAsync(1, User, "steamgriddb", "42", query, page.NextCursor);

        next.IsError.ShouldBeFalse();
        fixture.Browser.LastPage.ShouldBe(1);
        var count = fixture.Browser.BrowseCalls;
        (await fixture.Service.BrowseAsync(2, User, "steamgriddb", "42", query, page.NextCursor)).IsError.ShouldBeTrue();
        (await fixture.Service.BrowseAsync(1, Guid.NewGuid(), "steamgriddb", "42", query, page.NextCursor)).IsError.ShouldBeTrue();
        (await fixture.Service.BrowseAsync(1, User, "steamgriddb", "43", query, page.NextCursor)).IsError.ShouldBeTrue();
        (await fixture.Service.BrowseAsync(1, User, "steamgriddb", "42", query with { Style = "alternate" }, page.NextCursor)).IsError.ShouldBeTrue();
        fixture.Time.Advance(TimeSpan.FromMinutes(10));
        (await fixture.Service.BrowseAsync(1, User, "steamgriddb", "42", query, page.NextCursor)).IsError.ShouldBeTrue();
        fixture.Browser.BrowseCalls.ShouldBe(count);
    }

    [Fact]
    public async Task PreviewAsync_RepeatedThenExpiredCache_FetchesOnlyThumbnailAndNeverOriginal()
    {
        var fixture = new Fixture();
        var page = await fixture.BrowseAsync();
        var reference = page.Items[0].Reference;

        var first = await fixture.Service.PreviewAsync(1, User, reference);
        var second = await fixture.Service.PreviewAsync(1, User, reference);

        first.Value.Bytes.ShouldBe([9, 8, 7]);
        second.Value.Bytes.ShouldBe(first.Value.Bytes);
        fixture.Source.Previews.ShouldBe(1);
        fixture.Source.LastPreviewUrl.ShouldBe("https://cdn2.steamgriddb.com/thumb/a.jpg");
        fixture.Source.Downloads.ShouldBe(0);
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        (await fixture.Service.PreviewAsync(1, User, reference)).IsError.ShouldBeFalse();
        fixture.Source.Previews.ShouldBe(2);
    }

    [Fact]
    public async Task PreviewAsync_CacheExceeds64MiB_EvictsOldestEntry()
    {
        var fixture = new Fixture();
        fixture.Processor.OutputBytes = new byte[2 * 1024 * 1024];
        string? firstReference = null;
        for (var index = 0; index < 33; index++)
        {
            fixture.Browser.AssetId = (index + 1).ToString();
            var page = await fixture.BrowseAsync();
            firstReference ??= page.Items[0].Reference;
            (await fixture.Service.PreviewAsync(1, User, page.Items[0].Reference)).IsError.ShouldBeFalse();
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
        }
        fixture.Source.Previews.ShouldBe(33);

        (await fixture.Service.PreviewAsync(1, User, firstReference!)).IsError.ShouldBeFalse();

        fixture.Source.Previews.ShouldBe(34);
        fixture.Source.Downloads.ShouldBe(0);
    }

    private sealed class Fixture
    {
        public Browser Browser { get; } = new();
        public Source Source { get; } = new();
        public ManualTime Time { get; } = new();
        public Processor Processor { get; } = new();
        public ArtworkBrowsingService Service { get; }
        public Fixture() => Service = new ArtworkBrowsingService([Browser], Source, Processor,
            new EphemeralDataProtectionProvider(), new ArtworkPreviewCache(Time), Time);
        public async Task<ArtworkCandidatePage> BrowseAsync() =>
            (await Service.BrowseAsync(1, User, "steamgriddb", "42", new ProviderArtworkQuery(ArtworkRole.Poster))).Value;
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class Browser(string id = "steamgriddb") : IArtworkProviderBrowser
    {
        public string AssetId { get; set; } = "7";
        public bool Available { get; set; } = true;
        public int LastPage { get; private set; }
        public int BrowseCalls { get; private set; }
        public ArtworkProviderCapabilities Capabilities { get; } = new(id, id, []);
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);
        public Task<ErrorOr<IReadOnlyList<ProviderArtworkGame>>> SearchAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(ErrorOrFactory.From<IReadOnlyList<ProviderArtworkGame>>([]));
        public Task<ErrorOr<ProviderArtworkPage>> GetCandidatesAsync(string gameId, ProviderArtworkQuery query,
            int page = 0, CancellationToken ct = default)
        {
            LastPage = page;
            BrowseCalls++;
            return Task.FromResult(ErrorOrFactory.From(new ProviderArtworkPage(
                [new ProviderArtworkCandidate(gameId, AssetId, query.Role, 600, 900, "alternate",
                    new Uri("https://cdn2.steamgriddb.com/thumb/a.jpg"), new Uri("https://cdn2.steamgriddb.com/grid/a.png"),
                    "Artist", "https://www.steamgriddb.com/grid/7")], 1)));
        }
    }
    private sealed class Source : IArtworkAssetSource
    {
        public byte[] PreviewBytes { get; init; } = [1, 2, 3];
        public int Downloads { get; private set; }
        public int Previews { get; private set; }
        public string? LastPreviewUrl { get; private set; }
        public Task<ErrorOr<DownloadedArtworkAsset>> DownloadAsync(string providerId, string gameId, string assetId,
            ArtworkRole role, string trustedAssetUrl, CancellationToken ct = default)
        {
            Downloads++;
            throw new InvalidOperationException("Preview must never fetch the original.");
        }
        public Task<ErrorOr<DownloadedArtworkAsset>> DownloadPreviewAsync(string providerId, ArtworkRole role,
            string trustedPreviewUrl, CancellationToken ct = default)
        {
            Previews++;
            LastPreviewUrl = trustedPreviewUrl;
            return Task.FromResult(ErrorOrFactory.From(new DownloadedArtworkAsset(PreviewBytes, "image/jpeg", null, null)));
        }
    }
    private sealed class Processor : IArtworkImageProcessor
    {
        public byte[] OutputBytes { get; set; } = [9, 8, 7];
        public Task<ErrorOr<ProcessedArtworkImage>> ProcessAsync(ReadOnlyMemory<byte> original, ArtworkRole role,
            CancellationToken ct = default) => Task.FromResult(ErrorOrFactory.From(new ProcessedArtworkImage("image/jpeg", 3, 3,
                [new ProcessedArtworkVariant("thumb", "image/png", 3, 3, OutputBytes)])));
    }
}
