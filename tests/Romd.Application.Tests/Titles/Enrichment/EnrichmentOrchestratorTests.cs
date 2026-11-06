using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Tests.Titles.Enrichment.Helpers;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Source.Platform;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles.Enrichment;

public sealed class EnrichmentOrchestratorTests
{
    private readonly IEnrichmentContextFactory _contexts = Substitute.For<IEnrichmentContextFactory>();
    private readonly IProviderIdentityService _identities = Substitute.For<IProviderIdentityService>();
    private readonly ITitleProviderMatchService _matches = Substitute.For<ITitleProviderMatchService>();
    private readonly IArtworkCurationRepository _curation = Substitute.For<IArtworkCurationRepository>();
    private readonly IPlatformFieldDefaultRepository _platformDefaults = Substitute.For<IPlatformFieldDefaultRepository>();
    private readonly FakeMediaStager _stager = new();
    private int _rematerializations;

    private readonly EnrichmentOptions _options = new()
    {
        GlobalSourcePriority = ["providerA", "providerB"],
        DownloadMedia = false
    };

    public EnrichmentOrchestratorTests()
    {
        _contexts.CreateAsync(Arg.Any<Title>(), Arg.Any<Platform>(), Arg.Any<CancellationToken>())
            .Returns(call => new EnrichmentContext
            {
                TitleName = call.Arg<Title>().Name,
                PlatformShortName = call.Arg<Platform>().ShortName
            });
        Trust("providerA", "extA");
        Trust("providerB", "extB");
        _platformDefaults.GetByPlatformIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());
        _curation.GetExcludedUrlsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("extA", "extA", true)]
    [InlineData("extA", null, false)]
    [InlineData("other", "other", false)]
    public async Task EnrichTitleAsync_SharedIdentity_GatesFetchAndRechecksBeforeApplying(
        string? trustedAtFetch, string? trustedAtApply, bool enriches)
    {
        var title = CreateTitle();
        _identities.GetTrustedAsync(title.Id, "providerA", Arg.Any<CancellationToken>()).Returns(trustedAtFetch, trustedAtApply);
        var provider = Found("providerA", "extA", new EnrichmentData { Description = "Approved metadata" }, .2f);

        var result = await CreateOrchestrator(provider).EnrichTitleAsync(title, CreatePlatform());

        result.Enriched.ShouldBe(enriches);
        provider.ReceivedContexts.Count.ShouldBe(trustedAtFetch is null ? 0 : 1);
        if (trustedAtFetch is not null)
            provider.ReceivedContexts.Single().ExistingExternalId.ShouldBe(trustedAtFetch);
        // The identity service owns the association write; metadata must not create a duplicate child.
        title.ExternalIds.ShouldBeEmpty();
        if (enriches)
            title.MetadataLayers.Single(x => x.SourceId == "providerA").GetPayload()!.ProviderGameId.ShouldBe("extA");
        else
            title.MetadataLayers.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnrichTitleAsync_UntrustedProvider_IsNotFetchedAndCountsAsAnswered()
    {
        var title = CreateTitle();
        _identities.GetTrustedAsync(title.Id, "providerA", Arg.Any<CancellationToken>()).Returns((string?)null);
        var providerA = Found("providerA", "extA", new EnrichmentData { Description = "A" });
        var providerB = new FakeMetadataProvider("providerB", _ => EnrichmentResult.Error("down"));

        var result = await CreateOrchestrator(providerA, providerB).EnrichTitleAsync(title, CreatePlatform());

        providerA.ReceivedContexts.ShouldBeEmpty();
        ShouldBeOutcome(result, OrchestrationResult.NotFound);
    }

    [Fact]
    public async Task EnrichTitleAsync_SuppressedProvider_IsSkippedWithoutResolvingIdentity()
    {
        var title = CreateTitle();
        _matches.IsSuppressedAsync(title.Id, "providerA", Arg.Any<CancellationToken>()).Returns(true);
        var providerA = Found("providerA", "extA", new EnrichmentData { Description = "A" });
        var providerB = Found("providerB", "extB", new EnrichmentData { Developer = "B" });

        var result = await CreateOrchestrator(providerA, providerB).EnrichTitleAsync(title, CreatePlatform());

        providerA.ReceivedContexts.ShouldBeEmpty();
        await _identities.DidNotReceive().GetTrustedAsync(title.Id, "providerA", Arg.Any<CancellationToken>());
        result.Enriched.ShouldBeTrue();
        title.Description.ShouldBeNull();
        title.Developer.ShouldBe("B");
    }

    [Fact]
    public async Task EnrichTitleAsync_AllProvidersRun_NoShortCircuit()
    {
        var title = CreateTitle();
        var providerA = Found("providerA", "extA",
            new EnrichmentData { Description = "Provider A description", Publisher = "Publisher A" }, .9f);
        var providerB = Found("providerB", "extB", new EnrichmentData { Developer = "Developer B", Genre = "Action" }, .8f);

        var result = await CreateOrchestrator(providerA, providerB).EnrichTitleAsync(title, CreatePlatform());

        providerA.ReceivedContexts.Count.ShouldBe(1);
        providerB.ReceivedContexts.Count.ShouldBe(1);
        result.Enriched.ShouldBeTrue();
        // Trusted identity acceptance authorizes the result regardless of the provider's own score.
        result.BestConfidence.ShouldBe(1f);
        title.Name.ShouldBe("Super Mario World");
        title.Description.ShouldBe("Provider A description");
        title.Developer.ShouldBe("Developer B");
        title.FieldProvenance["Publisher"].ShouldBe("providerA");
        title.FieldProvenance["Genre"].ShouldBe("providerB");
        result.ArtworkResults.Select(x => x.ProviderId).ShouldBe(["providerA", "providerB"]);
        await _identities.Received(1).DiscoverAsync(title.Id, null, Arg.Any<CancellationToken>());
        await _identities.Received(2).GetTrustedAsync(title.Id, "providerA", Arg.Any<CancellationToken>());
        await _identities.Received(2).GetTrustedAsync(title.Id, "providerB", Arg.Any<CancellationToken>());
        await _identities.DidNotReceive().ResolveTrustedAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichTitleAsync_BothProvidersSupplyAField_GlobalPriorityWins()
    {
        var title = CreateTitle();
        var providerA = Found("providerA", "extA", new EnrichmentData { Description = "A" });
        var providerB = Found("providerB", "extB", new EnrichmentData { Description = "B" });

        await CreateOrchestrator(providerB, providerA).EnrichTitleAsync(title, CreatePlatform());

        title.Description.ShouldBe("A");
        title.FieldProvenance["Description"].ShouldBe("providerA");
    }

    [Fact]
    public async Task EnrichTitleAsync_UserValuesAndFieldOverrides_SurviveProviderResults()
    {
        var title = CreateTitle();
        title.StoreUserLayer(new TitleMetadataPayload { Description = "Curated description" }, updatedAt: DateTimeOffset.UnixEpoch);
        title.SetFieldSourceOverride("Developer", "providerB");
        var providerA = Found("providerA", "extA", new EnrichmentData { Description = "A", Developer = "A dev" });
        var providerB = Found("providerB", "extB", new EnrichmentData { Developer = "B dev" });

        await CreateOrchestrator(providerA, providerB).EnrichTitleAsync(title, CreatePlatform());

        title.Description.ShouldBe("Curated description");
        title.Developer.ShouldBe("B dev");
        title.Name.ShouldBe("Super Mario World");
    }

    [Fact]
    public async Task EnrichTitleAsync_ProviderContentRatings_MaterializeThroughTheCascade()
    {
        var title = CreateTitle();
        var claim = new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "T" };
        var provider = Found("providerA", "extA", new EnrichmentData { ContentRatings = [claim] });

        var result = await CreateOrchestrator(provider).EnrichTitleAsync(title, CreatePlatform());

        result.Enriched.ShouldBeTrue();
        title.ContentRatings.ShouldHaveSingleItem().Board.ShouldBe(RatingBoard.Esrb);
    }

    [Fact]
    public async Task EnrichTitleAsync_OneProviderNotFound_OtherStillRuns()
    {
        var title = CreateTitle();
        var providerA = new FakeMetadataProvider("providerA", _ => EnrichmentResult.NotFound());
        var providerB = Found("providerB", "extB", new EnrichmentData { Description = "B" });

        var result = await CreateOrchestrator(providerA, providerB).EnrichTitleAsync(title, CreatePlatform());

        result.Enriched.ShouldBeTrue();
        title.FieldProvenance["Description"].ShouldBe("providerB");
    }

    [Fact]
    public async Task EnrichTitleAsync_OneProviderThrows_OtherStillRuns()
    {
        var title = CreateTitle();
        var providerA = new FakeMetadataProvider("providerA", _ => throw new InvalidOperationException("API failure"));
        var providerB = Found("providerB", "extB", new EnrichmentData { Publisher = "Publisher B" });

        var result = await CreateOrchestrator(providerA, providerB).EnrichTitleAsync(title, CreatePlatform());

        result.Enriched.ShouldBeTrue();
        title.Publisher.ShouldBe("Publisher B");
    }

    [Fact]
    public async Task EnrichTitleAsync_NoProvidersConfigured_ThrowsBeforeBuildingContext()
    {
        var title = CreateTitle();
        var unconfigured = new FakeMetadataProvider("providerA", _ => EnrichmentResult.NotFound()) { IsConfigured = false };

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => CreateOrchestrator(unconfigured).EnrichTitleAsync(title, CreatePlatform()));

        error.Message.ShouldContain("Metadata Providers");
        unconfigured.InitializeCount.ShouldBe(1);
        title.EnrichmentStatus.ShouldBe(EnrichmentStatus.None);
        await _contexts.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default);
    }

    [Fact]
    public async Task EnrichStreamAsync_NoProvidersConfigured_ThrowsBeforeBuildingContext()
    {
        var title = CreateTitle();

        var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in CreateOrchestrator().EnrichStreamAsync(Stream(title)))
            { }
        });

        error.Message.ShouldContain("Metadata Providers");
        await _contexts.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default);
    }

    [Fact]
    public async Task EnrichStreamAsync_NoProvidersAndNoTitles_CompletesEmpty()
    {
        var results = await Collect(CreateOrchestrator().EnrichStreamAsync(Stream()));

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnrichTitleAsync_DisabledProvider_IsInitializedButNotCalled()
    {
        _options.Providers["providerB"] = new ProviderSettings { Enabled = false };
        var providerA = Found("providerA", "extA", new EnrichmentData { Description = "A" });
        var providerB = Found("providerB", "extB", new EnrichmentData { Description = "B" });

        var result = await CreateOrchestrator(providerA, providerB).EnrichTitleAsync(CreateTitle(), CreatePlatform());

        providerB.InitializeCount.ShouldBe(1);
        providerB.ReceivedContexts.ShouldBeEmpty();
        providerA.ReceivedContexts.Count.ShouldBe(1);
        result.Enriched.ShouldBeTrue();
    }

    [Fact]
    public async Task EnrichTitleAsync_RuntimeConfiguredProvider_IgnoresStaticEnablement()
    {
        _options.Providers["providerA"] = new ProviderSettings { Enabled = false };
        var provider = Found("providerA", "extA", new EnrichmentData { Description = "A" });
        provider.UsesRuntimeConfiguration = true;

        var result = await CreateOrchestrator(provider).EnrichTitleAsync(CreateTitle(), CreatePlatform());

        provider.ReceivedContexts.Count.ShouldBe(1);
        result.Enriched.ShouldBeTrue();
    }

    [Fact]
    public async Task EnrichTitleAsync_ConfirmedLink_IsNotDisplacedByADifferentGame()
    {
        var confirmed = TitleExternalId.Rehydrate(1, 1, "providerA", "confirmedA", 0.5f, true, DateTimeOffset.UtcNow);
        var title = CreateTitle(externalIds: [confirmed]);
        Trust("providerA", "differentA");
        var provider = Found("providerA", "differentA", new EnrichmentData { Description = "Fresh data" }, .99f);

        var result = await CreateOrchestrator(provider).EnrichTitleAsync(title, CreatePlatform());

        title.GetExternalId("providerA")!.ExternalId.ShouldBe("confirmedA");
        title.MetadataLayers.ShouldBeEmpty();
        title.Description.ShouldBeNull();
        ShouldBeOutcome(result, OrchestrationResult.Failed);
    }

    [Fact]
    public async Task EnrichTitleAsync_MoreConfidentExistingLink_IsNotDisplacedByAWeakerGame()
    {
        var existing = TitleExternalId.Rehydrate(1, 1, "providerA", "strongA", 0.9f, false, DateTimeOffset.UtcNow);
        var title = CreateTitle(externalIds: [existing]);
        var weaker = Found("providerA", "extA", new EnrichmentData { Description = "Weaker" }, .5f);

        (await CreateOrchestrator(weaker).EnrichTitleAsync(title, CreatePlatform())).Enriched.ShouldBeFalse();

        title.MetadataLayers.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnrichTitleAsync_SameGameAsConfirmedLink_IsApplied()
    {
        var confirmed = TitleExternalId.Rehydrate(1, 1, "providerA", "extA", 0.5f, true, DateTimeOffset.UtcNow);
        var title = CreateTitle(externalIds: [confirmed]);
        var provider = Found("providerA", "extA", new EnrichmentData { Description = "Confirmed metadata" }, .1f);

        var result = await CreateOrchestrator(provider).EnrichTitleAsync(title, CreatePlatform());

        ShouldBeOutcome(result, OrchestrationResult.Found(1f));
        title.Description.ShouldBe("Confirmed metadata");
    }

    [Theory]
    [InlineData(EnrichmentOutcome.Error, EnrichmentOutcome.Error, true)]
    [InlineData(EnrichmentOutcome.NotFound, EnrichmentOutcome.Error, false)]
    [InlineData(EnrichmentOutcome.PlatformNotSupported, EnrichmentOutcome.Error, false)]
    [InlineData(EnrichmentOutcome.NotFound, EnrichmentOutcome.NotFound, false)]
    public async Task EnrichTitleAsync_NoUsableData_FailsOnlyWhenEveryAnsweringProviderErrored(
        EnrichmentOutcome a, EnrichmentOutcome b, bool allErrored)
    {
        var providerA = new FakeMetadataProvider("providerA", _ => Outcome(a));
        var providerB = new FakeMetadataProvider("providerB", _ => Outcome(b));

        var result = await CreateOrchestrator(providerA, providerB).EnrichTitleAsync(CreateTitle(), CreatePlatform());

        result.Enriched.ShouldBeFalse();
        result.AllErrored.ShouldBe(allErrored);
        _rematerializations.ShouldBe(0);
    }

    [Fact]
    public async Task EnrichTitleAsync_FoundWithoutData_IsAnsweredButNotEnriched()
    {
        var title = CreateTitle();
        var provider = Found("providerA", "extA", new EnrichmentData());

        var result = await CreateOrchestrator(provider).EnrichTitleAsync(title, CreatePlatform());

        ShouldBeOutcome(result, OrchestrationResult.NotFound);
        title.MetadataLayers.ShouldBeEmpty();
        _rematerializations.ShouldBe(0);
    }

    [Fact]
    public async Task EnrichTitleAsync_SeveralProvidersEnrich_RematerializesOnceAfterAll()
    {
        var title = CreateTitle();
        var providerA = Found("providerA", "extA", new EnrichmentData { Description = "A desc", Publisher = "A pub" });
        var providerB = Found("providerB", "extB", new EnrichmentData { Description = "B desc", Developer = "B dev" });

        await CreateOrchestrator(providerA, providerB).EnrichTitleAsync(title, CreatePlatform());

        _rematerializations.ShouldBe(1);
        title.MetadataLayers.Count.ShouldBe(2);
        title.Description.ShouldBe("A desc");
        title.Developer.ShouldBe("B dev");
    }

    [Fact]
    public async Task EnrichTitleAsync_BelowConfiguredConfidence_StoresLayersWithoutRematerializing()
    {
        _options.MinimumAutoEnrichConfidence = 1.5f;
        var title = CreateTitle();
        var provider = Found("providerA", "extA", new EnrichmentData { Description = "Held for review" });

        var result = await CreateOrchestrator(provider).EnrichTitleAsync(title, CreatePlatform());

        ShouldBeOutcome(result, OrchestrationResult.Found(1f));
        title.MetadataLayers.Count.ShouldBe(1);
        title.Description.ShouldBeNull();
        _rematerializations.ShouldBe(0);
    }

    [Fact]
    public async Task EnrichTitleAsync_DownloadMedia_StagesOnlyEligibleMediaAndHandsItOff()
    {
        _options.DownloadMedia = true;
        _options.MediaTypesToDownload = [MediaType.Cover, MediaType.Screenshot, MediaType.Logo, MediaType.Video];
        var title = CreateTitle();
        title.AddMedia(TitleMedia.CreateNew(title.Id, MediaType.Logo, 9, "providerA", "image/png", DateTimeOffset.UnixEpoch, "https://logo"));
        _curation.GetExcludedUrlsAsync(title.Id, Arg.Any<CancellationToken>()).Returns(["https://rejected"]);
        var provider = new FakeMetadataProvider("providerA", _ => EnrichmentResult.Found("extA", 1f,
            new EnrichmentData { Description = "A" }, new Dictionary<MediaType, string>
            {
                [MediaType.Cover] = "https://cover",
                [MediaType.Screenshot] = "https://shot",
                [MediaType.Logo] = "https://logo-new",
                [MediaType.Video] = "https://rejected",
                [MediaType.Banner] = "https://banner"
            }));

        await using var result = await CreateOrchestrator(provider).EnrichTitleAsync(title, CreatePlatform());

        _stager.Requests.ShouldBe([(title.Id, "providerA", MediaType.Screenshot, "https://shot")]);
        result.StagedMedia.ShouldHaveSingleItem().Type.ShouldBe(MediaType.Screenshot);
        title.Media.Count.ShouldBe(1);
        _stager.Downloads.ShouldAllBe(download => !download.Disposed);
    }

    [Fact]
    public async Task EnrichTitleAsync_ResultDisposed_ReleasesStagedMedia()
    {
        EnableScreenshotDownloads();
        var result = await CreateOrchestrator(WithScreenshot("providerA", "extA")).EnrichTitleAsync(CreateTitle(), CreatePlatform());

        await result.DisposeAsync();

        _stager.Downloads.ShouldHaveSingleItem().Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task EnrichTitleAsync_CancelledAfterStaging_DisposesEveryStagedMediaAndPropagates()
    {
        EnableScreenshotDownloads();
        using var cancellation = new CancellationTokenSource();
        var providerA = WithScreenshot("providerA", "extA");
        var providerB = new FakeMetadataProvider("providerB", _ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return EnrichmentResult.NotFound();
        });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            CreateOrchestrator(providerA, providerB).EnrichTitleAsync(CreateTitle(), CreatePlatform(), cancellation.Token));

        _stager.Downloads.ShouldHaveSingleItem().Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task EnrichTitleAsync_StagerCancelled_DisposesMediaAlreadyStaged()
    {
        EnableScreenshotDownloads();
        _stager.CancelOnRequest = 2;

        await Should.ThrowAsync<OperationCanceledException>(() =>
            CreateOrchestrator(WithScreenshot("providerA", "extA"), WithScreenshot("providerB", "extB"))
                .EnrichTitleAsync(CreateTitle(), CreatePlatform()));

        _stager.Downloads.ShouldHaveSingleItem().Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task EnrichTitleAsync_RematerializationFails_DisposesStagedMedia()
    {
        EnableScreenshotDownloads();
        _platformDefaults.GetByPlatformIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database down"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            CreateOrchestrator(WithScreenshot("providerA", "extA")).EnrichTitleAsync(CreateTitle(), CreatePlatform()));

        _stager.Downloads.ShouldHaveSingleItem().Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task EnrichTitleAsync_ReleaseFailsDuringCancellation_StillSurfacesCancellationAndReleasesTheRest()
    {
        EnableScreenshotDownloads();
        _stager.FailReleaseOnRequests.Add(1);
        using var cancellation = new CancellationTokenSource();
        var providerC = new FakeMetadataProvider("providerC", _ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return EnrichmentResult.NotFound();
        });
        Trust("providerC", "extC");

        var error = await Should.ThrowAsync<Exception>(() => CreateOrchestrator(
                WithScreenshot("providerA", "extA"), WithScreenshot("providerB", "extB"), providerC)
            .EnrichTitleAsync(CreateTitle(), CreatePlatform(), cancellation.Token));

        error.ShouldBeAssignableTo<OperationCanceledException>();
        _ = _stager.Files[0].Received(1).DisposeAsync();
        _ = _stager.Files[1].Received(1).DisposeAsync();
        // A failing file release still releases its download, and the next item is still released.
        _stager.Downloads.ShouldAllBe(download => download.Disposed);
    }

    [Fact]
    public async Task EnrichTitleAsync_ReleaseFailsAfterRematerializationFailure_SurfacesTheRootCause()
    {
        EnableScreenshotDownloads();
        _stager.FailReleaseOnRequests.Add(1);
        _platformDefaults.GetByPlatformIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database down"));

        var error = await Should.ThrowAsync<Exception>(() =>
            CreateOrchestrator(WithScreenshot("providerA", "extA")).EnrichTitleAsync(CreateTitle(), CreatePlatform()));

        error.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("database down");
        _stager.Downloads.ShouldHaveSingleItem().Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task EnrichTitleAsync_ProviderThrowsBesideMediaWithFailingRelease_KeepsTheNormalResultPath()
    {
        EnableScreenshotDownloads();
        _stager.FailReleaseOnRequests.Add(1);
        var providerB = new FakeMetadataProvider("providerB", _ => throw new InvalidOperationException("API failure"));

        var result = await CreateOrchestrator(WithScreenshot("providerA", "extA"), providerB)
            .EnrichTitleAsync(CreateTitle(), CreatePlatform());

        // The provider error is tallied; staged media is handed off to the caller, not released early.
        ShouldBeOutcome(result, OrchestrationResult.Found(1f));
        result.StagedMedia.ShouldHaveSingleItem();
        _stager.Downloads.ShouldHaveSingleItem().Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task EnrichStreamAsync_ReleaseFailsOnAbandonedWindow_DisposesEnumeratorWithoutThrowing()
    {
        EnableScreenshotDownloads();
        _stager.FailReleaseOnRequests.Add(2);
        _stager.FailReleaseOnRequests.Add(3);
        var enumerator = CreateOrchestrator(WithScreenshot("providerA", "extA"))
            .EnrichStreamAsync(Stream(CreateTitle(1), CreateTitle(2), CreateTitle(3))).GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        await using var handedOff = enumerator.Current.Result;
        await Should.NotThrowAsync(async () => await enumerator.DisposeAsync());

        _stager.Downloads.Count.ShouldBe(3);
        _stager.Downloads.Skip(1).ShouldAllBe(download => download.Disposed);
        _stager.Downloads[0].Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task EnrichStreamAsync_MixedResults_AreReportedPerTitle()
    {
        var title1 = CreateTitle(1, "Found Game");
        var title2 = CreateTitle(2, "Missing Game");
        var provider = new FakeMetadataProvider("providerA", ctx => ctx.TitleName == "Found Game"
            ? EnrichmentResult.Found("extA", .9f, new EnrichmentData { Description = "Found" })
            : EnrichmentResult.NotFound());

        var results = await Collect(CreateOrchestrator(provider).EnrichStreamAsync(Stream(title1, title2)));

        results.Select(x => x.TitleId).ShouldBe([1, 2]);
        ShouldBeOutcome(results[0].Result, OrchestrationResult.Found(1f));
        ShouldBeOutcome(results[1].Result, OrchestrationResult.NotFound);
        title1.Description.ShouldBe("Found");
        title2.Description.ShouldBeNull();
    }

    [Fact]
    public async Task EnrichStreamAsync_Window_RunsProviderMajorAcrossAllTitles()
    {
        var log = new List<string>();
        var providerA = new FakeMetadataProvider("providerA", _ => EnrichmentResult.NotFound()) { CallLog = log };
        var providerB = new FakeMetadataProvider("providerB", _ => EnrichmentResult.NotFound()) { CallLog = log };

        await Collect(CreateOrchestrator(providerA, providerB)
            .EnrichStreamAsync(Stream(CreateTitle(1, "One"), CreateTitle(2, "Two"))));

        log.ShouldBe(["providerA:One", "providerA:Two", "providerB:One", "providerB:Two"]);
        providerA.StreamCount.ShouldBe(1);
        providerB.StreamCount.ShouldBe(1);
        await _identities.Received(1).DiscoverAsync(1, null, Arg.Any<CancellationToken>());
        await _identities.Received(1).DiscoverAsync(2, null, Arg.Any<CancellationToken>());
        await _identities.DidNotReceive().ResolveTrustedAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichStreamAsync_MoreThanOneWindow_YieldsEachWindowBeforeReadingTheNext()
    {
        var titles = Enumerable.Range(1, EnrichmentOrchestrator.StreamWindowSize + 5)
            .Select(id => CreateTitle(id, $"Game {id}")).ToArray();
        var provider = Found("providerA", "extA", new EnrichmentData { Description = "Windowed" });
        var contextsBuiltAtFirstResult = -1;
        var results = new List<int>();

        await foreach (var (titleId, result) in CreateOrchestrator(provider).EnrichStreamAsync(Stream(titles)))
        {
            if (results.Count == 0)
                contextsBuiltAtFirstResult = _contexts.ReceivedCalls().Count();
            results.Add(titleId);
            result.Enriched.ShouldBeTrue();
        }

        contextsBuiltAtFirstResult.ShouldBe(EnrichmentOrchestrator.StreamWindowSize);
        results.ShouldBe(titles.Select(x => x.Id));
        provider.StreamCount.ShouldBe(2);
        provider.InitializeCount.ShouldBe(1);
        _rematerializations.ShouldBe(titles.Length);
    }

    [Fact]
    public async Task EnrichStreamAsync_UntrustedAndSuppressedTitles_AreNotStreamedToTheProvider()
    {
        var trusted = CreateTitle(1, "Trusted");
        var untrusted = CreateTitle(2, "Untrusted");
        var suppressed = CreateTitle(3, "Suppressed");
        _identities.GetTrustedAsync(untrusted.Id, "providerA", Arg.Any<CancellationToken>()).Returns((string?)null);
        _matches.IsSuppressedAsync(suppressed.Id, "providerA", Arg.Any<CancellationToken>()).Returns(true);
        var provider = Found("providerA", "extA", new EnrichmentData { Description = "A" });

        var results = await Collect(CreateOrchestrator(provider).EnrichStreamAsync(Stream(trusted, untrusted, suppressed)));

        provider.ReceivedContexts.Select(x => x.TitleName).ShouldBe(["Trusted"]);
        results.Select(x => x.Result.Enriched).ShouldBe([true, false, false]);
        await _identities.DidNotReceive().GetTrustedAsync(suppressed.Id, "providerA", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichStreamAsync_ConsumerStopsEarly_DisposesMediaNotYetHandedOff()
    {
        EnableScreenshotDownloads();
        OrchestrationResult? handedOff = null;

        await foreach (var (_, result) in CreateOrchestrator(WithScreenshot("providerA", "extA"))
                           .EnrichStreamAsync(Stream(CreateTitle(1), CreateTitle(2))))
        {
            handedOff = result;
            break;
        }

        handedOff!.StagedMedia.ShouldHaveSingleItem();
        _stager.Downloads.Count.ShouldBe(2);
        _stager.Downloads.Count(download => download.Disposed).ShouldBe(1);
        await handedOff.DisposeAsync();
        _stager.Downloads.ShouldAllBe(download => download.Disposed);
    }

    [Fact]
    public async Task EnrichStreamAsync_Cancelled_DisposesTheWindowsStagedMedia()
    {
        EnableScreenshotDownloads();
        using var cancellation = new CancellationTokenSource();
        var providerA = WithScreenshot("providerA", "extA");
        var providerB = new FakeMetadataProvider("providerB", _ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return EnrichmentResult.NotFound();
        });

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in CreateOrchestrator(providerA, providerB)
                               .EnrichStreamAsync(Stream(CreateTitle(1), CreateTitle(2)), cancellation.Token))
            { }
        });

        _stager.Downloads.Count.ShouldBe(2);
        _stager.Downloads.ShouldAllBe(download => download.Disposed);
    }

    private EnrichmentOrchestrator CreateOrchestrator(params IMetadataProvider[] providers) => new(
        providers,
        _contexts,
        _identities,
        _matches,
        new CountingRematerializer(new TitleMetadataRematerializer(_platformDefaults, Options.Create(_options)),
            () => _rematerializations++),
        _stager,
        _curation,
        Options.Create(_options),
        NullLogger<EnrichmentOrchestrator>.Instance, TimeProvider.System);

    private static void ShouldBeOutcome(OrchestrationResult actual, OrchestrationResult expected)
    {
        actual.Enriched.ShouldBe(expected.Enriched);
        actual.BestConfidence.ShouldBe(expected.BestConfidence);
        actual.AllErrored.ShouldBe(expected.AllErrored);
    }

    private void Trust(string providerId, string gameId)
    {
        _identities.GetTrustedAsync(Arg.Any<int>(), providerId, Arg.Any<CancellationToken>()).Returns(gameId);
    }

    private void EnableScreenshotDownloads()
    {
        _options.DownloadMedia = true;
        _options.MediaTypesToDownload = [MediaType.Screenshot];
    }

    private static FakeMetadataProvider Found(string providerId, string gameId, EnrichmentData data, float score = 1f) =>
        new(providerId, _ => EnrichmentResult.Found(gameId, score, data));

    private static FakeMetadataProvider WithScreenshot(string providerId, string gameId) =>
        new(providerId, ctx => EnrichmentResult.Found(gameId, 1f, new EnrichmentData { Description = providerId },
            new Dictionary<MediaType, string> { [MediaType.Screenshot] = $"https://{providerId}/{ctx.TitleName}" }));

    private static EnrichmentResult Outcome(EnrichmentOutcome outcome) => outcome switch
    {
        EnrichmentOutcome.Error => EnrichmentResult.Error("failure"),
        EnrichmentOutcome.PlatformNotSupported => EnrichmentResult.PlatformNotSupported(),
        _ => EnrichmentResult.NotFound()
    };

    private static Title CreateTitle(int id = 1, string name = "Super Mario World",
        IEnumerable<TitleExternalId>? externalIds = null) => Title.Rehydrate(
        id, 10, name, name.ToLowerInvariant(), null, null, null, null, null, null, null,
        EnrichmentStatus.None, null, DateTimeOffset.UtcNow, externalIds);

    private static Platform CreatePlatform() =>
        Platform.Rehydrate(10, "Super Nintendo", "snes", "Nintendo", DateTimeOffset.UtcNow);

    private static async IAsyncEnumerable<(Title, Platform)> Stream(params Title[] titles)
    {
        foreach (var title in titles)
            yield return (title, CreatePlatform());
        await Task.CompletedTask;
    }

    private static async Task<List<(int TitleId, OrchestrationResult Result)>> Collect(
        IAsyncEnumerable<(int TitleId, OrchestrationResult Result)> results)
    {
        var collected = new List<(int TitleId, OrchestrationResult Result)>();
        await foreach (var result in results)
            collected.Add(result);
        return collected;
    }

    private sealed class CountingRematerializer(IMetadataRematerializer inner, Action onCall) : IMetadataRematerializer
    {
        public Task RematerializeAsync(Title title, CancellationToken ct = default)
        {
            onCall();
            return inner.RematerializeAsync(title, ct);
        }
    }

    private sealed class FakeMediaStager : IEnrichmentMediaStager
    {
        public List<(int TitleId, string ProviderId, MediaType Type, string Url)> Requests { get; } = [];
        public List<TrackedDownload> Downloads { get; } = [];
        public int? CancelOnRequest { get; set; }

        /// <summary>Requests whose staged file throws when released; the download must still be released.</summary>
        public HashSet<int> FailReleaseOnRequests { get; } = [];
        public List<IStagedFile> Files { get; } = [];

        public Task<StagedTitleMedia?> StageAsync(int titleId, string providerId, MediaType mediaType, string url,
            CancellationToken cancellationToken)
        {
            Requests.Add((titleId, providerId, mediaType, url));
            if (Requests.Count == CancelOnRequest)
                throw new OperationCanceledException();
            var download = new TrackedDownload();
            Downloads.Add(download);
            var file = Substitute.For<IStagedFile>();
            if (FailReleaseOnRequests.Contains(Requests.Count))
                file.DisposeAsync().Returns(ValueTask.FromException(new IOException("staged file is locked")));
            Files.Add(file);
            return Task.FromResult<StagedTitleMedia?>(new StagedTitleMedia(mediaType, providerId, "image/png", url,
                file, download));
        }
    }

    private sealed class TrackedDownload : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
