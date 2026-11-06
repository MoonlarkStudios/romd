using System.Text.Json;
using ErrorOr;
using TitleDetail = Romd.Contracts.Management.Models.TitleDetail;
using Romd.Admin.Application.Titles.Commands.UpdateUserMetadata;
using Romd.Admin.Application.Titles.Commands.SetTitleContentRating;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles.Commands.SetFieldOverrides;
using Romd.Application.Common.Concurrency;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class SetFieldOverridesAtomicityTests
{
    private const int PlatformId = 10;
    private const int TitleId = 20;
    private const int LibraryId = 30;
    private const int FileId = 40;
    private const int MediaId = 50;
    private const int ExternalId = 60;

    [Fact]
    public async Task SetFieldOverrides_Success_CommitsOverrideLibraryFlagAndPreservesUntargetedState()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database.Context);
        var handler = CreateHandler(database.Context, new EfUnitOfWork(database.Context));

        var result = await handler.HandleAsync(new SetFieldOverridesCommand(
            TitleId,
            new Dictionary<string, string?> { ["Genre"] = "igdb" },
            await CurrentAsync(database.Context)));

        result.IsError.ShouldBeFalse();
        result.Value.Genre.ShouldBe("Action");

        await using var assertionContext = database.CreateAssertionContext();
        var title = await assertionContext.Titles.AsNoTracking().SingleAsync(row => row.Id == TitleId);
        title.Genre.ShouldBe("Action");
        title.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        title.ScreenshotPrefsJson.ShouldBe("{\"layout\":\"grid\"}");
        var overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(title.FieldSourceOverridesJson);
        overrides.ShouldNotBeNull();
        overrides.Count.ShouldBe(2);
        overrides["Genre"].ShouldBe("igdb");
        overrides["Publisher"].ShouldBe("user");
        var provenance = JsonSerializer.Deserialize<Dictionary<string, string>>(title.FieldProvenanceJson);
        provenance.ShouldNotBeNull();
        provenance["Genre"].ShouldBe("igdb");
        provenance["Publisher"].ShouldBe("user");

        var media = await assertionContext.TitleMedia.AsNoTracking()
            .SingleAsync(row => row.TitleId == TitleId);
        media.Id.ShouldBe(MediaId);
        media.FileId.ShouldBe(FileId);
        media.SourceId.ShouldBe("user");
        media.ContentType.ShouldBe("image/png");
        media.SourceUrl.ShouldBe("https://example.test/cover.png");
        media.IsPrimary.ShouldBeTrue();

        var external = await assertionContext.TitleExternalIds.AsNoTracking()
            .SingleAsync(row => row.TitleId == TitleId);
        external.Id.ShouldBe(ExternalId);
        external.Provider.ShouldBe("igdb");
        external.ExternalId.ShouldBe("12345");
        external.IsConfirmed.ShouldBeTrue();

        var layers = await assertionContext.TitleMetadataLayers.AsNoTracking()
            .Where(row => row.TitleId == TitleId)
            .OrderBy(row => row.SourceId)
            .Select(row => new { row.SourceId, row.MetadataJson })
            .ToListAsync();
        layers.Count.ShouldBe(2);
        layers.Select(layer => layer.SourceId).ShouldBe(["igdb", "user"]);
        layers[0].MetadataJson.ShouldContain("Action");
        layers[1].MetadataJson.ShouldContain("Role-playing");

        var rating = await assertionContext.TitleContentRatings.AsNoTracking()
            .SingleAsync(row => row.TitleId == TitleId);
        rating.Board.ShouldBe((int)RatingBoard.Esrb);
        rating.Code.ShouldBe("T");
        rating.SourceId.ShouldBe("igdb");
        rating.DescriptorsJson.ShouldContain("Violence");
        (await assertionContext.Libraries.AsNoTracking().SingleAsync(row => row.Id == LibraryId))
            .NeedsMaterialization.ShouldBeTrue();
    }

    [Fact]
    public async Task SetFieldOverrides_CommitFails_RollsBackOverrideAndLibraryFlag()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database.Context);
        var inner = new EfUnitOfWork(database.Context);
        var handler = CreateHandler(database.Context, new FailingCommitUnitOfWork(inner));

        var result = await handler.HandleAsync(new SetFieldOverridesCommand(
            TitleId,
            new Dictionary<string, string?> { ["Genre"] = "igdb" },
            await CurrentAsync(database.Context)));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.TitleUpdateFailed");
        result.FirstError.Description.ShouldBe("Title update failed");

        await using var assertionContext = database.CreateAssertionContext();
        var title = await assertionContext.Titles.AsNoTracking().SingleAsync(row => row.Id == TitleId);
        title.Genre.ShouldBe("Role-playing");
        title.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        title.FieldSourceOverridesJson.ShouldNotContain("Genre");
        title.FieldSourceOverridesJson.ShouldContain("Publisher");
        (await assertionContext.Libraries.AsNoTracking().SingleAsync(row => row.Id == LibraryId))
            .NeedsMaterialization.ShouldBeFalse();
    }

    [Fact]
    public async Task SetFieldOverrides_CancelledAfterFlush_RollsBackOverrideAndLibraryFlagWithoutCommit()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database.Context);
        using var cancellation = new CancellationTokenSource();
        var unitOfWork = new CancelAfterFlushUnitOfWork(
            new EfUnitOfWork(database.Context),
            cancellation);
        var handler = CreateHandler(database.Context, unitOfWork);
        var ifMatch = await CurrentAsync(database.Context);

        var exception = await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.HandleAsync(
                new SetFieldOverridesCommand(
                    TitleId,
                    new Dictionary<string, string?> { ["Genre"] = "igdb" },
                    ifMatch),
                cancellation.Token));

        exception.CancellationToken.ShouldBe(cancellation.Token);
        cancellation.IsCancellationRequested.ShouldBeTrue();
        unitOfWork.CommitCount.ShouldBe(0);

        await using var assertionContext = database.CreateAssertionContext();
        var title = await assertionContext.Titles.AsNoTracking().SingleAsync(row => row.Id == TitleId);
        title.Genre.ShouldBe("Role-playing");
        title.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        var overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(title.FieldSourceOverridesJson);
        overrides.ShouldNotBeNull();
        overrides.Count.ShouldBe(1);
        overrides["Publisher"].ShouldBe("user");
        var provenance = JsonSerializer.Deserialize<Dictionary<string, string>>(title.FieldProvenanceJson);
        provenance.ShouldNotBeNull();
        provenance["Genre"].ShouldBe("user");
        provenance["Publisher"].ShouldBe("user");
        (await assertionContext.TitleContentRatings.AsNoTracking()
                .SingleAsync(row => row.TitleId == TitleId))
            .Code.ShouldBe("T");
        (await assertionContext.Libraries.AsNoTracking().SingleAsync(row => row.Id == LibraryId))
            .NeedsMaterialization.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserMetadata_BeforeCommit_WorkerSeesNeitherNewMetadataNorInvalidation(bool contentRating)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database.Context);
        var unitOfWork = new PausedFlushUnitOfWork(new EfUnitOfWork(database.Context));
        var mutation = MutateUserMetadataAsync(database.Context, unitOfWork, contentRating);

        try
        {
            await unitOfWork.Flushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var observer = database.CreateAssertionContext();
            var title = await observer.Titles.SingleAsync(row => row.Id == TitleId);
            title.Genre.ShouldBe("Role-playing");
            (await observer.TitleContentRatings.SingleAsync(row => row.TitleId == TitleId))
                .Code.ShouldBe("T");
            (await observer.Libraries.SingleAsync(row => row.Id == LibraryId))
                .NeedsMaterialization.ShouldBeFalse();
        }
        finally
        {
            unitOfWork.Resume.TrySetResult();
            await mutation;
        }

        (await mutation).IsError.ShouldBeFalse();
        await using var assertionContext = database.CreateAssertionContext();
        var updated = await assertionContext.Titles.SingleAsync(row => row.Id == TitleId);
        updated.Genre.ShouldBe(contentRating ? "Role-playing" : "Adventure");
        (await assertionContext.TitleContentRatings.SingleAsync(row => row.TitleId == TitleId))
            .Code.ShouldBe(contentRating ? "M" : "T");
        var userLayer = await assertionContext.TitleMetadataLayers
            .SingleAsync(row => row.TitleId == TitleId && row.SourceId == "user");
        userLayer.MetadataJson.ShouldContain(contentRating ? "M" : "Adventure");
        (await assertionContext.Libraries.SingleAsync(row => row.Id == LibraryId))
            .NeedsMaterialization.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserMetadata_CommitFails_RollsBackLayerEffectiveMetadataAndInvalidation(bool contentRating)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database.Context);

        var result = await MutateUserMetadataAsync(database.Context,
            new FailingCommitUnitOfWork(new EfUnitOfWork(database.Context)), contentRating);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.TitleUpdateFailed");
        await using var assertionContext = database.CreateAssertionContext();
        (await assertionContext.Titles.SingleAsync(row => row.Id == TitleId))
            .Genre.ShouldBe("Role-playing");
        (await assertionContext.TitleContentRatings.SingleAsync(row => row.TitleId == TitleId))
            .Code.ShouldBe("T");
        var userLayer = await assertionContext.TitleMetadataLayers
            .SingleAsync(row => row.TitleId == TitleId && row.SourceId == "user");
        userLayer.MetadataJson.ShouldContain("Role-playing");
        userLayer.MetadataJson.ShouldNotContain("Adventure");
        userLayer.MetadataJson.ShouldNotContain("RawCode");
        (await assertionContext.Libraries.SingleAsync(row => row.Id == LibraryId))
            .NeedsMaterialization.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IfMatch_StaleIsRejectedWithoutWriting_CurrentSavesAndBumps_ChainedSavesAgain(bool fieldOverride)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database.Context);
        async Task<(string? Genre, Guid Revision, string Overrides, bool Flagged)> ReadAsync()
        {
            await using var read = database.CreateAssertionContext();
            var row = await read.Titles.AsNoTracking().SingleAsync(x => x.Id == TitleId);
            return (row.Genre, row.Revision, row.FieldSourceOverridesJson,
                (await read.Libraries.AsNoTracking().SingleAsync(x => x.Id == LibraryId)).NeedsMaterialization);
        }
        var before = await ReadAsync();

        var stale = await SaveAsync(database.Context, new EfUnitOfWork(database.Context), fieldOverride,
            RevisionPrecondition.For(Guid.NewGuid().ToString()));

        stale.FirstError.Code.ShouldBe("Titles.PreconditionFailed");
        ((int)stale.FirstError.Type).ShouldBe(412);
        (await ReadAsync()).ShouldBe(before);

        database.Context.ChangeTracker.Clear();
        var current = await SaveAsync(database.Context, new EfUnitOfWork(database.Context), fieldOverride,
            RevisionPrecondition.For(before.Revision.ToString()));
        current.IsError.ShouldBeFalse();
        var saved = await ReadAsync();
        saved.Genre.ShouldBe(fieldOverride ? "Action" : "Adventure");
        saved.Revision.ShouldNotBe(before.Revision);
        current.Value.Revision.ShouldBe(saved.Revision.ToString());

        // The response's revision is the next write's validator; the first one is now stale.
        database.Context.ChangeTracker.Clear();
        (await SaveAsync(database.Context, new EfUnitOfWork(database.Context), fieldOverride,
            RevisionPrecondition.For(before.Revision.ToString()))).FirstError.Code.ShouldBe("Titles.PreconditionFailed");
        database.Context.ChangeTracker.Clear();
        (await SaveAsync(database.Context, new EfUnitOfWork(database.Context), fieldOverride,
            RevisionPrecondition.For(current.Value.Revision))).IsError.ShouldBeFalse();
        (await ReadAsync()).Revision.ShouldNotBe(saved.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IfMatch_TwoSavesFromTheSameRevision_OneWinsAndTheOtherFailsThePrecondition(bool loserOverridesField)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database.Context);
        Guid revision;
        await using (var read = database.CreateAssertionContext())
            revision = (await read.Titles.AsNoTracking().SingleAsync(x => x.Id == TitleId)).Revision;
        var winnerUnitOfWork = new PausedFlushUnitOfWork(new EfUnitOfWork(database.Context));
        var winner = SaveAsync(database.Context, winnerUnitOfWork, fieldOverride: false, RevisionPrecondition.For(revision.ToString()));
        await using var loserContext = database.CreateAssertionContext();
        await using var observer = database.CreateAssertionContext();

        try
        {
            // The winner holds the title row. The loser passes the revision check against committed data and
            // then waits on that row, so only the save-time recheck can reject it.
            await winnerUnitOfWork.Flushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await loserContext.Database.OpenConnectionAsync();
            int loserPid = await loserContext.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
            var loser = SaveAsync(loserContext, new EfUnitOfWork(loserContext), loserOverridesField, RevisionPrecondition.For(revision.ToString()));
            while (!await observer.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = {loserPid} AND NOT granted) AS \"Value\"").SingleAsync())
            {
                loser.IsCompleted.ShouldBeFalse();
                await Task.Yield();
            }
            winnerUnitOfWork.Resume.TrySetResult();

            (await winner).IsError.ShouldBeFalse();
            (await loser).FirstError.Code.ShouldBe("Titles.PreconditionFailed");
        }
        finally
        {
            winnerUnitOfWork.Resume.TrySetResult();
        }

        await using var assertion = database.CreateAssertionContext();
        var title = await assertion.Titles.AsNoTracking().SingleAsync(x => x.Id == TitleId);
        title.Genre.ShouldBe("Adventure");
        title.FieldSourceOverridesJson.ShouldNotContain("Genre");
        title.Revision.ShouldNotBe(revision);
    }

    private static Task<ErrorOr<TitleDetail>> SaveAsync(RomdDbContext context, IUnitOfWork unitOfWork, bool fieldOverride, RevisionPrecondition ifMatch) =>
        fieldOverride
            ? CreateHandler(context, unitOfWork).HandleAsync(new SetFieldOverridesCommand(TitleId,
                new Dictionary<string, string?> { ["Genre"] = "igdb" }, ifMatch))
            : new UpdateUserMetadataCommandHandler(new Romd.Persistence.ReferenceData.ReferenceCatalogService(context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.For(context)),
                    new TitleRepository(context),
                    new TitleRepository(context),
                    new Romd.Persistence.Queries.TitleDetailReader(context), new TestMetadataRematerializer(),
                    new LibraryRepository(context), unitOfWork,
                    NullLogger<UpdateUserMetadataCommandHandler>.Instance, clock: TimeProvider.System)
                .HandleAsync(new UpdateUserMetadataCommand(
                    TitleId, new TitleMetadataPayload { Publisher = "Curated Publisher", Genre = "Adventure" },
                    TitleMetadataFields.Publisher | TitleMetadataFields.Genre, ifMatch));

    private static Task<RevisionPrecondition> CurrentAsync(RomdDbContext context) =>
        TitleRevisions.CurrentAsync(context, TitleId);

    private static async Task<ErrorOr<TitleDetail>> MutateUserMetadataAsync(
        RomdDbContext context, IUnitOfWork unitOfWork, bool contentRating)
    {
        var ifMatch = await CurrentAsync(context);
        return await (contentRating
            ? new SetTitleContentRatingCommandHandler(new Romd.Persistence.ReferenceData.ReferenceCatalogService(context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.For(context)),
                    new TitleRepository(context),
                    new TitleRepository(context),
                    new Romd.Persistence.Queries.TitleDetailReader(context), new TestMetadataRematerializer(),
                    new LibraryRepository(context), unitOfWork,
                    NullLogger<SetTitleContentRatingCommandHandler>.Instance, clock: TimeProvider.System)
                .HandleAsync(new SetTitleContentRatingCommand(TitleId, RatingBoard.Esrb, "M", null, null, ifMatch))
            : new UpdateUserMetadataCommandHandler(new Romd.Persistence.ReferenceData.ReferenceCatalogService(context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.For(context)),
                    new TitleRepository(context),
                    new TitleRepository(context),
                    new Romd.Persistence.Queries.TitleDetailReader(context), new TestMetadataRematerializer(),
                    new LibraryRepository(context), unitOfWork,
                    NullLogger<UpdateUserMetadataCommandHandler>.Instance, clock: TimeProvider.System)
                .HandleAsync(new UpdateUserMetadataCommand(
                    TitleId, new TitleMetadataPayload { Publisher = "Curated Publisher", Genre = "Adventure" },
                    TitleMetadataFields.Publisher | TitleMetadataFields.Genre, ifMatch)));
    }

    private sealed class PausedFlushUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public TaskCompletionSource Flushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);

        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            Flushed.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
        }

        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(cancellationToken);
    }

    private static SetFieldOverridesCommandHandler CreateHandler(
        RomdDbContext context,
        IUnitOfWork unitOfWork) =>
        new(new Romd.Persistence.ReferenceData.ReferenceCatalogService(context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.For(context)),
            new TitleRepository(context),
            new TitleRepository(context),
            new Romd.Persistence.Queries.TitleDetailReader(context),
            new TestMetadataRematerializer(),
            new LibraryRepository(context),
            unitOfWork,
            NullLogger<SetFieldOverridesCommandHandler>.Instance);

    private static async Task SeedAsync(RomdDbContext context)
    {
        var now = DateTimeOffset.UtcNow;
        context.Platforms.Add(new PlatformEntity
        {
            Id = PlatformId,
            Name = "Super Nintendo",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Super Nintendo",
            BaseCompactLabel = "Super Nintendo",
            CanonicalKey = "snes",
            ShortName = "snes",
            Manufacturer = "Nintendo",
            CreatedAt = now
        });
        context.Files.Add(new FileEntityPersistence
        {
            Id = FileId,
            Sha256 = Sha256.Parse(new string('a', 64)),
            Size = 1024,
            SizeOnDisk = 768,
            IsCompressed = true,
            CreatedAt = now
        });

        var providerLayer = TitleMetadataLayer.CreateNew(
            TitleId,
            "igdb",
            MetadataSourceType.Provider,
            new TitleMetadataPayload
            {
                Genre = "Action",
                Publisher = "Nintendo",
                ContentRatings =
                [
                    new ContentRatingClaim
                    {
                        Board = RatingBoard.Esrb,
                        RawCode = "T",
                        Descriptors = ["Violence"]
                    }
                ]
            }, updatedAt: DateTimeOffset.UnixEpoch);
        var userLayer = TitleMetadataLayer.CreateNew(
            TitleId,
            "user",
            MetadataSourceType.User,
            new TitleMetadataPayload
            {
                Genre = "Role-playing",
                Publisher = "Curated Publisher"
            }, updatedAt: DateTimeOffset.UnixEpoch);
        var domain = Title.Rehydrate(
            id: TitleId,
            platformId: PlatformId,
            name: "Super Mario World",
            normalizedName: "super mario world",
            description: null,
            publisher: null,
            developer: null,
            genre: null,
            releaseDate: null,
            players: null,
            rating: null,
            enrichmentStatus: EnrichmentStatus.Completed,
            lastEnrichedAt: now,
            createdAt: now,
            externalIds:
            [
                TitleExternalId.Rehydrate(ExternalId, TitleId, "igdb", "12345", 1, true, now)
            ],
            media:
            [
                TitleMedia.Rehydrate(
                    MediaId,
                    TitleId,
                    MediaType.Cover,
                    FileId,
                    "user",
                    "image/png",
                    true,
                    "https://example.test/cover.png",
                    now)
            ],
            metadataLayers: [providerLayer, userLayer],
            fieldSourceOverrides: new Dictionary<string, string> { ["Publisher"] = "user" });
        domain.Rematerialize(["igdb"]);

        var title = TitleEntity.FromDomain(domain);
        title.CatalogState = TitleCatalogState.UserOnly;
        title.ScreenshotPrefsJson = "{\"layout\":\"grid\"}";
        context.Titles.Add(title);
        context.TitleContentRatings.AddRange(
            domain.ContentRatings.Select(rating => TitleContentRatingEntity.FromDomain(TitleId, rating)));
        context.Libraries.Add(new LibraryEntity
        {
            Id = LibraryId,
            Name = "All Games",
            ConfigurationJson = JsonSerializer.Serialize(new LibraryConfiguration
            {
                AllowedPlatformIds = [PlatformId]
            }),
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = false,
            CreatedAt = now
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private sealed class TestMetadataRematerializer : IMetadataRematerializer
    {
        public Task RematerializeAsync(Title title, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            title.Rematerialize(["igdb"]);
            title.RecalculatePrimaryMedia(["igdb"]);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingCommitUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            inner.FlushAsync(cancellationToken);

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new FailingCommitTransaction(await inner.BeginTransactionAsync(cancellationToken));
    }

    private sealed class FailingCommitTransaction(ITransaction inner) : ITransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("private simulated commit failure"));

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            inner.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class CancelAfterFlushUnitOfWork(
        IUnitOfWork inner,
        CancellationTokenSource cancellation) : IUnitOfWork
    {
        public int CommitCount { get; private set; }

        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);

        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            await cancellation.CancelAsync();
            throw new OperationCanceledException(cancellation.Token);
        }

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new RecordingCommitTransaction(
                await inner.BeginTransactionAsync(cancellationToken),
                () => CommitCount++);
    }

    private sealed class RecordingCommitTransaction(
        ITransaction inner,
        Action recordCommit) : ITransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            recordCommit();
            return inner.CommitAsync(cancellationToken);
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            inner.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly PostgreSqlTestDatabase _connection;
        private readonly DbContextOptions<RomdDbContext> _options;

        private TestDatabase(PostgreSqlTestDatabase connection, DbContextOptions<RomdDbContext> options)
        {
            _connection = connection;
            _options = options;
            Context = new RomdDbContext(options);
        }

        public RomdDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = PostgreSqlTestDatabase.Create();
            var options = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(connection.ConnectionString)
                .Options;
            var database = new TestDatabase(connection, options);
            await database.Context.Database.EnsureCreatedAsync();
            return database;
        }

        public RomdDbContext CreateAssertionContext() => new(_options);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
