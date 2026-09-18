using System.Text.Json;
using ErrorOr;
using TitleDetail = Romd.Contracts.Management.Models.TitleDetail;
using Romd.Admin.Application.Titles.Commands.UpdateUserMetadata;
using Romd.Admin.Application.Titles.Commands.SetTitleContentRating;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles.Commands.SetFieldOverrides;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
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
            new Dictionary<string, string?> { ["Genre"] = "igdb" }));

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
            new Dictionary<string, string?> { ["Genre"] = "igdb" }));

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

        var exception = await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.HandleAsync(
                new SetFieldOverridesCommand(
                    TitleId,
                    new Dictionary<string, string?> { ["Genre"] = "igdb" }),
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

    private static Task<ErrorOr<TitleDetail>> MutateUserMetadataAsync(
        RomdDbContext context, IUnitOfWork unitOfWork, bool contentRating) =>
        contentRating
            ? new SetTitleContentRatingCommandHandler(new Romd.Persistence.ReferenceData.ReferenceCatalogService(context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance),
                    new TitleRepository(context), new TestMetadataRematerializer(),
                    new LibraryRepository(context), unitOfWork,
                    NullLogger<SetTitleContentRatingCommandHandler>.Instance)
                .HandleAsync(new SetTitleContentRatingCommand(TitleId, RatingBoard.Esrb, "M", null, null))
            : new UpdateUserMetadataCommandHandler(new Romd.Persistence.ReferenceData.ReferenceCatalogService(context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance),
                    new TitleRepository(context), new TestMetadataRematerializer(),
                    new LibraryRepository(context), unitOfWork,
                    NullLogger<UpdateUserMetadataCommandHandler>.Instance)
                .HandleAsync(new UpdateUserMetadataCommand(
                    TitleId, null, null, "Curated Publisher", null, "Adventure", null, null, null));

    private sealed class PausedFlushUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public TaskCompletionSource Flushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        new(new Romd.Persistence.ReferenceData.ReferenceCatalogService(context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance),
            new TitleRepository(context),
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
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
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
            });
        var userLayer = TitleMetadataLayer.CreateNew(
            TitleId,
            "user",
            MetadataSourceType.User,
            new TitleMetadataPayload
            {
                Genre = "Role-playing",
                Publisher = "Curated Publisher"
            });
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
