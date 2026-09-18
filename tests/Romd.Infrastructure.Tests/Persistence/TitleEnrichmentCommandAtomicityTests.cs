using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles.Commands.TriggerTitleEnrichment;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class TitleEnrichmentCommandAtomicityTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TriggerEnrichment_Success_CommitsPendingJobAndPreservesCurationState()
    {
        await using var database = await TestDatabase.CreateAsync();
        var enqueuer = Substitute.For<IEnrichmentJobEnqueuer>();
        var handler = Handler(database, enqueuer);

        var result = await handler.HandleAsync(new TriggerTitleEnrichmentCommand(7));

        result.IsError.ShouldBeFalse();
        await using var read = database.CreateReadContext();
        var title = await read.Titles.SingleAsync();
        title.EnrichmentStatus.ShouldBe(EnrichmentStatus.Pending.ToString());
        title.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        title.ScreenshotPrefsJson.ShouldBe("""{"preferred":"gameplay"}""");
        title.FieldProvenanceJson.ShouldBe("""{"Description":"igdb"}""");
        title.FieldSourceOverridesJson.ShouldBe("""{"Genre":"user"}""");
        (await read.TrackedTitles.AnyAsync(tracked => tracked.TitleId == title.Id)).ShouldBeTrue();
        var rating = await read.TitleContentRatings.SingleAsync();
        rating.Code.ShouldBe("E");
        rating.SourceId.ShouldBe("igdb");
        var media = await read.TitleMedia.SingleAsync();
        media.FileId.ShouldBe(11);
        media.IsPrimary.ShouldBeTrue();
        var job = await read.Jobs.OfType<EnrichmentJobEntity>().SingleAsync();
        job.Id.ShouldBe(result.Value);
        job.TitleId.ShouldBe(7);
        job.Phase.ShouldBe(EnrichmentJobPhase.Pending.ToString());
        job.HangfireJobId.ShouldBeNull();
        job.CreatedAt.ShouldBe(FixedNow);
        await enqueuer.Received(1).EnqueueAsync(result.Value, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerEnrichment_CommitFailure_RollsBackTitleAndJob()
    {
        await using var database = await TestDatabase.CreateAsync(failCommit: true);
        var enqueuer = Substitute.For<IEnrichmentJobEnqueuer>();
        var handler = Handler(database, enqueuer);

        var result = await handler.HandleAsync(new TriggerTitleEnrichmentCommand(7));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Enrichment.RequestFailed");
        await AssertNoEnrichmentEffectsAsync(database);
        await enqueuer.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default);
    }

    [Fact]
    public async Task TriggerEnrichment_CancelledAtCommit_RollsBackAndPropagates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var enqueuer = Substitute.For<IEnrichmentJobEnqueuer>();
        using var cancellation = new CancellationTokenSource();
        var handler = new TriggerTitleEnrichmentCommandHandler(
            database.TitleRepository,
            database.JobRepository,
            enqueuer,
            new CancelAtCommitUnitOfWork(database.UnitOfWork, cancellation),
            new FixedTimeProvider(FixedNow),
            NullLogger<TriggerTitleEnrichmentCommandHandler>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.HandleAsync(new TriggerTitleEnrichmentCommand(7), cancellation.Token));

        await AssertNoEnrichmentEffectsAsync(database);
        await enqueuer.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default);
    }

    [Fact]
    public async Task TriggerEnrichment_ActivePendingUndispatchedRetry_KeepsSingleJobAndAccelerates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var pending = EnrichmentJob.Create("Chrono", 7, 3, new FixedTimeProvider(FixedNow));
        await database.JobRepository.AddAsync(pending);
        var enqueuer = Substitute.For<IEnrichmentJobEnqueuer>();
        var handler = Handler(database, enqueuer);

        var result = await handler.HandleAsync(new TriggerTitleEnrichmentCommand(7));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(pending.Id);
        await using var read = database.CreateReadContext();
        (await read.Jobs.OfType<EnrichmentJobEntity>().CountAsync()).ShouldBe(1);
        (await read.Titles.SingleAsync()).EnrichmentStatus
            .ShouldBe(EnrichmentStatus.Pending.ToString());
        await enqueuer.Received(1).EnqueueAsync(pending.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerEnrichment_TerminalRetry_CreatesNewPendingJob()
    {
        await using var database = await TestDatabase.CreateAsync();
        var finished = EnrichmentJob.Create("Chrono", 7, 3, new FixedTimeProvider(FixedNow));
        finished.Start("hangfire-done");
        finished.Complete();
        await database.JobRepository.AddAsync(finished);
        var enqueuer = Substitute.For<IEnrichmentJobEnqueuer>();
        var handler = Handler(database, enqueuer);

        var result = await handler.HandleAsync(new TriggerTitleEnrichmentCommand(7));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldNotBe(finished.Id);
        await using var read = database.CreateReadContext();
        (await read.Jobs.OfType<EnrichmentJobEntity>().CountAsync()).ShouldBe(2);
        var active = await read.Jobs.OfType<EnrichmentJobEntity>()
            .SingleAsync(job => job.Phase == EnrichmentJobPhase.Pending.ToString());
        active.Id.ShouldBe(result.Value);
        await enqueuer.Received(1).EnqueueAsync(result.Value, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerEnrichment_MissingTitle_ReturnsNotFoundWithoutEffects()
    {
        await using var database = await TestDatabase.CreateAsync();
        var enqueuer = Substitute.For<IEnrichmentJobEnqueuer>();
        var handler = Handler(database, enqueuer);

        var result = await handler.HandleAsync(new TriggerTitleEnrichmentCommand(404));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.TitleNotFound");
        await AssertNoEnrichmentEffectsAsync(database);
    }

    private static TriggerTitleEnrichmentCommandHandler Handler(
        TestDatabase database,
        IEnrichmentJobEnqueuer enqueuer) =>
        new(
            database.TitleRepository,
            database.JobRepository,
            enqueuer,
            database.UnitOfWork,
            new FixedTimeProvider(FixedNow),
            NullLogger<TriggerTitleEnrichmentCommandHandler>.Instance);

    private static async Task AssertNoEnrichmentEffectsAsync(TestDatabase database)
    {
        await using var read = database.CreateReadContext();
        (await read.Titles.SingleAsync()).EnrichmentStatus
            .ShouldBe(EnrichmentStatus.Completed.ToString());
        (await read.Jobs.OfType<EnrichmentJobEntity>().AnyAsync()).ShouldBeFalse();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FailingCommitUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            inner.FlushAsync(cancellationToken);

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new FailingCommitTransaction(
                inner,
                await inner.BeginTransactionAsync(cancellationToken));
    }

    private sealed class FailingCommitTransaction(
        IUnitOfWork unitOfWork,
        ITransaction inner) : ITransaction
    {
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await unitOfWork.FlushAsync(cancellationToken);
            await inner.RollbackAsync(CancellationToken.None);
            throw new InvalidOperationException("Injected commit failure.");
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            inner.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class CancelAtCommitUnitOfWork(
        IUnitOfWork inner,
        CancellationTokenSource cancellation) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            inner.FlushAsync(cancellationToken);

        public async Task<ITransaction> BeginTransactionAsync(
            CancellationToken cancellationToken = default) =>
            new CancelAtCommitTransaction(
                inner,
                await inner.BeginTransactionAsync(cancellationToken),
                cancellation);
    }

    private sealed class CancelAtCommitTransaction(
        IUnitOfWork unitOfWork,
        ITransaction inner,
        CancellationTokenSource cancellation) : ITransaction
    {
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await unitOfWork.FlushAsync(cancellationToken);
            await cancellation.CancelAsync();
            await inner.RollbackAsync(CancellationToken.None);
            throw new OperationCanceledException(cancellation.Token);
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            inner.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly PostgreSqlTestDatabase _connection;
        private readonly DbContextOptions<RomdDbContext> _options;

        private TestDatabase(
            PostgreSqlTestDatabase connection,
            DbContextOptions<RomdDbContext> options,
            RomdDbContext context,
            bool failCommit)
        {
            _connection = connection;
            _options = options;
            Context = context;
            TitleRepository = new TitleRepository(context);
            JobRepository = new EnrichmentJobRepository(context, new FixedTimeProvider(FixedNow));
            IUnitOfWork unitOfWork = new EfUnitOfWork(context);
            UnitOfWork = failCommit ? new FailingCommitUnitOfWork(unitOfWork) : unitOfWork;
        }

        public RomdDbContext Context { get; }
        public TitleRepository TitleRepository { get; }
        public EnrichmentJobRepository JobRepository { get; }
        public IUnitOfWork UnitOfWork { get; }

        public RomdDbContext CreateReadContext() => new(_options);

        public static async Task<TestDatabase> CreateAsync(bool failCommit = false)
        {
            var connection = PostgreSqlTestDatabase.Create();
            var options = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(connection.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            var context = new RomdDbContext(options);
            context.Platforms.Add(new PlatformEntity
            {
                Id = 3,
                Name = "Super Nintendo",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
                CreatedAt = DateTimeOffset.UtcNow
            });
            context.Titles.Add(new TitleEntity
            {
                Id = 7,
                PlatformId = 3,
                Name = "Chrono",
                NormalizedName = "chrono",
                EnrichmentStatus = EnrichmentStatus.Completed.ToString(),
                CatalogState = TitleCatalogState.UserOnly,
                FieldProvenanceJson = """{"Description":"igdb"}""",
                FieldSourceOverridesJson = """{"Genre":"user"}""",
                ScreenshotPrefsJson = """{"preferred":"gameplay"}""",
                CreatedAt = DateTimeOffset.UtcNow
            });
            context.TrackedTitles.Add(new TrackedTitleEntity
            {
                TitleId = 7,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            context.TitleContentRatings.Add(new TitleContentRatingEntity
            {
                TitleId = 7,
                Board = 1,
                Code = "E",
                Designation = 1,
                SourceId = "igdb",
                CreatedAt = DateTimeOffset.UtcNow
            });
            context.Files.Add(new FileEntityPersistence
            {
                Id = 11,
                Sha256 = Sha256.FromBytes(new byte[Sha256.ByteLength]),
                Size = 1,
                SizeOnDisk = 1,
                IsCompressed = false,
                CreatedAt = DateTimeOffset.UtcNow
            });
            context.TitleMedia.Add(new TitleMediaEntity
            {
                TitleId = 7,
                Type = nameof(MediaType.Cover),
                FileId = 11,
                SourceId = "igdb",
                IsPrimary = true,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            return new TestDatabase(connection, options, context, failCommit);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
