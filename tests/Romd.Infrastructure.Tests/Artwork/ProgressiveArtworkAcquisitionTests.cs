using ErrorOr;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Storage.Files;
using Romd.Contracts.Management.Artwork;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Artwork;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class ProgressiveArtworkAcquisitionTests
{
    [Fact]
    public async Task FillAsync_BackdropFitOnLaterPage_BeatsCropFallback()
    {
        var rig = new Rig(role: ArtworkRole.Backdrop);
        rig.Page(0, [Rig.Candidate("crop", 1600, 1200) with { Role = ArtworkRole.Backdrop }], 1);
        rig.Page(1, [Rig.Candidate("fit", 1920, 1080) with { Role = ArtworkRole.Backdrop }]);
        await rig.Fill();
        rig.Downloads.ShouldBe(["fit"]);
    }

    [Fact]
    public async Task FillAsync_BackdropFitFails_UsesBoundedCropFallback()
    {
        var rig = new Rig(role: ArtworkRole.Backdrop);
        rig.Page(0, [Rig.Candidate("crop", 1600, 1200) with { Role = ArtworkRole.Backdrop },
            Rig.Candidate("fit", 1920, 1080) with { Role = ArtworkRole.Backdrop }]);
        rig.Invalid.Add("fit");
        await rig.Fill();
        rig.Downloads.ShouldBe(["fit", "crop"]);
        await rig.Curation.Received(1).StageLocalAssetAsync(1, ArtworkRole.Backdrop, "igdb",
            Arg.Any<RetainedArtworkContent>(), Arg.Any<CancellationToken>(), "42", "crop");
    }

    [Fact]
    public async Task FillAsync_BackdropExcessiveCrop_RemainsUnavailableWithoutPinningEmpty()
    {
        var rig = new Rig(role: ArtworkRole.Backdrop);
        rig.Page(0, [Rig.Candidate("square", 1600, 1600) with { Role = ArtworkRole.Backdrop }]);
        await rig.Fill();
        rig.Downloads.ShouldBeEmpty();
        await rig.Acquisition.Received().RecordAsync(1, ArtworkRole.Backdrop, "Unavailable", "igdb", Arg.Any<CancellationToken>(), Arg.Any<Guid?>());
        await rig.Curation.DidNotReceiveWithAnyArgs().StageLocalAssetAsync(default, default, default!, default!, default, default);
    }

    [Fact]
    public async Task FillAsync_ExplicitEmptyBackdrop_DoesNotSearch()
    {
        var rig = new Rig(role: ArtworkRole.Backdrop);
        rig.Curation.GetStatesAsync(1, Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From<IReadOnlyList<ArtworkCurationState>>(
            Enum.GetValues<ArtworkRole>().Select(role => new ArtworkCurationState(role, ArtworkSelectionMode.Empty, 0, null, null)).ToArray()));
        await rig.Fill();
        await rig.Browser.DidNotReceiveWithAnyArgs().GetCandidatesAsync(default!, default!, default, default);
        await rig.Acquisition.Received().RecordAsync(1, ArtworkRole.Backdrop, "LeftEmpty", null, Arg.Any<CancellationToken>(), Arg.Any<Guid?>());
    }

    [Fact]
    public async Task FillAsync_UsableFirstPage_PreservesProviderOrderAndDoesNotFetchNextPage()
    {
        var rig = new Rig();
        // First provider-ranked image has a less exact aspect ratio and higher ID.
        rig.Page(0, [Rig.Candidate("99", 500, 700), Rig.Candidate("1", 600, 900)], 1);
        await rig.Fill();
        rig.Downloads.ShouldBe(["99"]);
        await rig.Browser.DidNotReceive().GetCandidatesAsync("42", Arg.Any<ProviderArtworkQuery>(), 1, Arg.Any<CancellationToken>());
        await rig.AssertPublished("99");
    }

    [Fact]
    public async Task FillAsync_FirstThreeAssetsInvalid_PublishesFourthWithoutPersistingRejection()
    {
        var rig = new Rig();
        rig.Page(0, Enumerable.Range(1, 4).Select(x => Rig.Candidate(x.ToString())).ToArray());
        rig.Invalid.UnionWith(["1", "2", "3"]);
        await rig.Fill();
        rig.Downloads.ShouldBe(["1", "2", "3", "4"]);
        await rig.AssertPublished("4");
    }

    [Fact]
    public async Task FillAsync_FirstPageUnusable_ContinuesToNextPage()
    {
        var rig = new Rig();
        rig.Page(0, [Rig.Candidate("1", 10, 10)], 1);
        rig.Page(1, [Rig.Candidate("2")]);
        await rig.Fill();
        rig.Downloads.ShouldBe(["2"]);
        await rig.AssertPublished("2");
    }

    [Fact]
    public async Task FillAsync_AllAssetsInvalid_BoundsAttemptsAndExpiresTemporaryFailures()
    {
        var rig = new Rig();
        rig.Page(0, Enumerable.Range(1, 12).Select(x => Rig.Candidate(x.ToString())).ToArray());
        rig.Invalid.UnionWith(Enumerable.Range(1, 12).Select(x => x.ToString()));
        await rig.Fill();
        rig.Downloads.Count.ShouldBe(8);
        rig.Downloads.Clear();
        await rig.Fill();
        rig.Downloads.ShouldBe(["9", "10", "11", "12"]);
        rig.Downloads.Clear();
        rig.Clock.Now += TimeSpan.FromMinutes(6);
        rig.Invalid.Clear();
        await rig.Fill();
        rig.Downloads.ShouldBe(["1"]);
        await rig.AssertPublished("1");
    }

    [Fact]
    public async Task FillAsync_RateLimited_RequestsDeferralWithoutFailedOutcomeOrShortRetries()
    {
        var rig = new Rig();
        var due = rig.Clock.Now.AddMinutes(2);
        rig.Browser.GetCandidatesAsync("42", Arg.Any<ProviderArtworkQuery>(), 0, Arg.Any<CancellationToken>())
            .Returns(ArtworkProviderErrors.RateLimited(due));
        var context = await rig.Fill();
        context.DeferredUntil.ShouldBe(due);
        await rig.Browser.Received(1).GetCandidatesAsync("42", Arg.Any<ProviderArtworkQuery>(), 0, Arg.Any<CancellationToken>());
        await rig.Acquisition.DidNotReceive().RecordAsync(1, ArtworkRole.Poster, "Failed", Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>());
        rig.Downloads.ShouldBeEmpty();
    }

    [Fact]
    public async Task FillAsync_PreferredProviderThrottled_FallbackCanFillWithoutDeferral()
    {
        var rig = new Rig(withFallback: true);
        rig.Browser.GetCandidatesAsync("42", Arg.Any<ProviderArtworkQuery>(), 0, Arg.Any<CancellationToken>())
            .Returns(ArtworkProviderErrors.RateLimited(rig.Clock.Now.AddMinutes(2)));
        // A fallback with an empty first page must still be allowed to advance.
        rig.Fallback.GetCandidatesAsync("42", Arg.Any<ProviderArtworkQuery>(), 0, Arg.Any<CancellationToken>())
            .Returns(new ProviderArtworkPage([], 1));
        rig.Fallback.GetCandidatesAsync("42", Arg.Any<ProviderArtworkQuery>(), 1, Arg.Any<CancellationToken>())
            .Returns(new ProviderArtworkPage([Rig.Candidate("7")], null));
        var context = await rig.Fill();
        context.DeferredUntil.ShouldBeNull();
        rig.Downloads.ShouldBe(["7"]);
        await rig.Curation.Received(1).StageLocalAssetAsync(1, ArtworkRole.Poster, "igdb",
            Arg.Any<RetainedArtworkContent>(), Arg.Any<CancellationToken>(), "42", "7");
    }

    [Fact]
    public async Task FillAsync_IdentityDiscoveryThrottled_DefersOnlyUnresolvedRole()
    {
        var rig = new Rig();
        var due = rig.Clock.Now.AddMinutes(1);
        rig.Matches.DiscoverAsync(1, Arg.Any<JobContext>(), Arg.Any<CancellationToken>())
            .Returns(new ProviderIdentityDiscoveryResult(new Dictionary<string, DateTimeOffset> { ["steamgriddb"] = due }));
        var context = await rig.Fill();
        context.DeferredUntil.ShouldBe(due);
        await rig.Browser.DidNotReceiveWithAnyArgs().GetCandidatesAsync(default!, default!, default, default);
        await rig.Acquisition.DidNotReceiveWithAnyArgs().RecordAsync(default, default, default!, default, default, default);
    }

    [Fact]
    public async Task FillAsync_IdentityDiscoveryThrottled_TrustedFallbackStillPublishes()
    {
        var rig = new Rig(withFallback: true);
        rig.Matches.DiscoverAsync(1, Arg.Any<JobContext>(), Arg.Any<CancellationToken>())
            .Returns(new ProviderIdentityDiscoveryResult(new Dictionary<string, DateTimeOffset> { ["steamgriddb"] = rig.Clock.Now.AddMinutes(1) }));
        rig.Fallback.GetCandidatesAsync("42", Arg.Any<ProviderArtworkQuery>(), 0, Arg.Any<CancellationToken>())
            .Returns(new ProviderArtworkPage([Rig.Candidate("7")], null));
        var context = await rig.Fill();
        context.DeferredUntil.ShouldBeNull();
        rig.Downloads.ShouldBe(["7"]);
        await rig.Curation.Received(1).StageLocalAssetAsync(1, ArtworkRole.Poster, "igdb",
            Arg.Any<RetainedArtworkContent>(), Arg.Any<CancellationToken>(), "42", "7");
    }

    [Fact]
    public async Task FillAsync_PreferredProviderExhaustsBudget_StillAttemptsFallback()
    {
        var rig = new Rig(withFallback: true);
        rig.Page(0, Enumerable.Range(1, 9).Select(x => Rig.Candidate(x.ToString())).ToArray());
        rig.Invalid.UnionWith(Enumerable.Range(1, 9).Select(x => x.ToString()));
        rig.Fallback.GetCandidatesAsync("42", Arg.Any<ProviderArtworkQuery>(), 0, Arg.Any<CancellationToken>())
            .Returns(new ProviderArtworkPage([Rig.Candidate("99")], null));
        await rig.Fill();
        rig.Downloads.ShouldBe(["1", "2", "3", "4", "5", "6", "7", "8", "99"]);
        await rig.Curation.Received(1).StageLocalAssetAsync(1, ArtworkRole.Poster, "igdb",
            Arg.Any<RetainedArtworkContent>(), Arg.Any<CancellationToken>(), "42", "99");
    }

    [Fact]
    public async Task FillAsync_LinkChangesBeforePublication_DoesNotStoreOrPublish()
    {
        var rig = new Rig();
        rig.Page(0, [Rig.Candidate("1")]);
        rig.Matches.GetLinkRevisionAsync(1, "steamgriddb", "42", Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        await rig.Fill();
        rig.Files.ShouldNotHavePublished();
        await rig.Curation.DidNotReceiveWithAnyArgs().StageLocalAssetAsync(default, default, default!, default!, default, default, default);
        await rig.Acquisition.Received(1).RecordAsync(1, ArtworkRole.Poster, "Superseded", "steamgriddb", Arg.Any<CancellationToken>(), Arg.Any<Guid?>());
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Rig
    {
        public readonly Clock Clock = new();
        public readonly IArtworkAcquisitionStore Acquisition = Substitute.For<IArtworkAcquisitionStore>();
        public readonly IArtworkCurationRepository Curation = Substitute.For<IArtworkCurationRepository>();
        public readonly ITitleProviderMatchService Matches = Substitute.For<ITitleProviderMatchService>();
        public readonly IArtworkProviderBrowser Browser = Substitute.For<IArtworkProviderBrowser>();
        public readonly IArtworkProviderBrowser Fallback = Substitute.For<IArtworkProviderBrowser>();
        public readonly IFileStorageService Files = Substitute.For<IFileStorageService>().PublishesEveryFile();
        public readonly List<string> Downloads = [];
        public readonly HashSet<string> Invalid = [];
        private readonly LinkedProviderArtworkService _service;

        public Rig(bool withFallback = false, ArtworkRole role = ArtworkRole.Poster)
        {
            Acquisition.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new ArtworkEnrichmentSettings(Guid.Empty, role == ArtworkRole.Poster, false, false, [], role == ArtworkRole.Backdrop, false));
            Acquisition.LockMissingAsync(1, role, 0, Arg.Any<CancellationToken>()).Returns(true);
            Curation.GetStatesAsync(1, Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From<IReadOnlyList<ArtworkCurationState>>(
                Enum.GetValues<ArtworkRole>().Select(role => new ArtworkCurationState(role, ArtworkSelectionMode.Automatic, 0, null, null)).ToArray()));
            Curation.GetGalleryAsync(1, Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From<IReadOnlyList<ArtworkAsset>>(value: []));
            var revision = Guid.NewGuid();
            Matches.GetAsync(1, Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From<IReadOnlyList<TitleProviderMatch>>(
                (withFallback ? new[] { "steamgriddb", "igdb" } : [role == ArtworkRole.Backdrop ? "igdb" : "steamgriddb"]).Select(id =>
                    new TitleProviderMatch(id, id, true, ["artwork"], revision, "Confirmed", "42", null, null, null, false)).ToArray()));
            Matches.GetLinkRevisionAsync(1, Arg.Any<string>(), "42", Arg.Any<CancellationToken>()).Returns(revision);
            Browser.Capabilities.Returns(new ArtworkProviderCapabilities(role == ArtworkRole.Backdrop ? "igdb" : "steamgriddb", "SteamGridDB", [new(role, [], [])]));
            Fallback.Capabilities.Returns(new ArtworkProviderCapabilities("igdb", "IGDB", [new(role, [], [])]));
            var source = Substitute.For<IArtworkAssetSource>();
            source.DownloadAsync(Arg.Any<string>(), "42", Arg.Any<string>(), role, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var id = call.ArgAt<string>(2);
                    Downloads.Add(id);
                    return Task.FromResult<ErrorOr<DownloadedArtworkAsset>>(Invalid.Contains(id)
                        ? ArtworkProviderErrors.InvalidResponse()
                        : new DownloadedArtworkAsset([1, 2, 3], "image/png", null, null));
                });
            var processor = Substitute.For<IArtworkImageProcessor>();
            processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), role, Arg.Any<CancellationToken>())
                .Returns(new ProcessedArtworkImage("image/png", role == ArtworkRole.Backdrop ? 1600 : 600, role == ArtworkRole.Backdrop ? 1200 : 900, [new("card", "image/png", 600, 900, [4])]));
            _service = new LinkedProviderArtworkService(withFallback ? [Browser, Fallback] : [Browser], source, Matches, Acquisition, Curation, processor,
                Files, Substitute.For<IUnitOfWork>(), Options.Create(new EnrichmentOptions()),
                failureCache: new ArtworkCandidateFailureCache(Clock), timeProvider: Clock);
        }

        public static ProviderArtworkCandidate Candidate(string id, int width = 600, int height = 900) => new(
            "42", id, ArtworkRole.Poster, width, height, "alternate", new($"https://cdn2.steamgriddb.com/thumb/{id}.png"),
            new($"https://cdn2.steamgriddb.com/grid/{id}.png"), null, $"https://www.steamgriddb.com/grid/{id}");

        public void Page(int page, IReadOnlyList<ProviderArtworkCandidate> candidates, int? next = null) =>
            Browser.GetCandidatesAsync("42", Arg.Any<ProviderArtworkQuery>(), page, Arg.Any<CancellationToken>())
                .Returns(new ProviderArtworkPage(candidates, next));

        public async Task<JobContext> Fill()
        {
            var context = new JobContext(null, _ => Task.CompletedTask, default);
            var title = Title.Rehydrate(1, 10, "Game", "game", null, null, null, null,
                null, null, null, EnrichmentStatus.Completed, null, Clock.Now);
            await _service.FillAsync(title, Platform.Rehydrate(10, "Super Nintendo", "snes", "Nintendo", Clock.Now), context, default);
            return context;
        }

        public Task AssertPublished(string id) => Curation.Received(1).StageLocalAssetAsync(1, ArtworkRole.Poster,
            "steamgriddb", Arg.Any<RetainedArtworkContent>(), Arg.Any<CancellationToken>(), "42", id);
    }
}
