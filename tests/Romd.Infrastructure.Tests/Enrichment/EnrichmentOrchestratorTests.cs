using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Enrichment;
using Romd.Infrastructure.Tests.Enrichment.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public class EnrichmentOrchestratorTests
{
    private readonly IEnrichmentContextFactory _contextFactory = Substitute.For<IEnrichmentContextFactory>();

    private readonly EnrichmentOptions _options = new()
    {
        GlobalSourcePriority = ["providerA", "providerB"], DownloadMedia = false
    };

    private readonly IPlatformFieldDefaultRepository _platformFieldDefaultRepo =
        Substitute.For<IPlatformFieldDefaultRepository>();

    public EnrichmentOrchestratorTests()
    {
        _platformFieldDefaultRepo.GetByPlatformIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());
    }

    private EnrichmentOrchestrator CreateOrchestrator(params IMetadataProvider[] providers)
    {
        return new EnrichmentOrchestrator(
            providers,
            _contextFactory,
            new MetadataMerger(_platformFieldDefaultRepo, Options.Create(_options)),
            null!, // MediaDownloader not needed when DownloadMedia = false
            Options.Create(_options),
            NullLogger<EnrichmentOrchestrator>.Instance);
    }

    private static Title CreateTitle(int id = 1)
    {
        return Title.Rehydrate(
            id, 10, "Super Mario World",
            "super mario world",
            null, null, null, null,
            null, null, null,
            EnrichmentStatus.None, null,
            DateTimeOffset.UtcNow);
    }

    private static Platform CreatePlatform() =>
        Platform.Rehydrate(10, "Super Nintendo", "snes", "Nintendo", DateTimeOffset.UtcNow);

    private static EnrichmentContext CreateContext()
    {
        return new EnrichmentContext { TitleName = "Super Mario World", PlatformShortName = "snes" };
    }

    [Fact]
    public async Task EnrichTitleAsync_AllProvidersRun_NoShortCircuit()
    {
        var title = CreateTitle();
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        var providerA = new FakeMetadataProvider("providerA", _ =>
            EnrichmentResult.Found("extA", 0.9f,
                new EnrichmentData { Description = "Provider A description", Publisher = "Publisher A" }));

        var providerB = new FakeMetadataProvider("providerB", _ =>
            EnrichmentResult.Found("extB", 0.8f, new EnrichmentData { Developer = "Developer B", Genre = "Action" }));

        var orchestrator = CreateOrchestrator(providerA, providerB);
        var result = await orchestrator.EnrichTitleAsync(title, platform);

        // Both providers were called
        providerA.ReceivedContexts.Count.ShouldBe(1);
        providerB.ReceivedContexts.Count.ShouldBe(1);

        // Result indicates enrichment found with best confidence
        result.Enriched.ShouldBeTrue();
        result.BestConfidence.ShouldBe(0.9f);

        // Both layers stored — fields from both providers are materialized
        title.Description.ShouldBe("Provider A description");
        title.Publisher.ShouldBe("Publisher A");
        title.Developer.ShouldBe("Developer B");
        title.Genre.ShouldBe("Action");

        // Provenance tracks correct sources
        title.FieldProvenance["Description"].ShouldBe("providerA");
        title.FieldProvenance["Publisher"].ShouldBe("providerA");
        title.FieldProvenance["Developer"].ShouldBe("providerB");
        title.FieldProvenance["Genre"].ShouldBe("providerB");
    }

    [Fact]
    public async Task EnrichTitleAsync_ProviderBAlsoHasData_PriorityWins()
    {
        var title = CreateTitle();
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        var providerA = new FakeMetadataProvider("providerA", _ =>
            EnrichmentResult.Found("extA", 0.9f, new EnrichmentData { Description = "Provider A description" }));

        var providerB = new FakeMetadataProvider("providerB", _ =>
            EnrichmentResult.Found("extB", 0.8f, new EnrichmentData { Description = "Provider B description" }));

        var orchestrator = CreateOrchestrator(providerA, providerB);
        await orchestrator.EnrichTitleAsync(title, platform);

        // Both providers called (no short-circuit)
        providerA.ReceivedContexts.Count.ShouldBe(1);
        providerB.ReceivedContexts.Count.ShouldBe(1);

        // providerA wins Description due to global priority order
        title.Description.ShouldBe("Provider A description");
        title.FieldProvenance["Description"].ShouldBe("providerA");
    }

    [Fact]
    public async Task EnrichTitleAsync_OneProviderNotFound_OtherStillRuns()
    {
        var title = CreateTitle();
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        var providerA = new FakeMetadataProvider("providerA", _ =>
            EnrichmentResult.NotFound());

        var providerB = new FakeMetadataProvider("providerB", _ =>
            EnrichmentResult.Found("extB", 0.85f, new EnrichmentData { Description = "Provider B description" }));

        var orchestrator = CreateOrchestrator(providerA, providerB);
        var result = await orchestrator.EnrichTitleAsync(title, platform);

        result.Enriched.ShouldBeTrue();
        result.BestConfidence.ShouldBe(0.85f);
        title.Description.ShouldBe("Provider B description");
        title.FieldProvenance["Description"].ShouldBe("providerB");
    }

    [Fact]
    public async Task EnrichTitleAsync_OneProviderThrows_OtherStillRuns()
    {
        var title = CreateTitle();
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        var providerA = new FakeMetadataProvider("providerA", _ =>
            throw new InvalidOperationException("API failure"));

        var providerB = new FakeMetadataProvider("providerB", _ =>
            EnrichmentResult.Found("extB", 0.7f, new EnrichmentData { Publisher = "Publisher B" }));

        var orchestrator = CreateOrchestrator(providerA, providerB);
        var result = await orchestrator.EnrichTitleAsync(title, platform);

        // providerB still ran and succeeded
        result.Enriched.ShouldBeTrue();
        title.Publisher.ShouldBe("Publisher B");
    }

    [Fact]
    public async Task EnrichTitleAsync_NoProvidersConfigured_ThrowsBeforeMutatingTitle()
    {
        var title = CreateTitle();
        var platform = CreatePlatform();

        var unconfigured = new FakeMetadataProvider("unconfigured", _ =>
            EnrichmentResult.NotFound()) { IsConfigured = false };

        var orchestrator = CreateOrchestrator(unconfigured);
        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => orchestrator.EnrichTitleAsync(title, platform));

        error.Message.ShouldContain("Metadata Providers");
        title.EnrichmentStatus.ShouldBe(EnrichmentStatus.None);
        await _contextFactory.DidNotReceive().CreateAsync(title, platform, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichStreamAsync_NoProvidersConfigured_ThrowsBeforeMutatingTitle()
    {
        var title = CreateTitle();
        var platform = CreatePlatform();
        var orchestrator = CreateOrchestrator();
        async IAsyncEnumerable<(Title, Platform)> Titles()
        {
            yield return (title, platform);
            await Task.CompletedTask;
        }

        var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in orchestrator.EnrichStreamAsync(Titles())) { }
        });

        error.Message.ShouldContain("Metadata Providers");
        title.EnrichmentStatus.ShouldBe(EnrichmentStatus.None);
        await _contextFactory.DidNotReceive().CreateAsync(title, platform, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichTitleAsync_DisabledProvider_NotCalled()
    {
        var title = CreateTitle();
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        // Disable providerB
        _options.Providers["providerB"] = new ProviderSettings { Enabled = false };

        var providerA = new FakeMetadataProvider("providerA", _ =>
            EnrichmentResult.Found("extA", 0.9f, new EnrichmentData { Description = "Provider A only" }));

        var providerB = new FakeMetadataProvider("providerB", _ =>
            EnrichmentResult.Found("extB", 0.8f, new EnrichmentData()));

        var orchestrator = CreateOrchestrator(providerA, providerB);
        var result = await orchestrator.EnrichTitleAsync(title, platform);

        providerA.ReceivedContexts.Count.ShouldBe(1);
        providerB.ReceivedContexts.ShouldBeEmpty();
        result.Enriched.ShouldBeTrue();
    }

    [Fact]
    public async Task EnrichTitleAsync_AllProvidersNotFound_ReturnsNotFound()
    {
        var title = CreateTitle();
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        var providerA = new FakeMetadataProvider("providerA", _ =>
            EnrichmentResult.NotFound());
        var providerB = new FakeMetadataProvider("providerB", _ =>
            EnrichmentResult.NotFound());

        var orchestrator = CreateOrchestrator(providerA, providerB);
        var result = await orchestrator.EnrichTitleAsync(title, platform);

        result.Enriched.ShouldBeFalse();
        title.Description.ShouldBeNull();
    }

    [Fact]
    public async Task EnrichTitleAsync_RematerializationHappensOnce()
    {
        var title = CreateTitle();
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        var providerA = new FakeMetadataProvider("providerA", _ =>
            EnrichmentResult.Found("extA", 0.9f, new EnrichmentData { Description = "A desc", Publisher = "A pub" }));

        var providerB = new FakeMetadataProvider("providerB", _ =>
            EnrichmentResult.Found("extB", 0.8f, new EnrichmentData
            {
                Description = "B desc", // Should lose to A due to priority
                Developer = "B dev"
            }));

        var orchestrator = CreateOrchestrator(providerA, providerB);
        await orchestrator.EnrichTitleAsync(title, platform);

        // The materialized state should reflect global priority:
        // A's description wins (higher priority), B's developer fills the gap
        title.Description.ShouldBe("A desc");
        title.Publisher.ShouldBe("A pub");
        title.Developer.ShouldBe("B dev");

        // Both layers exist
        title.MetadataLayers.Count.ShouldBe(2);

        // Both external IDs stored
        title.ExternalIds.Count.ShouldBe(2);
        title.GetExternalId("providerA").ShouldNotBeNull();
        title.GetExternalId("providerB").ShouldNotBeNull();
    }

    [Fact]
    public async Task EnrichTitleAsync_PerProviderContext_GetsCorrectExternalId()
    {
        // Title has an existing external ID for providerA
        var extA = TitleExternalId.Rehydrate(1, 1, "providerA", "existingA", 0.9f, false, DateTimeOffset.UtcNow);
        var title = Title.Rehydrate(
            1, 10, "Super Mario World",
            "super mario world",
            null, null, null, null,
            null, null, null,
            EnrichmentStatus.None, null,
            DateTimeOffset.UtcNow, [extA]);
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        // Capture the contexts passed to each provider
        EnrichmentContext? capturedContextA = null;
        EnrichmentContext? capturedContextB = null;

        var providerA = new FakeMetadataProvider("providerA", ctx =>
        {
            capturedContextA = ctx;
            return EnrichmentResult.Found("extA", 0.9f, new EnrichmentData { Description = "A" });
        });

        var providerB = new FakeMetadataProvider("providerB", ctx =>
        {
            capturedContextB = ctx;
            return EnrichmentResult.NotFound();
        });

        var orchestrator = CreateOrchestrator(providerA, providerB);
        await orchestrator.EnrichTitleAsync(title, platform);

        // ProviderA should receive the existing external ID
        capturedContextA.ShouldNotBeNull();
        capturedContextA.ExistingExternalId.ShouldBe("existingA");

        // ProviderB has no existing external ID
        capturedContextB.ShouldNotBeNull();
        capturedContextB.ExistingExternalId.ShouldBeNull();
    }

    [Fact]
    public async Task EnrichTitleAsync_ConfirmedLink_BoostsConfidence()
    {
        // Title has a confirmed external ID for providerA
        var extA = TitleExternalId.Rehydrate(1, 1, "providerA", "existingA", 0.5f, true, DateTimeOffset.UtcNow);
        var title = Title.Rehydrate(
            1, 10, "Super Mario World",
            "super mario world",
            null, null, null, null,
            null, null, null,
            EnrichmentStatus.None, null,
            DateTimeOffset.UtcNow, [extA]);
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        var providerA = new FakeMetadataProvider("providerA", _ =>
            EnrichmentResult.Found("existingA", 0.5f, new EnrichmentData { Description = "A" }));

        var orchestrator = CreateOrchestrator(providerA);
        var result = await orchestrator.EnrichTitleAsync(title, platform);

        // Confirmed link should boost confidence to 1.0
        result.Enriched.ShouldBeTrue();
        result.BestConfidence.ShouldBe(1.0f);
    }

    [Fact]
    public async Task EnrichTitleAsync_ConfirmedLink_NotOverwrittenByAutoMatch()
    {
        // Title has a confirmed external ID for providerA
        var extA = TitleExternalId.Rehydrate(1, 1, "providerA", "existingA", 0.9f, true, DateTimeOffset.UtcNow);
        var title = Title.Rehydrate(
            1, 10, "Super Mario World",
            "super mario world",
            null, null, null, null,
            null, null, null,
            EnrichmentStatus.Completed, null,
            DateTimeOffset.UtcNow, [extA]);
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        // Provider returns a different external ID
        var providerA = new FakeMetadataProvider("providerA", _ =>
            EnrichmentResult.Found("differentId", 0.95f, new EnrichmentData { Description = "Fresh data" }));

        var orchestrator = CreateOrchestrator(providerA);
        await orchestrator.EnrichTitleAsync(title, platform);

        // External ID should remain unchanged (confirmed)
        title.GetExternalId("providerA")!.ExternalId.ShouldBe("existingA");
        title.GetExternalId("providerA")!.IsConfirmed.ShouldBeTrue();

        // Content from a rejected game must not be applied to the confirmed association.
        title.Description.ShouldBeNull();
    }

    [Fact]
    public async Task EnrichTitleAsync_AllProvidersError_ReturnsFailed()
    {
        var title = CreateTitle();
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        var providerA = new FakeMetadataProvider("providerA", _ =>
            EnrichmentResult.Error("API timeout"));
        var providerB = new FakeMetadataProvider("providerB", _ =>
            EnrichmentResult.Error("Rate limited"));

        var orchestrator = CreateOrchestrator(providerA, providerB);
        var result = await orchestrator.EnrichTitleAsync(title, platform);

        result.Enriched.ShouldBeFalse();
        result.AllErrored.ShouldBeTrue();
    }

    [Fact]
    public async Task EnrichTitleAsync_MixedErrorAndNotFound_ReturnsNotFound()
    {
        var title = CreateTitle();
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        var providerA = new FakeMetadataProvider("providerA", _ =>
            EnrichmentResult.NotFound());
        var providerB = new FakeMetadataProvider("providerB", _ =>
            EnrichmentResult.Error("Connection refused"));

        var orchestrator = CreateOrchestrator(providerA, providerB);
        var result = await orchestrator.EnrichTitleAsync(title, platform);

        result.Enriched.ShouldBeFalse();
        result.AllErrored.ShouldBeFalse();
    }

    [Fact]
    public async Task EnrichTitleAsync_PlatformNotSupportedAndError_ReturnsNotFound()
    {
        var title = CreateTitle();
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        var providerA = new FakeMetadataProvider("providerA", _ =>
            EnrichmentResult.PlatformNotSupported());
        var providerB = new FakeMetadataProvider("providerB", _ =>
            EnrichmentResult.Error("API failure"));

        var orchestrator = CreateOrchestrator(providerA, providerB);
        var result = await orchestrator.EnrichTitleAsync(title, platform);

        // PlatformNotSupported counts as completed, so not all errored
        result.Enriched.ShouldBeFalse();
        result.AllErrored.ShouldBeFalse();
    }

    [Fact]
    public async Task EnrichTitleAsync_FoundWithNoData_NotCountedAsEnriched()
    {
        var title = CreateTitle();
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        // Provider returns Found but with empty EnrichmentData
        var providerA = new FakeMetadataProvider("providerA", _ =>
            EnrichmentResult.Found("extA", 0.9f, new EnrichmentData()));

        var orchestrator = CreateOrchestrator(providerA);
        var result = await orchestrator.EnrichTitleAsync(title, platform);

        // Should NOT be counted as enriched since data is empty
        result.Enriched.ShouldBeFalse();
    }

    [Fact]
    public async Task EnrichTitleAsync_FoundWithNoData_ExternalIdStillStored()
    {
        var title = CreateTitle();
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        // Provider returns Found but with empty EnrichmentData
        var providerA = new FakeMetadataProvider("providerA", _ =>
            EnrichmentResult.Found("extA", 0.9f, new EnrichmentData()));

        var orchestrator = CreateOrchestrator(providerA);
        await orchestrator.EnrichTitleAsync(title, platform);

        // External ID should still be stored even though not "enriched"
        title.GetExternalId("providerA").ShouldNotBeNull();
        title.GetExternalId("providerA")!.ExternalId.ShouldBe("extA");
    }

    [Fact]
    public async Task EnrichTitleAsync_LowConfidence_SkipsRematerialization()
    {
        var title = CreateTitle();
        var platform = CreatePlatform();
        var context = CreateContext();

        _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(context);

        // Confidence 0.3 is below default threshold of 0.6
        var providerA = new FakeMetadataProvider("providerA", _ =>
            EnrichmentResult.Found("extA", 0.3f,
                new EnrichmentData
                {
                    Description = "Low confidence description", Publisher = "Low confidence publisher"
                }));

        var orchestrator = CreateOrchestrator(providerA);
        var result = await orchestrator.EnrichTitleAsync(title, platform);

        // Result should indicate enrichment found
        result.Enriched.ShouldBeTrue();
        result.BestConfidence.ShouldBe(0.3f);

        // But layers are stored — metadata layer exists
        title.MetadataLayers.Count.ShouldBe(1);

        // Effective fields should NOT be materialized (rematerialization was skipped)
        title.Description.ShouldBeNull();
        title.Publisher.ShouldBeNull();
    }

    [Fact]
    public async Task EnrichStreamAsync_MultipleTitles_AllProcessed()
    {
        var platform = CreatePlatform();
        var context = CreateContext();

        var titles = Enumerable.Range(1, 3)
            .Select(i => CreateTitle(i))
            .ToList();

        foreach (var title in titles)
        {
            _contextFactory.CreateAsync(title, platform, Arg.Any<CancellationToken>())
                .Returns(context);
        }

        var provider = new FakeMetadataProvider("providerA", _ =>
            EnrichmentResult.Found("ext", 0.9f, new EnrichmentData { Description = "Batch description" }));

        var orchestrator = CreateOrchestrator(provider);

        var results = new List<(int TitleId, OrchestrationResult Result)>();
        await foreach (var pair in orchestrator.EnrichStreamAsync(ToAsyncStream(titles, platform)))
        {
            results.Add(pair);
        }

        results.Count.ShouldBe(3);
        foreach (var (_, result) in results)
        {
            result.Enriched.ShouldBeTrue();
            result.BestConfidence.ShouldBe(0.9f);
        }

        // All 3 titles' contexts were sent through the provider's streaming interface
        provider.ReceivedContexts.Count.ShouldBe(3);
    }

    [Fact]
    public async Task EnrichStreamAsync_MixedResults_CorrectPerTitle()
    {
        var platform = CreatePlatform();
        var context = CreateContext();

        var title1 = CreateTitle();
        var title2 = CreateTitle(2);

        _contextFactory.CreateAsync(title1, platform, Arg.Any<CancellationToken>())
            .Returns(context);
        _contextFactory.CreateAsync(title2, platform, Arg.Any<CancellationToken>())
            .Returns(context with { TitleName = "Another Game" });

        int callCount = 0;
        var provider = new FakeMetadataProvider("providerA", ctx =>
        {
            callCount++;
            // First call returns found, second returns not found
            return callCount == 1
                ? EnrichmentResult.Found("ext1", 0.9f, new EnrichmentData { Description = "Found" })
                : EnrichmentResult.NotFound();
        });

        var orchestrator = CreateOrchestrator(provider);

        var results = new List<(int TitleId, OrchestrationResult Result)>();
        await foreach (var pair in orchestrator.EnrichStreamAsync(
                           ToAsyncStream([title1, title2], platform)))
        {
            results.Add(pair);
        }

        results.Count.ShouldBe(2);

        var result1 = results.First(r => r.TitleId == title1.Id);
        var result2 = results.First(r => r.TitleId == title2.Id);

        result1.Result.Enriched.ShouldBeTrue();
        result2.Result.Enriched.ShouldBeFalse();
    }

    private static async IAsyncEnumerable<(Title Title, Platform Platform)> ToAsyncStream(
        IEnumerable<Title> titles, Platform platform)
    {
        foreach (var title in titles)
        {
            yield return (title, platform);
        }

        await Task.CompletedTask;
    }
}
