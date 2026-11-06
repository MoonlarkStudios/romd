using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Dashboard;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Jobs.Executors;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public class BulkEnrichmentJobExecutorTests : IDisposable
{
    private readonly List<ServiceProvider> _services = [];

    public void Dispose()
    {
        foreach (var services in _services)
            services.Dispose();
    }

    private readonly IFileStorageService _fileStorage = Substitute.For<IFileStorageService>();
    private readonly IJobNotifier _notifier = Substitute.For<IJobNotifier>();
    private readonly IStatsNotifier _statsNotifier = Substitute.For<IStatsNotifier>();

    private readonly EnrichmentOptions _options = new()
    {
        PersistenceBatchSize = 3,
        MinimumAutoEnrichConfidence = 0.6f
    };

    private readonly ILibraryMaterializationService _materializationService = Substitute.For<ILibraryMaterializationService>();
    private readonly IEnrichmentOrchestrator _orchestrator = Substitute.For<IEnrichmentOrchestrator>();
    private readonly IPlatformRepository _platformRepo = Substitute.For<IPlatformRepository>();
    private readonly ITitleMetadataStore _titleRepo = Substitute.For<ITitleMetadataStore>();
    private readonly ITitleAggregateReader _titleRepoAggregates = Substitute.For<ITitleAggregateReader>();
    private readonly ITitleRowLock _titleLock = Substitute.For<ITitleRowLock>();
    private readonly ITitleEnrichmentStatusReader _enrichmentStatus = Substitute.For<ITitleEnrichmentStatusReader>();

    /// <summary>Selection read models for the given titles; owned IDs carry local payload.</summary>
    private static IReadOnlyList<EnrichmentCandidate> Candidates(IEnumerable<Title> titles, params int[] ownedIds) =>
        titles.Select(title => new EnrichmentCandidate(title.Id, title.Name, ownedIds.Contains(title.Id))).ToList();

    private readonly IAutomaticArtworkService _artwork = Substitute.For<IAutomaticArtworkService>();

    private BulkEnrichmentJobExecutor CreateExecutor(
        ITitleAggregateReader? aggregates = null, ITitleMetadataStore? metadata = null,
        IAutomaticArtworkService? artwork = null)
    {
        var services = new ServiceCollection()
            .AddScoped(_ => new BulkEnrichmentTitleProcessor(
                aggregates ?? _titleRepoAggregates, metadata ?? _titleRepo, _titleLock, _platformRepo,
                _fileStorage, Substitute.For<IUnitOfWork>(), _notifier, _materializationService, Options.Create(_options),
                NullLogger<BulkEnrichmentTitleProcessor>.Instance, artwork ?? _artwork, clock: TimeProvider.System))
            .BuildServiceProvider();
        _services.Add(services);
        return new BulkEnrichmentJobExecutor(
            aggregates ?? _titleRepoAggregates, _enrichmentStatus, _platformRepo, _orchestrator,
            _statsNotifier, Options.Create(_options), NullLogger<BulkEnrichmentJobExecutor>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(), clock: TimeProvider.System);
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
        var job = BulkEnrichmentJob.Create(platformId, "SNES", createdAt: DateTimeOffset.UtcNow);
        job.Start("hangfire-123", startedAt: DateTimeOffset.UtcNow);
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

        _enrichmentStatus.GetCandidatesAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns(Candidates([titleA, titleB, titleC], 2, 3));

        // Return full aggregates on GetAggregateAsync
        _titleRepoAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(CreateTitle(1, "Alpha"));
        _titleRepoAggregates.GetAggregateAsync(2, Arg.Any<CancellationToken>()).Returns(CreateTitle(2, "Beta"));
        _titleRepoAggregates.GetAggregateAsync(3, Arg.Any<CancellationToken>()).Returns(CreateTitle(3, "Charlie"));

        SetupStreamOrchestratorDefault(OrchestrationResult.NotFound);

        var job = CreateJob();
        var executor = CreateExecutor();
        await executor.ExecuteAsync(job, CreateJobContext());

        // Verify owned titles (B, C) were processed before non-owned (A)
        // by checking the call order
        var calls = _titleRepoAggregates.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ITitleAggregateReader.GetAggregateAsync))
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
        _enrichmentStatus.GetCandidatesAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns(Candidates([titleA]));

        // Simulate race condition: title already completed by another job
        var completedTitle = CreateTitle(1, "Alpha", EnrichmentStatus.Completed);
        _titleRepoAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(completedTitle);

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
        _enrichmentStatus.GetCandidatesAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns(Candidates(titles));

        foreach (var t in titles)
        {
            _titleRepoAggregates.GetAggregateAsync(t.Id, Arg.Any<CancellationToken>())
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
        _titleRepo.ApplyEnrichmentStagedAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>())
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
        _enrichmentStatus.GetCandidatesAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns(Candidates([titleA, titleB]));

        _titleRepoAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(CreateTitle(1, "Alpha"));
        _titleRepoAggregates.GetAggregateAsync(2, Arg.Any<CancellationToken>()).Returns(CreateTitle(2, "Beta"));

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
        _enrichmentStatus.GetCandidatesAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns(Candidates([titleA]));

        _titleRepoAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(CreateTitle(1, "Alpha"));

        // Confidence 0.3 is below MinimumAutoEnrichConfidence of 0.6
        SetupStreamOrchestratorDefault(OrchestrationResult.Found(0.3f));

        var job = CreateJob();
        var executor = CreateExecutor();
        await executor.ExecuteAsync(job, CreateJobContext());

        // Title should be saved with LowConfidence status
        await _titleRepo.Received(1).UpdateMaterializedMetadataStagedAsync(
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
        _enrichmentStatus.GetCandidatesAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns(Candidates([titleA]));

        _titleRepoAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(CreateTitle(1, "Alpha"));

        SetupStreamOrchestratorDefault(OrchestrationResult.Failed);

        var job = CreateJob();
        var executor = CreateExecutor();
        await executor.ExecuteAsync(job, CreateJobContext());

        // Title should be saved with Failed status
        await _titleRepo.Received(1).ApplyEnrichmentStagedAsync(
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
        _enrichmentStatus.GetCandidatesAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns(Candidates(titles));

        foreach (var t in titles)
        {
            _titleRepoAggregates.GetAggregateAsync(t.Id, Arg.Any<CancellationToken>())
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
        _enrichmentStatus.GetCandidatesAsync(
                10,
                Arg.Any<EnrichmentScope>(),
                Arg.Any<CancellationToken>())
            .Returns(Candidates([title]));
        _titleRepoAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(title);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ProcessedWindows_ReleaseAggregatesIncludingPersistenceFailures(bool failPersistence)
    {
        const int windowSize = EnrichmentOrchestrator.StreamWindowSize;
        const int titleCount = windowSize * 3 + 1;
        var references = new Dictionary<int, WeakReference<Title>>();
        _platformRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(CreatePlatform());
        _enrichmentStatus.GetCandidatesAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, titleCount)
                .Select(id => new EnrichmentCandidate(id, $"Title{id:D4}", false)).ToList());
        int checkedWindows = 0;
        _orchestrator.EnrichStreamAsync(Arg.Any<IAsyncEnumerable<(Title, Platform)>>(), Arg.Any<CancellationToken>())
            .Returns(call => WindowedResults(call.ArgAt<IAsyncEnumerable<(Title, Platform)>>(0),
                _ => OrchestrationResult.NotFound, window =>
                {
                    if (window == 0)
                        return;
                    AssertPreviousWindowsCollected(references, window * windowSize);
                    checkedWindows++;
                }));

        var job = CreateJob();
        // These fakes do not retain returned or received aggregates in mock call history.
        await CreateExecutor(new TransientTitleReader(references), new TransientMetadataStore(failPersistence),
            new TransientArtwork()).ExecuteAsync(job, CreateJobContext());

        checkedWindows.ShouldBe(3);
        job.FailedCount.ShouldBe(failPersistence ? titleCount / 2 : 0);
        job.ProcessedCount.ShouldBe(titleCount);
        job.NotFoundCount.ShouldBe(failPersistence ? titleCount - titleCount / 2 : titleCount);
        job.EnrichedCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_ReplacementMedia_UsesOriginalSnapshotAfterReload()
    {
        _platformRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(CreatePlatform());
        _enrichmentStatus.GetCandidatesAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns([new EnrichmentCandidate(1, "Alpha", false)]);
        var original = CreateTitle(1, "Alpha");
        original.AddMedia(TitleMedia.CreateNew(1, MediaType.Cover, 11, "provider", "image/png", createdAt: DateTimeOffset.UnixEpoch));
        original.AddMedia(TitleMedia.CreateNew(1, MediaType.Logo, 12, "provider", "image/png", createdAt: DateTimeOffset.UnixEpoch));
        var reloaded = CreateTitle(1, "Alpha");
        reloaded.AddMedia(TitleMedia.CreateNew(1, MediaType.Cover, 13, "provider", "image/png", createdAt: DateTimeOffset.UnixEpoch));
        reloaded.AddMedia(TitleMedia.CreateNew(1, MediaType.Logo, 12, "provider", "image/png", createdAt: DateTimeOffset.UnixEpoch));
        _titleRepoAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(original, reloaded);
        SetupStreamOrchestratorDefault(OrchestrationResult.Found(0.9f));

        await CreateExecutor().ExecuteAsync(CreateJob(), CreateJobContext());

        await _fileStorage.Received(1).DeleteIfUnreferencedAsync(11, Arg.Any<CancellationToken>());
        await _fileStorage.DidNotReceive().DeleteIfUnreferencedAsync(12, Arg.Any<CancellationToken>());
        await _fileStorage.DidNotReceive().DeleteIfUnreferencedAsync(13, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancellation")]
    [InlineData("ownership")]
    [InlineData("deferral")]
    public async Task ExecuteAsync_ResultProcessingStopsOrFails_DisposesMediaAndInputStream(string outcome)
    {
        using var cancellation = new CancellationTokenSource();
        _platformRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(CreatePlatform());
        _enrichmentStatus.GetCandidatesAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns([new EnrichmentCandidate(1, "Alpha", false), new EnrichmentCandidate(2, "Beta", false)]);
        _titleRepoAggregates.GetAggregateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => CreateTitle(call.ArgAt<int>(0), $"Title{call.ArgAt<int>(0)}"));
        var staged = Substitute.For<IStagedFile>();
        var download = Substitute.For<IAsyncDisposable>();
        var result = OrchestrationResult.NotFound with
        {
            StagedMedia = [new StagedTitleMedia(MediaType.Cover, "provider", "image/png", "https://images/cover",
                staged, download)]
        };
        Exception? expected = outcome switch
        {
            "failure" => new IOException("artwork failed"),
            "cancellation" => new OperationCanceledException(cancellation.Token),
            "ownership" => new JobExecutionOwnershipLostException(),
            _ => null
        };
        _artwork.FillAsync(Arg.Any<Title>(), Arg.Any<Platform>(), Arg.Any<JobContext>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<ProviderArtworkResult>?>())
            .Returns(call =>
            {
                if (call.ArgAt<Title>(0).Id != 1)
                    return Task.CompletedTask;
                if (outcome == "deferral")
                    call.ArgAt<JobContext>(2).RequestDeferral(DateTimeOffset.UtcNow.AddMinutes(1));
                if (outcome == "cancellation")
                    cancellation.Cancel();
                return expected is null ? Task.CompletedTask : Task.FromException(expected);
            });
        bool streamDisposed = false;
        int yieldedResults = 0;
        _orchestrator.EnrichStreamAsync(Arg.Any<IAsyncEnumerable<(Title, Platform)>>(), Arg.Any<CancellationToken>())
            .Returns(call => WindowedResults(call.ArgAt<IAsyncEnumerable<(Title, Platform)>>(0), id =>
            {
                yieldedResults++;
                return id == 1 ? result : OrchestrationResult.NotFound;
            }, _ => { }, () => streamDisposed = true));
        var job = CreateJob();
        var context = CreateJobContext(cancellation.Token);

        Exception? actual = null;
        try
        {
            await CreateExecutor().ExecuteAsync(job, context);
        }
        catch (Exception ex)
        {
            actual = ex;
        }

        if (outcome is "cancellation" or "ownership")
            actual.ShouldBeSameAs(expected);
        else
            actual.ShouldBeNull();
        streamDisposed.ShouldBeTrue();
        _ = staged.Received(1).DisposeAsync();
        _ = download.Received(1).DisposeAsync();
        yieldedResults.ShouldBe(outcome == "failure" ? 2 : 1);
        job.FailedCount.ShouldBe(outcome == "failure" ? 1 : 0);
        (context.DeferredUntil is not null).ShouldBe(outcome == "deferral");
    }

    private static async IAsyncEnumerable<(int TitleId, OrchestrationResult Result)> WindowedResults(
        IAsyncEnumerable<(Title, Platform)> stream,
        Func<int, OrchestrationResult> result,
        Action<int> onWindowLoaded,
        Action? onDispose = null)
    {
        await using var input = stream.GetAsyncEnumerator();
        try
        {
            for (int window = 0; ; window++)
            {
                // Store IDs only so this fake does not itself extend aggregate lifetime.
                var ids = await ReadWindowIdsAsync(input);
                if (ids.Count == 0)
                    yield break;
                onWindowLoaded(window);
                foreach (int id in ids)
                    yield return (id, result(id));
            }
        }
        finally
        {
            onDispose?.Invoke();
        }
    }

    private static async Task<List<int>> ReadWindowIdsAsync(IAsyncEnumerator<(Title Title, Platform Platform)> input)
    {
        var ids = new List<int>();
        while (ids.Count < EnrichmentOrchestrator.StreamWindowSize && await input.MoveNextAsync())
            ids.Add(input.Current.Title.Id);
        return ids;
    }

    private sealed class TransientTitleReader(Dictionary<int, WeakReference<Title>> references) : ITitleAggregateReader
    {
        public Task<Title?> GetAggregateAsync(int titleId, CancellationToken cancellationToken = default)
        {
            var title = CreateTitle(titleId, $"Title{titleId:D4}");
            references[titleId] = new WeakReference<Title>(title);
            return Task.FromResult<Title?>(title);
        }
    }

    private sealed class TransientMetadataStore(bool failPersistence) : ITitleMetadataStore
    {
        public Task ApplyEnrichmentStagedAsync(Title title, CancellationToken cancellationToken = default) =>
            failPersistence && title.Id % 2 == 0
                ? Task.FromException(new IOException("metadata write failed"))
                : Task.CompletedTask;

        public Task UpdateMaterializedMetadataStagedAsync(Title title, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task UpdateUserMetadataStagedAsync(Title title, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TransientArtwork : IAutomaticArtworkService
    {
        public Task FillAsync(Title title, Platform platform, JobContext context, CancellationToken ct,
            IReadOnlyList<ProviderArtworkResult>? results = null) => Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertPreviousWindowsCollected(IReadOnlyDictionary<int, WeakReference<Title>> references,
        int previousTitleCount)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        // The most recent loop value may remain in an async state machine. All earlier
        // aggregates must be collectible while the job is still processing later windows.
        foreach (var (id, reference) in references.Where(pair => pair.Key < previousTitleCount))
            reference.TryGetTarget(out _).ShouldBeFalse($"completed title {id} remained alive across windows");
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
