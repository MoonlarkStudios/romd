using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Jobs.Executors;
using Shouldly;
using Xunit;
namespace Romd.Infrastructure.Tests.Artwork;

public sealed class ArtworkCompletionJobExecutorTests
{
    [Theory]
    [InlineData("Artwork")]
    [InlineData("Enrich")]
    public async Task Execute_DeferredTitle_RemainsPendingWithoutFalseFailure(string mode)
    {
        var store = Substitute.For<IArtworkCompletionStore>();
        var titles = Substitute.For<ITitleAggregateReader>();
        var platforms = Substitute.For<IPlatformRepository>();
        var artwork = Substitute.For<IAutomaticArtworkService>();
        var pipeline = Substitute.For<Romd.Admin.Application.Titles.Enrichment.ITitleEnrichmentPipeline>();
        var job = ArtworkCompletionJob.Create(Guid.NewGuid(), 1, null, createdAt: DateTimeOffset.UtcNow);
        store.NextAsync(job.Id, default).Returns(new ArtworkCompletionWork(1, "Game", false, mode));
        titles.GetAggregateAsync(1, default).Returns(Title.Rehydrate(1, 10, "Game", "game", null, null, null, null, null, null, null, EnrichmentStatus.None, null, DateTimeOffset.UtcNow));
        platforms.GetByIdAsync(10, default).Returns(Platform.Rehydrate(10, "Test", "test", "Test", DateTimeOffset.UtcNow));
        var context = new JobContext(null, _ => Task.CompletedTask, default, jobId: job.Id);
        Task Defer()
        {
            context.RequestDeferral(DateTimeOffset.UtcNow.AddMinutes(2));
            return Task.CompletedTask;
        }
        artwork.FillAsync(Arg.Any<Title>(), Arg.Any<Platform>(), context, default).Returns(_ => Defer());
        pipeline.EnrichAsync(1, 10, context).Returns(_ => Defer());

        await new ArtworkCompletionJobExecutor(store, titles, platforms, artwork,
            NullLogger<ArtworkCompletionJobExecutor>.Instance, clock: TimeProvider.System, pipeline).ExecuteAsync(job, context);

        job.Processed.ShouldBe(0);
        job.Errors.ShouldBeEmpty();
        await store.DidNotReceive().CompleteAsync(Arg.Any<Guid>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyDictionary<ArtworkRole, ArtworkCompletionOutcome>>(), Arg.Any<MetadataExecutionOutcome>(), context, default);
        await store.DidNotReceive().OutcomesAsync(Arg.Any<Guid>(), Arg.Any<int>(), default);
    }

