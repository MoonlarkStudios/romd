using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Dashboard;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Jobs.Executors;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public class BulkEnrichmentJobExecutorTests
{
    private readonly IFileStorageService _fileStorage = Substitute.For<IFileStorageService>();
    private readonly IJobNotifier _notifier = Substitute.For<IJobNotifier>();
    private readonly IStatsNotifier _statsNotifier = Substitute.For<IStatsNotifier>();

    private readonly EnrichmentOptions _options = new()
    {
        PersistenceBatchSize = 3, MinimumAutoEnrichConfidence = 0.6f
    };

    private readonly ILibraryMaterializationService _materializationService = Substitute.For<ILibraryMaterializationService>();
    private readonly IEnrichmentOrchestrator _orchestrator = Substitute.For<IEnrichmentOrchestrator>();
    private readonly IPlatformRepository _platformRepo = Substitute.For<IPlatformRepository>();
    private readonly ITitleRepository _titleRepo = Substitute.For<ITitleRepository>();

    private BulkEnrichmentJobExecutor CreateExecutor()
    {
        return new BulkEnrichmentJobExecutor(
            _titleRepo,
            _platformRepo,
            _orchestrator,
            _fileStorage,
            _notifier,
            _statsNotifier,
            _materializationService,
            Options.Create(_options),
            NullLogger<BulkEnrichmentJobExecutor>.Instance, Substitute.For<Romd.Admin.Application.Artwork.IAutomaticArtworkService>());
    }

    private static Platform CreatePlatform(int id = 10) =>
        Platform.Rehydrate(id, "Super Nintendo", "snes", "Nintendo", DateTimeOffset.UtcNow);

    private static Title CreateTitle(int id, string name, EnrichmentStatus status = EnrichmentStatus.None)
    {
        return Title.Rehydrate(
            id, 10, name,
            name.ToLowerInvariant(),
            null, null, null, null,
            null, null, null,
            status, null,
            DateTimeOffset.UtcNow);
    }

    private static BulkEnrichmentJob CreateJob(int platformId = 10)
    {
        var job = BulkEnrichmentJob.Create(platformId, "SNES");
        job.Start("hangfire-123");
        return job;
    }

    private static JobContext CreateJobContext(CancellationToken ct = default) =>
        new(null, _ => Task.CompletedTask, ct);

    private void SetupStreamOrchestrator(
        Func<List<(Title, Platform)>, Dictionary<int, OrchestrationResult>> resultFactory)
    {
        _orchestrator.EnrichStreamAsync(
                Arg.Any<IAsyncEnumerable<(Title, Platform)>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var stream = callInfo.ArgAt<IAsyncEnumerable<(Title, Platform)>>(0);
                return ConsumeAndReturn(stream, resultFactory);
            });
    }

    private void SetupStreamOrchestratorDefault(OrchestrationResult defaultResult)
    {
        SetupStreamOrchestrator(items =>
            items.ToDictionary(b => b.Item1.Id, _ => defaultResult));
    }

    private static async IAsyncEnumerable<(int TitleId, OrchestrationResult Result)> ConsumeAndReturn(
        IAsyncEnumerable<(Title, Platform)> stream,
        Func<List<(Title, Platform)>, Dictionary<int, OrchestrationResult>> resultFactory)
    {
        var items = new List<(Title, Platform)>();
        await foreach (var pair in stream)
        {
            items.Add(pair);
        }

        var results = resultFactory(items);
        foreach ((int titleId, var result) in results)
        {
            yield return (titleId, result);
        }
    }

    [Fact]
    public async Task ExecuteAsync_OwnedTitlesProcessedFirst()
    {
        var platform = CreatePlatform();
        _platformRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(platform);

        var titleA = CreateTitle(1, "Alpha");
        var titleB = CreateTitle(2, "Beta");
        var titleC = CreateTitle(3, "Charlie");

        _titleRepo.GetNeedingEnrichmentByPlatformAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns([titleA, titleB, titleC]);

        // Only title B and C are owned
        _titleRepo.GetTitleIdsWithLocalPayloadAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<int> { 2, 3 });

        // Return full titles on GetWithMetadataLayersAsync
        _titleRepo.GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>()).Returns(CreateTitle(1, "Alpha"));
        _titleRepo.GetWithMetadataLayersAsync(2, Arg.Any<CancellationToken>()).Returns(CreateTitle(2, "Beta"));
        _titleRepo.GetWithMetadataLayersAsync(3, Arg.Any<CancellationToken>()).Returns(CreateTitle(3, "Charlie"));

        SetupStreamOrchestratorDefault(OrchestrationResult.NotFound);

        var job = CreateJob();
        var executor = CreateExecutor();
        await executor.ExecuteAsync(job, CreateJobContext());

        // Verify owned titles (B, C) were processed before non-owned (A)
        // by checking the call order
        var calls = _titleRepo.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "GetWithMetadataLayersAsync")
            .Select(c => (int)c.GetArguments()[0]!)
            .ToList();

        // Owned titles (2, 3) should come before non-owned (1)
        int indexOfTitle1 = calls.IndexOf(1);
        int indexOfTitle2 = calls.IndexOf(2);
        int indexOfTitle3 = calls.IndexOf(3);

        indexOfTitle2.ShouldBeLessThan(indexOfTitle1);
        indexOfTitle3.ShouldBeLessThan(indexOfTitle1);

        // Nothing enriched (all NotFound) → no storage broadcast.
        await _statsNotifier.DidNotReceive().NotifyStorageChangedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SkipsAlreadyEnrichedTitles()
    {
        var platform = CreatePlatform();
        _platformRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(platform);

        var titleA = CreateTitle(1, "Alpha");
        _titleRepo.GetNeedingEnrichmentByPlatformAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns([titleA]);
        _titleRepo.GetTitleIdsWithLocalPayloadAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<int>());

        // Simulate race condition: title already completed by another job
        var completedTitle = CreateTitle(1, "Alpha", EnrichmentStatus.Completed);
        _titleRepo.GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>()).Returns(completedTitle);

        // Orchestrator should not be called since the title is skipped
        SetupStreamOrchestratorDefault(OrchestrationResult.NotFound);

        var job = CreateJob();
        var executor = CreateExecutor();
        await executor.ExecuteAsync(job, CreateJobContext());

        // Orchestrator was called but the stream yielded no titles (all filtered out)
        // Verify no titles were actually enriched
        await _orchestrator.DidNotReceive()
            .EnrichTitleAsync(Arg.Any<Title>(), Arg.Any<Platform>(), Arg.Any<CancellationToken>());

        job.SkippedCount.ShouldBe(1);
        job.ProcessedCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_CheckpointsAtConfiguredFrequency()
    {
        var platform = CreatePlatform();
        _platformRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(platform);

        // Create 5 titles, batch size = 3
        var titles = Enumerable.Range(1, 5)
            .Select(i => CreateTitle(i, $"Title{i}"))
            .ToList();
        _titleRepo.GetNeedingEnrichmentByPlatformAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns(titles);
        _titleRepo.GetTitleIdsWithLocalPayloadAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<int>());

        foreach (var t in titles)
        {
            _titleRepo.GetWithMetadataLayersAsync(t.Id, Arg.Any<CancellationToken>())
                .Returns(CreateTitle(t.Id, t.Name));
        }

        SetupStreamOrchestratorDefault(OrchestrationResult.NotFound);

        int checkpointCount = 0;
        var context = new JobContext(null, _ =>
        {
            checkpointCount++;
            return Task.CompletedTask;
        }, CancellationToken.None);

        var job = CreateJob();
        var executor = CreateExecutor();
        await executor.ExecuteAsync(job, context);

        // Initial checkpoint (SetTotalTitles) + final checkpoint
        // With BatchSize=10 and PersistenceBatchSize=3, all 5 titles fit in 1 batch (chunk of 5)
        // After processing 5 titles: 5 >= 3 → checkpoint, then final checkpoint for remaining 0
        // Actually: processedSinceCheckpoint = 5 >= 3 → checkpoint, then processedSinceCheckpoint=0 → no final
        // Total: initial + after-chunk = 2
        checkpointCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task ExecuteAsync_NotifiesForEnrichedTitles()
    {
        bool insideOwnedMutation = false;
        _titleRepo.UpdateAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                insideOwnedMutation.ShouldBeTrue();
                return Task.CompletedTask;
            });
        _materializationService.FlagAffectedLibrariesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                insideOwnedMutation.ShouldBeTrue();
                return Task.CompletedTask;
            });
        var platform = CreatePlatform();
        _platformRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(platform);

        var titleA = CreateTitle(1, "Alpha");
        var titleB = CreateTitle(2, "Beta");
        _titleRepo.GetNeedingEnrichmentByPlatformAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns([titleA, titleB]);
        _titleRepo.GetTitleIdsWithLocalPayloadAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<int>());

        _titleRepo.GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>()).Returns(CreateTitle(1, "Alpha"));
        _titleRepo.GetWithMetadataLayersAsync(2, Arg.Any<CancellationToken>()).Returns(CreateTitle(2, "Beta"));

        // Title A enriched, Title B not found
        SetupStreamOrchestrator(items =>
        {
            var results = new Dictionary<int, OrchestrationResult>();
            foreach (var (title, _) in items)
            {
                results[title.Id] = title.Id == 1
                    ? OrchestrationResult.Found(0.9f)
                    : OrchestrationResult.NotFound;
            }

            return results;
        });

        var job = CreateJob();
        var executor = CreateExecutor();
        var context = new JobContext(
            null,
            _ => Task.CompletedTask,
            CancellationToken.None,
            ownedMutationDelegate: async (mutation, mutationCt) =>
            {
                insideOwnedMutation = true;
                try
                {
                    await mutation(mutationCt);
                }
                finally
                {
                    insideOwnedMutation = false;
                }
            });
        await executor.ExecuteAsync(job, context);

        // Notified for enriched title only
        await _notifier.Received(1).NotifyTitleEnrichedAsync(
            1, 10, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _notifier.DidNotReceive().NotifyTitleEnrichedAsync(
            2, Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());

        // Storage stats refreshed once for the whole job since media may have changed.
        await _statsNotifier.Received(1).NotifyStorageChangedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SetsLowConfidenceWhenBelowThreshold()
    {
        var platform = CreatePlatform();
        _platformRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(platform);

        var titleA = CreateTitle(1, "Alpha");
        _titleRepo.GetNeedingEnrichmentByPlatformAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns([titleA]);
        _titleRepo.GetTitleIdsWithLocalPayloadAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<int>());

        _titleRepo.GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>()).Returns(CreateTitle(1, "Alpha"));

        // Confidence 0.3 is below MinimumAutoEnrichConfidence of 0.6
        SetupStreamOrchestratorDefault(OrchestrationResult.Found(0.3f));

        var job = CreateJob();
        var executor = CreateExecutor();
        await executor.ExecuteAsync(job, CreateJobContext());

        // Title should be saved with LowConfidence status
        await _titleRepo.Received(1).UpdateAsync(
            Arg.Is<Title>(t => t.EnrichmentStatus == EnrichmentStatus.LowConfidence),
            Arg.Any<CancellationToken>());

        job.EnrichedCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_AllProvidersErrored_RecordsFailed()
    {
        var platform = CreatePlatform();
        _platformRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(platform);

        var titleA = CreateTitle(1, "Alpha");
        _titleRepo.GetNeedingEnrichmentByPlatformAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns([titleA]);
        _titleRepo.GetTitleIdsWithLocalPayloadAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<int>());

        _titleRepo.GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>()).Returns(CreateTitle(1, "Alpha"));

        SetupStreamOrchestratorDefault(OrchestrationResult.Failed);

        var job = CreateJob();
        var executor = CreateExecutor();
        await executor.ExecuteAsync(job, CreateJobContext());

        // Title should be saved with Failed status
        await _titleRepo.Received(1).UpdateAsync(
            Arg.Is<Title>(t => t.EnrichmentStatus == EnrichmentStatus.Failed),
            Arg.Any<CancellationToken>());

        job.FailedCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_UsesStreamOrchestration()
    {
        var platform = CreatePlatform();
        _platformRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(platform);

        var titles = Enumerable.Range(1, 3)
            .Select(i => CreateTitle(i, $"Title{i}"))
            .ToList();
        _titleRepo.GetNeedingEnrichmentByPlatformAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns(titles);
        _titleRepo.GetTitleIdsWithLocalPayloadAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<int>());

        foreach (var t in titles)
        {
            _titleRepo.GetWithMetadataLayersAsync(t.Id, Arg.Any<CancellationToken>())
                .Returns(CreateTitle(t.Id, t.Name));
        }

        SetupStreamOrchestratorDefault(OrchestrationResult.Found(0.9f));

        var job = CreateJob();
        var executor = CreateExecutor();
        await executor.ExecuteAsync(job, CreateJobContext());

        // Verify EnrichStreamAsync was called (not EnrichTitleAsync)
        _orchestrator.Received(1).EnrichStreamAsync(
            Arg.Any<IAsyncEnumerable<(Title, Platform)>>(),
            Arg.Any<CancellationToken>());

        // EnrichTitleAsync should NOT have been called
        await _orchestrator.DidNotReceive()
            .EnrichTitleAsync(Arg.Any<Title>(), Arg.Any<Platform>(), Arg.Any<CancellationToken>());

        job.EnrichedCount.ShouldBe(3);
    }

    [Fact]
    public async Task ExecuteAsync_CrashBeforeCheckpoint_RedeliverySkipsTitleWithLibraryAlreadyInvalidated()
    {
        var platform = CreatePlatform();
        var title = CreateTitle(1, "Alpha");
        _platformRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(platform);
        _titleRepo.GetNeedingEnrichmentByPlatformAsync(
                10,
                Arg.Any<EnrichmentScope>(),
                Arg.Any<CancellationToken>())
            .Returns([title]);
        _titleRepo.GetTitleIdsWithLocalPayloadAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<int>());
        _titleRepo.GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>()).Returns(title);
        _orchestrator.EnrichStreamAsync(
                Arg.Any<IAsyncEnumerable<(Title, Platform)>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => EnrichEveryYieldedTitle(
                call.ArgAt<IAsyncEnumerable<(Title, Platform)>>(0)));
        var executor = CreateExecutor();
        var job = CreateJob();
        int checkpoint = 0;
        var crashingContext = new JobContext(
            null,
            _ => ++checkpoint == 1
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("crash before checkpoint")),
            CancellationToken.None,
            ownedMutationDelegate: (mutation, mutationCt) => mutation(mutationCt));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(job, crashingContext));

        title.EnrichmentStatus.ShouldBe(EnrichmentStatus.Completed);
        await _materializationService.Received(1)
            .FlagAffectedLibrariesAsync(10, Arg.Any<CancellationToken>());

        var redelivery = CreateJob();
        await executor.ExecuteAsync(redelivery, CreateJobContext());

        redelivery.SkippedCount.ShouldBe(1);
        await _materializationService.Received(1)
            .FlagAffectedLibrariesAsync(10, Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<(int TitleId, OrchestrationResult Result)> EnrichEveryYieldedTitle(
        IAsyncEnumerable<(Title, Platform)> stream)
    {
        await foreach ((var title, _) in stream)
        {
            yield return (title.Id, OrchestrationResult.Found(0.9f));
        }
    }
}
