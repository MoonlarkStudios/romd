using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
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
using Romd.Application.Common.Security;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class BulkEnrichmentPersistenceLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ThousandResults_BoundsTrackingPreservesParentStateAndFlowsFenceAndActor(bool failWrites)
    {
        // Real EF trackers prove scope lifetime without a database connection. Publication's SQL,
        // revision and rollback behavior is exercised separately by the queue-safety tests.
        const int titleCount = 1000;
        var actor = Guid.NewGuid();
        var job = BulkEnrichmentJob.Create(10, "SNES", createdAt: DateTimeOffset.UtcNow, createdByUserId: actor);
        job.Start("bulk-lifetime-test", startedAt: DateTimeOffset.UtcNow);
        var token = Guid.NewGuid();
        var observations = new TrackingObservations(job.Id, token, actor, failWrites);
        var dbOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options;
        await using var parentDb = new RomdDbContext(dbOptions);
        parentDb.Titles.Add(new TitleEntity { Id = titleCount + 1, Name = "Pending parent", NormalizedName = "pending-parent" });
        var reader = new TitleReader();
        var platforms = Substitute.For<IPlatformRepository>();
        platforms.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(
            Platform.Rehydrate(10, "Super Nintendo", "snes", "Nintendo", DateTimeOffset.UtcNow));
        var candidates = Substitute.For<ITitleEnrichmentStatusReader>();
        candidates.GetCandidatesAsync(10, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, titleCount).Select(id => new EnrichmentCandidate(id, $"Title{id:D4}", true)).ToArray());
        var options = Options.Create(new EnrichmentOptions());
        await using var services = new ServiceCollection()
            .AddScoped(_ => new RomdDbContext(dbOptions))
            .AddScoped<IAuditContext, JobAuditContext>()
            .AddScoped<ScopeState>()
            .AddScoped<IJobExecutionMutationGuard>(provider => new RecordingGuard(provider.GetRequiredService<ScopeState>(), observations))
            .AddScoped<ITitleMetadataStore>(provider => new TrackingMetadataStore(provider.GetRequiredService<RomdDbContext>(),
                provider.GetRequiredService<IAuditContext>(), provider.GetRequiredService<ScopeState>(), observations))
            .AddScoped(provider => new BulkEnrichmentTitleProcessor(reader,
                provider.GetRequiredService<ITitleMetadataStore>(), Substitute.For<ITitleRowLock>(), platforms,
                Substitute.For<IFileStorageService>(), Substitute.For<IUnitOfWork>(), Substitute.For<IJobNotifier>(), Substitute.For<ILibraryMaterializationService>(),
                options, NullLogger<BulkEnrichmentTitleProcessor>.Instance, Substitute.For<IAutomaticArtworkService>(), clock: TimeProvider.System))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var executor = new BulkEnrichmentJobExecutor(reader, candidates, platforms, new FoundOrchestrator(),
            Substitute.For<IStatsNotifier>(), options, NullLogger<BulkEnrichmentJobExecutor>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(), clock: TimeProvider.System);
        var context = new JobContext(null, _ => Task.CompletedTask, CancellationToken.None,
            ownedMutationDelegate: (_, _) => throw new InvalidOperationException("The job scope cannot own a child context's write."),
            jobId: job.Id);

        using (JobExecutionFenceScope.Enter(job.Id, token, TimeProvider.System))
            await executor.ExecuteAsync(job, context);

        observations.Created.ShouldBe(titleCount);
        observations.Disposed.ShouldBe(titleCount);
        observations.MaximumActive.ShouldBe(1);
        observations.MaximumTracked.ShouldBe(1);
        observations.Active.ShouldBe(0);
        observations.GuardedWrites.ShouldBe(failWrites ? titleCount + titleCount / 2 : titleCount * 2);
        job.FailedCount.ShouldBe(failWrites ? titleCount / 2 : 0);
        parentDb.ChangeTracker.Entries<TitleEntity>().Single().State.ShouldBe(EntityState.Added);
    }

    private sealed class TrackingObservations(Guid jobId, Guid token, Guid actor, bool failWrites)
    {
        public Guid JobId { get; } = jobId;
        public Guid Token { get; } = token;
        public Guid Actor { get; } = actor;
        public bool FailWrites { get; } = failWrites;
        public int Created { get; set; }
        public int Disposed { get; set; }
        public int Active { get; set; }
        public int MaximumActive { get; set; }
        public int MaximumTracked { get; set; }
        public int GuardedWrites { get; set; }
    }

    private sealed class ScopeState
    {
        public bool OwnsMutation { get; set; }
    }

    private sealed class RecordingGuard(ScopeState scope, TrackingObservations observations) : IJobExecutionMutationGuard
    {
        public async Task ExecuteAsync(Guid jobId, Guid fenceToken, Func<CancellationToken, Task> mutation,
            CancellationToken ct = default)
        {
            jobId.ShouldBe(observations.JobId);
            fenceToken.ShouldBe(observations.Token);
            scope.OwnsMutation.ShouldBeFalse();
            scope.OwnsMutation = true;
            observations.GuardedWrites++;
            try
            { await mutation(ct); }
            finally { scope.OwnsMutation = false; }
        }
    }

    private sealed class TrackingMetadataStore : ITitleMetadataStore, IAsyncDisposable
    {
        private readonly RomdDbContext _db;
        private readonly IAuditContext _audit;
        private readonly ScopeState _scope;
        private readonly TrackingObservations _observations;

        public TrackingMetadataStore(RomdDbContext db, IAuditContext audit, ScopeState scope, TrackingObservations observations)
        {
            _db = db;
            _audit = audit;
            _scope = scope;
            _observations = observations;
            observations.Created++;
            observations.MaximumActive = Math.Max(observations.MaximumActive, ++observations.Active);
        }

        public Task ApplyEnrichmentStagedAsync(Title title, CancellationToken cancellationToken = default)
        {
            Track(title);
            return _observations.FailWrites && title.Id % 2 == 0
                ? Task.FromException(new IOException("Publication failed.")) : Task.CompletedTask;
        }

        public Task UpdateMaterializedMetadataStagedAsync(Title title, CancellationToken cancellationToken = default)
        {
            Track(title);
            return Task.CompletedTask;
        }

        public Task UpdateUserMetadataStagedAsync(Title title, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private void Track(Title title)
        {
            _scope.OwnsMutation.ShouldBeTrue();
            _audit.ActorId.ShouldBe(_observations.Actor);
            if (!_db.Titles.Local.Any())
                _db.Attach(new TitleEntity { Id = title.Id, Name = title.Name, NormalizedName = title.NormalizedName });
            _db.Titles.Local.Single().Id.ShouldBe(title.Id);
            _observations.MaximumTracked = Math.Max(_observations.MaximumTracked, _db.ChangeTracker.Entries().Count());
        }

        public ValueTask DisposeAsync()
        {
            _observations.Disposed++;
            _observations.Active--;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TitleReader : ITitleAggregateReader
    {
        public Task<Title?> GetAggregateAsync(int titleId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Title?>(Title.Rehydrate(titleId, 10, $"Title{titleId:D4}", $"title{titleId:D4}",
                null, null, null, null, null, null, null, EnrichmentStatus.Pending, null, DateTimeOffset.UtcNow));
    }

    private sealed class FoundOrchestrator : IEnrichmentOrchestrator
    {
        public Task<OrchestrationResult> EnrichTitleAsync(Title title, Platform platform,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<(int TitleId, OrchestrationResult Result)> EnrichStreamAsync(
            IAsyncEnumerable<(Title Title, Platform Platform)> titles,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var (title, _) in titles.WithCancellation(cancellationToken))
                yield return (title.Id, OrchestrationResult.Found(1));
        }
    }
}