    [Fact]
    public async Task Execute_FailureDoesNotPreventLaterTitle_AndUntrackedTitleIsSkipped()
    {
        var store = Substitute.For<IArtworkCompletionStore>();
        var titles = Substitute.For<ITitleAggregateReader>();
        var platforms = Substitute.For<IPlatformRepository>();
        var artwork = Substitute.For<IAutomaticArtworkService>();
        var job = ArtworkCompletionJob.Create(Guid.NewGuid(), 3, null, createdAt: DateTimeOffset.UtcNow);
        var work = new Queue<ArtworkCompletionWork>([new(1, "Failure", false), new(2, "Untracked", true), new(3, "Success", false)]);
        store.NextAsync(job.Id, default).ReturnsForAnyArgs(_ => Task.FromResult(work.Count > 0 ? work.Dequeue() : null));
        titles.GetAggregateAsync(Arg.Any<int>(), default).ReturnsForAnyArgs(call => Title.Rehydrate(call.Arg<int>(), 10, "Game", "game", null, null, null, null, null, null, null, EnrichmentStatus.None, null, DateTimeOffset.UtcNow));
        platforms.GetByIdAsync(10, default).ReturnsForAnyArgs(Platform.Rehydrate(10, "Test", "test", "Test", DateTimeOffset.UtcNow));
        artwork.FillAsync(Arg.Is<Title>(t => t.Id == 1), Arg.Any<Platform>(), Arg.Any<JobContext>(), default).Returns(Task.FromException(new IOException("Provider failed")));
        store.OutcomesAsync(job.Id, 3, default).Returns(Enum.GetValues<ArtworkRole>().ToDictionary(x => x, _ => ArtworkCompletionOutcome.Updated));
        var completed = new Dictionary<int, IReadOnlyDictionary<ArtworkRole, ArtworkCompletionOutcome>>();
        var metadata = new Dictionary<int, MetadataExecutionOutcome>();
        store.CompleteAsync(job.Id, Arg.Any<int>(), Arg.Any<IReadOnlyDictionary<ArtworkRole, ArtworkCompletionOutcome>>(), Arg.Any<MetadataExecutionOutcome>(), Arg.Any<JobContext>(), default).Returns(call =>
        {
            completed[call.Arg<int>()] = call.Arg<IReadOnlyDictionary<ArtworkRole, ArtworkCompletionOutcome>>();
            metadata[call.Arg<int>()] = call.Arg<MetadataExecutionOutcome>();
            return Task.CompletedTask;
        });
        store.CompletedCountAsync(job.Id, default).Returns(_ => completed.Count);
        await new ArtworkCompletionJobExecutor(store, titles, platforms, artwork, NullLogger<ArtworkCompletionJobExecutor>.Instance, clock: TimeProvider.System).ExecuteAsync(job, new JobContext(null, _ => Task.CompletedTask, default, jobId: job.Id));
        completed[1].Values.ShouldAllBe(x => x == ArtworkCompletionOutcome.Failed);
        completed[2].Values.ShouldAllBe(x => x == ArtworkCompletionOutcome.Untracked);
        completed[3].Values.ShouldAllBe(x => x == ArtworkCompletionOutcome.Updated);
        // Artwork mode, skipped titles and failed artwork never ran a metadata step.
        metadata.Values.ShouldAllBe(x => x == MetadataExecutionOutcome.NotAttempted);
        job.Processed.ShouldBe(3);
        await artwork.DidNotReceive().FillAsync(Arg.Is<Title>(t => t.Id == 2), Arg.Any<Platform>(), Arg.Any<JobContext>(), default);
    }
    [Fact]
    public async Task Execute_FullEnrichmentUsesSharedPipeline_ArtworkModeDoesNot()
    {
        var store = Substitute.For<IArtworkCompletionStore>();
        var titles = Substitute.For<ITitleAggregateReader>();
        var platforms = Substitute.For<IPlatformRepository>();
        var artwork = Substitute.For<IAutomaticArtworkService>();
        var pipeline = Substitute.For<Romd.Admin.Application.Titles.Enrichment.ITitleEnrichmentPipeline>();
        var job = ArtworkCompletionJob.Create(Guid.NewGuid(), 2, null, createdAt: DateTimeOffset.UtcNow);
        var work = new Queue<ArtworkCompletionWork>([new(1, "Full", false, "Enrich"), new(2, "Artwork", false)]);
        store.NextAsync(job.Id, default).ReturnsForAnyArgs(_ => Task.FromResult(work.Count > 0 ? work.Dequeue() : null));
        titles.GetAggregateAsync(Arg.Any<int>(), default).ReturnsForAnyArgs(call => Title.Rehydrate(call.Arg<int>(), 10, "Game", "game", null, null, null, null, null, null, null, EnrichmentStatus.None, null, DateTimeOffset.UtcNow));
        platforms.GetByIdAsync(10, default).ReturnsForAnyArgs(Platform.Rehydrate(10, "Test", "test", "Test", DateTimeOffset.UtcNow));
        store.OutcomesAsync(job.Id, Arg.Any<int>(), default).Returns(Enum.GetValues<ArtworkRole>().ToDictionary(x => x, _ => ArtworkCompletionOutcome.AlreadyPresent));
        var context = new JobContext(null, _ => Task.CompletedTask, default, jobId: job.Id);
        await new ArtworkCompletionJobExecutor(store, titles, platforms, artwork, NullLogger<ArtworkCompletionJobExecutor>.Instance, clock: TimeProvider.System, pipeline).ExecuteAsync(job, context);
        await pipeline.Received(1).EnrichAsync(1, 10, context);
        await pipeline.DidNotReceive().EnrichAsync(2, Arg.Any<int?>(), Arg.Any<JobContext>(), Arg.Any<bool>());
        await artwork.Received(1).FillAsync(Arg.Is<Title>(x => x.Id == 2), Arg.Any<Platform>(), context, default);
        await artwork.DidNotReceive().FillAsync(Arg.Is<Title>(x => x.Id == 1), Arg.Any<Platform>(), context, default);
    }

    [Fact]
    public async Task Execute_EnrichmentStepThrows_FallsBackToFailedMetadata_OtherwiseNotAttempted()
    {
        var store = Substitute.For<IArtworkCompletionStore>();
        var titles = Substitute.For<ITitleAggregateReader>();
        var platforms = Substitute.For<IPlatformRepository>();
        var pipeline = Substitute.For<Romd.Admin.Application.Titles.Enrichment.ITitleEnrichmentPipeline>();
        var job = ArtworkCompletionJob.Create(Guid.NewGuid(), 3, null, createdAt: DateTimeOffset.UtcNow);
        var work = new Queue<ArtworkCompletionWork>([new(1, "Throws", false, "Enrich"), new(2, "Recorded", false, "Enrich"), new(3, "Lookup fails", false, "Enrich")]);
        store.NextAsync(job.Id, default).ReturnsForAnyArgs(_ => Task.FromResult(work.Count > 0 ? work.Dequeue() : null));
        titles.GetAggregateAsync(Arg.Any<int>(), default).ReturnsForAnyArgs(call => call.Arg<int>() == 3
            ? Task.FromException<Title?>(new IOException("Database unavailable"))
            : Task.FromResult<Title?>(Title.Rehydrate(call.Arg<int>(), 10, "Game", "game", null, null, null, null, null, null, null, EnrichmentStatus.None, null, DateTimeOffset.UtcNow)));
        platforms.GetByIdAsync(10, default).ReturnsForAnyArgs(Platform.Rehydrate(10, "Test", "test", "Test", DateTimeOffset.UtcNow));
        var context = new JobContext(null, _ => Task.CompletedTask, default, jobId: job.Id);
        pipeline.EnrichAsync(1, 10, context).Returns(Task.FromException(new InvalidOperationException("No enabled, configured metadata providers are available.")));
        store.OutcomesAsync(job.Id, 2, default).Returns(Enum.GetValues<ArtworkRole>().ToDictionary(x => x, _ => ArtworkCompletionOutcome.Updated));
        var metadata = new Dictionary<int, MetadataExecutionOutcome>();
        store.CompleteAsync(job.Id, Arg.Any<int>(), Arg.Any<IReadOnlyDictionary<ArtworkRole, ArtworkCompletionOutcome>>(), Arg.Any<MetadataExecutionOutcome>(), context, default)
            .Returns(call => { metadata[call.Arg<int>()] = call.Arg<MetadataExecutionOutcome>(); return Task.CompletedTask; });

        await new ArtworkCompletionJobExecutor(store, titles, platforms, Substitute.For<IAutomaticArtworkService>(),
            NullLogger<ArtworkCompletionJobExecutor>.Instance, clock: TimeProvider.System, pipeline).ExecuteAsync(job, context);

        // An exception inside the metadata step is a failed execution. A finished step recorded its own outcome,
        // which the NotAttempted fallback cannot lower; a failure before the step leaves it not attempted.
        metadata[1].ShouldBe(MetadataExecutionOutcome.Failed);
        metadata[2].ShouldBe(MetadataExecutionOutcome.NotAttempted);
        metadata[3].ShouldBe(MetadataExecutionOutcome.NotAttempted);
        await pipeline.DidNotReceive().EnrichAsync(3, Arg.Any<int?>(), Arg.Any<JobContext>(), Arg.Any<bool>());
    }

}
