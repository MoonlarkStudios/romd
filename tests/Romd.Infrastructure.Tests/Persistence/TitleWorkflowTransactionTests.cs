using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.MergeTitles;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.Commands.SetPrimaryMedia;
using Romd.Application.Common.Concurrency;
using NSubstitute;
using Npgsql;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Commands.DeleteTitleMedia;
using Romd.Admin.Application.Titles.Commands.SetEnrichmentOmission;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Ids;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Enrichment;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>
///     The four title workflows that used the removed immediate update: each owns its transaction,
///     commits its write with its work intent, rolls both back on business errors, cancellation,
///     persistence failure, and stale snapshots, and preserves state it does not own. Title writes answer with
///     the state they committed: a writer that commits right after one is in neither its body nor its revision.
/// </summary>
public sealed class TitleWorkflowTransactionTests
{
    private const int TitleId = 7;
    private const int SourceTitleId = 8;

    [Fact]
    public async Task SetPrimaryMedia_Success_CommitsOnlyTheSelection()
    {
        await using var database = await TestDatabase.CreateAsync();

        var result = await SetPrimaryMedia(database).HandleAsync(new SetPrimaryMediaCommand(TitleId, 102, database.IfMatch));

        result.IsError.ShouldBeFalse();
        result.Value.Media.Items.Single(media => media.IsPrimary && media.Type == nameof(MediaType.Cover))
            .SourceId.ShouldBe("user");
        await using var read = database.CreateReadContext();
        await AssertPrimaryCoverAsync(read, 102);
        await AssertUnownedStatePreservedAsync(read, database.InitialRevision, rotated: true);
    }

    [Fact]
    public async Task SetPrimaryMedia_UnknownMedia_ReturnsErrorWithoutEffects()
    {
        await using var database = await TestDatabase.CreateAsync();

        var result = await SetPrimaryMedia(database).HandleAsync(new SetPrimaryMediaCommand(TitleId, 404, database.IfMatch));

        result.FirstError.Code.ShouldBe("Catalog.MediaNotFound");
        await using var read = database.CreateReadContext();
        await AssertPrimaryCoverAsync(read, 101);
        await AssertUnownedStatePreservedAsync(read, database.InitialRevision, rotated: false);
    }

    [Fact]
    public async Task SetPrimaryMedia_CommitFails_RollsBackSelection()
    {
        await using var database = await TestDatabase.CreateAsync(Failure.Commit);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            SetPrimaryMedia(database).HandleAsync(new SetPrimaryMediaCommand(TitleId, 102, database.IfMatch)));

        await using var read = database.CreateReadContext();
        await AssertPrimaryCoverAsync(read, 101);
        await AssertUnownedStatePreservedAsync(read, database.InitialRevision, rotated: false);
    }

    [Fact]
    public async Task SetPrimaryMedia_TitleChangedAfterLoad_FailsThePreconditionWithoutEffects()
    {
        await using var database = await TestDatabase.CreateAsync(Failure.ConcurrentEdit);

        var result = await SetPrimaryMedia(database).HandleAsync(new SetPrimaryMediaCommand(TitleId, 102, database.IfMatch));

        result.FirstError.Code.ShouldBe("Titles.PreconditionFailed");
        await using var read = database.CreateReadContext();
        await AssertPrimaryCoverAsync(read, 101);
    }

    [Fact]
    public async Task SetPrimaryMedia_StaleIfMatch_FailsThePreconditionWithoutEffects()
    {
        await using var database = await TestDatabase.CreateAsync();

        var result = await SetPrimaryMedia(database).HandleAsync(
            new SetPrimaryMediaCommand(TitleId, 102, RevisionPrecondition.For(Guid.NewGuid().ToString())));

        result.FirstError.Code.ShouldBe("Titles.PreconditionFailed");
        await using var read = database.CreateReadContext();
        await AssertPrimaryCoverAsync(read, 101);
        await AssertUnownedStatePreservedAsync(read, database.InitialRevision, rotated: false);
    }

    [Fact]
    public async Task MergeTitles_Success_AbsorbsSourceAndPreservesTargetState()
    {
        await using var database = await TestDatabase.CreateAsync(withSource: true);

        var result = await MergeTitles(database).HandleAsync(new MergeTitlesCommand(SourceTitleId, TitleId, database.IfMatch, database.SourceInitialRevision!.Value.ToString()));

        result.IsError.ShouldBeFalse();
        await using var read = database.CreateReadContext();
        (await read.Titles.AnyAsync(title => title.Id == SourceTitleId)).ShouldBeFalse();
        (await read.TitleExternalIds.Where(link => link.TitleId == TitleId).Select(link => link.Provider)
                .OrderBy(provider => provider).ToListAsync())
            .ShouldBe(["igdb", "screenscraper", "steamgriddb"]);
        (await read.TitleMetadataLayers.AnyAsync(layer => layer.TitleId == TitleId && layer.SourceId == "screenscraper"))
            .ShouldBeTrue();
        (await read.TitleMedia.CountAsync(media => media.TitleId == TitleId)).ShouldBe(4);
        (await read.MetadataRematerializationRequests.SingleAsync()).TitleId.ShouldBe(TitleId);
        var target = await read.Titles.SingleAsync(title => title.Id == TitleId);
        target.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        target.HasLocalPayload.ShouldBeTrue();
        target.ScreenshotPrefsJson.ShouldBe("""{"preserve":true}""");
        (await read.TitleMedia.SingleAsync(media => media.Id == 101)).Attribution.ShouldBe("Artist");
        (await read.TrackedTitles.AnyAsync(tracked => tracked.TitleId == TitleId)).ShouldBeTrue();
    }

    [Fact]
    public async Task MergeTitles_TargetUnlinkedAProvider_AbsorbsNeitherItsLinkNorItsMetadataNorItsMedia()
    {
        await using var database = await TestDatabase.CreateAsync(withSource: true);
        // The curator unlinked screenscraper on the target; the source still carries a screenscraper link and a
        // screenscraper layer with a field the target has from no other source.
        database.Context.Add(new TitleProviderMatchStateEntity
        {
            TitleId = TitleId,
            ProviderId = "screenscraper",
            SuppressAutomaticMatch = true
        });
        await database.Context.SaveChangesAsync();
        await database.Context.TitleMetadataLayers.Where(layer => layer.TitleId == SourceTitleId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(layer => layer.MetadataJson,
                """{"description":"Source description","publisher":"Source publisher"}"""));
        database.Context.ChangeTracker.Clear();

        var result = await MergeTitles(database).HandleAsync(new MergeTitlesCommand(SourceTitleId, TitleId, database.IfMatch,
            database.SourceInitialRevision!.Value.ToString()));

        result.IsError.ShouldBeFalse();
        result.Value.Publisher.ShouldBeNull();
        await using var read = database.CreateReadContext();
        (await read.TitleExternalIds.Where(link => link.TitleId == TitleId).Select(link => link.Provider)
                .OrderBy(provider => provider).ToListAsync())
            .ShouldBe(["igdb", "steamgriddb"]);
        var target = await read.Titles.SingleAsync(title => title.Id == TitleId);
        target.Publisher.ShouldBeNull();
        target.FieldProvenanceJson.ShouldNotContain("screenscraper");
        (await read.TitleMetadataLayers.Where(layer => layer.TitleId == TitleId).Select(layer => layer.SourceId)
                .OrderBy(source => source).ToListAsync())
            .ShouldBe(["igdb", "user"]);
        // The source's screenscraper background is not absorbed either.
        (await read.TitleMedia.Where(media => media.TitleId == TitleId).Select(media => media.SourceId).ToListAsync())
            .ShouldNotContain("screenscraper");
        (await read.Set<TitleProviderMatchStateEntity>().SingleAsync(state => state.TitleId == TitleId))
            .SuppressAutomaticMatch.ShouldBeTrue();
    }

    [Fact]
    public async Task MergeTitles_TargetUnlinkedAProvider_StillCarriesTheSourcesGalleryImagesAndArtworkAssetsFromIt()
    {
        await using var database = await TestDatabase.CreateAsync(withSource: true);
        // An unlink keeps the provider's curator-collected gallery images and artwork-library assets, so a merge
        // carries the source's too. Only the provider's link, layer and enrichment media stay behind.
        var now = DateTimeOffset.UtcNow;
        database.Context.Add(new TitleProviderMatchStateEntity
        {
            TitleId = TitleId,
            ProviderId = "screenscraper",
            SuppressAutomaticMatch = true
        });
        database.Context.TitleMedia.Add(new TitleMediaEntity
        {
            Id = 105,
            TitleId = SourceTitleId,
            Type = nameof(MediaType.Logo),
            FileId = 4,
            SourceId = "gallery:screenscraper",
            CreatedAt = now
        });
        database.Context.ArtworkAssets.Add(new ArtworkAssetEntity
        {
            TitleId = SourceTitleId,
            Role = ArtworkRole.Logo,
            SourceId = "screenscraper",
            ProviderGameId = "5",
            ProviderAssetId = "50",
            OriginalFileId = 4,
            ContentVersion = "original",
            ContentType = "image/png",
            Width = 400,
            Height = 200,
            IsEligible = true,
            CreatedAt = now,
            Variants = [new() { Name = "small", FileId = 4, ContentVersion = "small-v1", ContentType = "image/png",
                Width = 120, Height = 60 }]
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var result = await MergeTitles(database).HandleAsync(new MergeTitlesCommand(SourceTitleId, TitleId, database.IfMatch,
            database.SourceInitialRevision!.Value.ToString()));

        result.IsError.ShouldBeFalse();
        await using var read = database.CreateReadContext();
        var media = await read.TitleMedia.Where(item => item.TitleId == TitleId).Select(item => item.SourceId).ToListAsync();
        media.ShouldContain("gallery:screenscraper");
        media.ShouldNotContain("screenscraper");
        (await read.ArtworkAssets.Where(asset => asset.TitleId == TitleId).Select(asset => asset.SourceId).ToListAsync())
            .ShouldBe(["screenscraper"]);
        (await read.TitleExternalIds.AnyAsync(link => link.TitleId == TitleId && link.Provider == "screenscraper"))
            .ShouldBeFalse();
        (await read.TitleMetadataLayers.AnyAsync(layer => layer.TitleId == TitleId && layer.SourceId == "screenscraper"))
            .ShouldBeFalse();
    }

    [Theory]
    [InlineData(Failure.Commit)]
    [InlineData(Failure.CancelAtCommit)]
    [InlineData(Failure.ConcurrentSourceEdit)]
    public async Task MergeTitles_CommitDoesNotHappen_RollsBackTargetSourceAndIntent(Failure failure)
    {
        await using var database = await TestDatabase.CreateAsync(failure, withSource: true);
        var merge = () => MergeTitles(database).HandleAsync(
            new MergeTitlesCommand(SourceTitleId, TitleId, database.IfMatch, database.SourceInitialRevision!.Value.ToString()),
            database.Cancellation);

        // A concurrent source edit is the source's stale batch revision, reported as a 409.
        if (failure == Failure.ConcurrentSourceEdit)
            (await merge()).FirstError.Code.ShouldBe(CatalogErrors.MergeSourceChangedCode);
        else
            await Should.ThrowAsync<Exception>(merge);

        await using var read = database.CreateReadContext();
        (await read.Titles.AnyAsync(title => title.Id == SourceTitleId)).ShouldBeTrue();
        (await read.TitleExternalIds.CountAsync(link => link.TitleId == TitleId)).ShouldBe(2);
        (await read.TitleMedia.CountAsync(media => media.TitleId == TitleId)).ShouldBe(3);
        (await read.MetadataRematerializationRequests.AnyAsync()).ShouldBeFalse();
        await AssertUnownedStatePreservedAsync(read, database.InitialRevision, rotated: false);
    }

    [Fact]
    public async Task DeleteMedia_AWriterThatCommitsRightAfter_IsNotInTheResponse()
    {
        await using var database = await TestDatabase.CreateAsync();
        Guid committed = default;
        var files = Substitute.For<IFileStorageService>();
        // The file delete runs after the commit: capture the delete's revision, then let a later writer commit.
        files.DeleteIfUnreferencedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            committed = await database.StoredRevisionAsync();
            await database.WriteDescriptionConcurrentlyAsync("Later writer");
            return true;
        });

        var result = await DeleteMedia(database, files).HandleAsync(new DeleteTitleMediaCommand(TitleId, 102, database.IfMatch));

        result.IsError.ShouldBeFalse();
        result.Value.Revision.ShouldBe(committed.ToString());
        result.Value.Description.ShouldBe("Curated description");
        result.Value.Media.Items.ShouldNotContain(media => media.Id == IdCoder.Encode(102));
        (await database.StoredRevisionAsync()).ShouldNotBe(committed);
    }

    [Fact]
    public async Task DeleteMedia_ProviderImage_ALaterEnrichmentNeverAcquiresItAgain()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string url = "https://images.igdb.com/igdb/image/upload/t_original/shot.jpg";
        await database.Context.TitleMedia.Where(media => media.Id == 103)
            .ExecuteUpdateAsync(setters => setters.SetProperty(media => media.SourceUrl, url));

        (await DeleteMedia(database, Substitute.For<IFileStorageService>())
            .HandleAsync(new DeleteTitleMediaCommand(TitleId, 103, database.IfMatch))).IsError.ShouldBeFalse();

        // The provider offers the deleted image again; the title has no screenshot from it, so only the exclusion
        // keeps the enrichment from acquiring it.
        await using var read = database.CreateReadContext();
        var title = (await new TitleRepository(read).GetAggregateAsync(TitleId)).ShouldNotBeNull();
        var identities = Substitute.For<Romd.Admin.Application.MetadataProviders.IProviderIdentityService>();
        identities.GetTrustedAsync(TitleId, "igdb", Arg.Any<CancellationToken>()).Returns("123");
        var contexts = Substitute.For<IEnrichmentContextFactory>();
        contexts.CreateAsync(default!, default!, default).ReturnsForAnyArgs(new EnrichmentContext
        {
            TitleName = title.Name,
            PlatformShortName = "snes"
        });
        var stager = Substitute.For<IEnrichmentMediaStager>();
        var orchestrator = new EnrichmentOrchestrator(
            [new Romd.Infrastructure.Tests.Enrichment.Helpers.FakeMetadataProvider("igdb", _ => EnrichmentResult.Found("123", 1f, new EnrichmentData { Genre = "RPG" },
                new Dictionary<MediaType, string> { [MediaType.Screenshot] = url }))],
            contexts, identities, Substitute.For<Romd.Admin.Application.MetadataProviders.ITitleProviderMatchService>(),
            Substitute.For<IMetadataRematerializer>(), stager, new ArtworkCurationRepository(read, TimeProvider.System),
            Options.Create(new EnrichmentOptions
            {
                GlobalSourcePriority = ["igdb"],
                DownloadMedia = true,
                MediaTypesToDownload = [MediaType.Screenshot]
            }),
            NullLogger<EnrichmentOrchestrator>.Instance, clock: TimeProvider.System);

        await using var enriched = await orchestrator.EnrichTitleAsync(title,
            Romd.Domain.Source.Platform.Platform.Rehydrate(3, "Super Nintendo", "snes", "Nintendo", DateTimeOffset.UtcNow));

        enriched.Enriched.ShouldBeTrue();
        await stager.DidNotReceiveWithAnyArgs().StageAsync(default, default!, default, default!, default);
    }

    [Fact]
    public async Task SetOmission_AWriterThatCommitsRightAfter_IsNotInTheResponse()
    {
        await using var database = await TestDatabase.CreateAsync();
        var reader = new LaterWriterTitleDetailReader(database);
        var handler = new SetEnrichmentOmissionCommandHandler(database.ReferenceCatalog,
            new EnrichmentWorkbench(database.Context, TimeProvider.System, new ArtworkReader(database.Context)),
            reader, database.UnitOfWork, Microsoft.Extensions.Logging.Abstractions.NullLogger<SetEnrichmentOmissionCommandHandler>.Instance);

        var result = await handler.HandleAsync(new SetEnrichmentOmissionCommand(TitleId, "Genre", true, database.IfMatch));
        await reader.LaterWriter;

        result.IsError.ShouldBeFalse();
        reader.RowHeld.ShouldBeTrue();
        result.Value.Revision.ShouldBe(reader.Written.ToString());
        result.Value.Revision.ShouldNotBe(database.InitialRevision.ToString());
        result.Value.Description.ShouldBe("Curated description");
        (await database.StoredRevisionAsync()).ShouldNotBe(reader.Written);
    }

    private static SetPrimaryMediaCommandHandler SetPrimaryMedia(TestDatabase database) =>
        new(database.ReferenceCatalog, database.Titles, database.Titles, database.TitleDetails, database.UnitOfWork,
            NullLogger<SetPrimaryMediaCommandHandler>.Instance);

    private static MergeTitlesCommandHandler MergeTitles(TestDatabase database) =>
        new(database.ReferenceCatalog, database.Titles, database.Titles, database.TitleDetails,
            new TrackedTitleRepository(database.Context, TimeProvider.System),
            new DatRepository(database.Context, TimeProvider.System),
            database.UnitOfWork, database.Scheduler, new LibraryRepository(database.Context),
            CatalogProjectionTestFactory.Create(database.Context), Options.Create(new EnrichmentOptions()),
            NullLogger<MergeTitlesCommandHandler>.Instance, TimeProvider.System);

    private static DeleteTitleMediaCommandHandler DeleteMedia(TestDatabase database, IFileStorageService files) =>
        new(database.ReferenceCatalog, database.Titles, database.TitleDetails, database.Titles,
            new ArtworkCurationRepository(database.Context, TimeProvider.System), files,
            Substitute.For<IAdminEventOutbox>(), database.UnitOfWork, Options.Create(new EnrichmentOptions()),
            NullLogger<DeleteTitleMediaCommandHandler>.Instance);

    private static async Task AssertPrimaryCoverAsync(RomdDbContext read, int mediaId) =>
        (await read.TitleMedia.Where(media => media.TitleId == TitleId && media.Type == nameof(MediaType.Cover))
                .Where(media => media.IsPrimary).Select(media => media.Id).ToListAsync())
            .ShouldBe([mediaId]);

    private static async Task AssertUnownedStatePreservedAsync(RomdDbContext read, Guid initialRevision,
        bool rotated)
    {
        var title = await read.Titles.SingleAsync(row => row.Id == TitleId);
        (title.Revision != initialRevision).ShouldBe(rotated);
        title.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        title.HasLocalPayload.ShouldBeTrue();
        title.ScreenshotPrefsJson.ShouldBe("""{"preserve":true}""");
        title.FieldSourceOverridesJson.ShouldBe("""{"Genre":"user"}""");
        title.Description.ShouldBe("Curated description");
        (await read.TitleExternalIds.Where(link => link.TitleId == TitleId).OrderBy(link => link.Provider)
                .Select(link => new { link.Provider, link.ExternalId, link.IsConfirmed }).ToListAsync())
            .Select(link => (link.Provider, link.ExternalId, link.IsConfirmed))
            .ShouldBe([("igdb", "123", false), ("steamgriddb", "77", false)]);
        (await read.TitleMetadataLayers.Where(layer => layer.TitleId == TitleId).OrderBy(layer => layer.SourceId)
                .Select(layer => layer.MetadataJson).ToListAsync())
            .ShouldBe(["""{"Description":"provider"}""", """{"Description":"Curated description"}"""]);
        (await read.TitleMedia.Where(media => media.TitleId == TitleId).CountAsync()).ShouldBe(3);
        (await read.TitleMedia.SingleAsync(media => media.Id == 101)).Attribution.ShouldBe("Artist");
        (await read.TitleContentRatings.SingleAsync(rating => rating.TitleId == TitleId)).Code.ShouldBe("E");
        (await read.TrackedTitles.AnyAsync(tracked => tracked.TitleId == TitleId)).ShouldBeTrue();
    }

    public enum Failure
    {
        None,
        Commit,
        CancelAtCommit,
        ConcurrentEdit,
        ConcurrentSourceEdit
    }

    /// <summary>
    ///     Injects the failure at the transaction boundary: a failed or cancelled commit after the
    ///     final flush, or a concurrent edit committed by another connection right after begin.
    /// </summary>
    private sealed class FaultingUnitOfWork(TestDatabase database, IUnitOfWork inner) : IUnitOfWork
    {
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);

        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            var transaction = await inner.BeginTransactionAsync(cancellationToken);
            if (database.Failure == Failure.ConcurrentEdit)
                await database.RotateRevisionConcurrentlyAsync(TitleId);
            if (database.Failure == Failure.ConcurrentSourceEdit)
                await database.RotateRevisionConcurrentlyAsync(SourceTitleId);
            return database.Failure is Failure.Commit or Failure.CancelAtCommit
                ? new FaultingTransaction(database, inner, transaction)
                : transaction;
        }
    }

    private sealed class FaultingTransaction(TestDatabase database, IUnitOfWork unitOfWork, ITransaction inner)
        : ITransaction
    {
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await unitOfWork.FlushAsync(cancellationToken);
            await inner.RollbackAsync(CancellationToken.None);
            if (database.Failure == Failure.CancelAtCommit)
            {
                await database.CancellationSource.CancelAsync();
                throw new OperationCanceledException(database.Cancellation);
            }

            throw new InvalidOperationException("Injected commit failure.");
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    ///     Probes, just before the write reads its response, whether the write still holds the title row. While it
    ///     does, a later writer started here cannot commit first; once it does not, the later writer is allowed to
    ///     commit before the read, so a write that reads after its own commit would see it.
    /// </summary>
    private sealed class LaterWriterTitleDetailReader(TestDatabase database) : ITitleDetailReader
    {
        public Task<TitleDetailData?> GetPublicDetailAsync(int titleId, CancellationToken ct = default) => database.TitleDetails.GetPublicDetailAsync(titleId, ct);
        public Task<ErrorOr<Romd.Admin.Application.Titles.TitleReadPage<TitleMediaData>>> GetMediaPageAsync(int titleId, string? cursor, int limit, CancellationToken ct) => database.TitleDetails.GetMediaPageAsync(titleId, cursor, limit, ct);
        public Task<ErrorOr<Romd.Admin.Application.Titles.TitleReadPage<TitleReleaseData>>> GetReleasePageAsync(int titleId, string? cursor, int limit, CancellationToken ct) => database.TitleDetails.GetReleasePageAsync(titleId, cursor, limit, ct);
        public Task<ErrorOr<Romd.Admin.Application.Titles.TitleReadPage<TitleReleaseSourceData>>> GetReleaseSourcesPageAsync(int titleId, string? releaseId, string? cursor, int limit, CancellationToken ct) => database.TitleDetails.GetReleaseSourcesPageAsync(titleId, releaseId, cursor, limit, ct);

        public Task LaterWriter { get; private set; } = Task.CompletedTask;
        public Guid Written { get; private set; }
        public bool RowHeld { get; private set; }

        public async Task<TitleDetailData?> GetDetailAsync(int titleId, CancellationToken cancellationToken = default)
        {
            Written = await database.Context.Titles.Where(title => title.Id == titleId).Select(title => title.Revision)
                .SingleAsync(cancellationToken);
            RowHeld = await database.TitleRowLockedAsync(titleId);
            LaterWriter = database.WriteDescriptionConcurrentlyAsync("Later writer");
            if (!RowHeld)
                await LaterWriter;
            return await database.TitleDetails.GetDetailAsync(titleId, cancellationToken);
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly PostgreSqlTestDatabase _connection;
        private readonly DbContextOptions<RomdDbContext> _options;

        private TestDatabase(PostgreSqlTestDatabase connection, DbContextOptions<RomdDbContext> options,
            Failure failure, Guid initialRevision, Guid? sourceInitialRevision)
        {
            _connection = connection;
            _options = options;
            Failure = failure;
            InitialRevision = initialRevision;
            SourceInitialRevision = sourceInitialRevision;
            Context = new RomdDbContext(options);
            Titles = new TitleRepository(Context);
            TitleDetails = new Romd.Persistence.Queries.TitleDetailReader(Context);
            UnitOfWork = new FaultingUnitOfWork(this, new EfUnitOfWork(Context));
            Scheduler = new RematerializationScheduler(Context, TimeProvider.System);
            ReferenceCatalog = new ReferenceCatalogService(Context, TestReferenceBlobs.For(Context));
        }

        public Failure Failure { get; }
        public Guid InitialRevision { get; }
        public Guid? SourceInitialRevision { get; }

        /// <summary>The target title's validator as the client read it.</summary>
        public RevisionPrecondition IfMatch => RevisionPrecondition.For(InitialRevision.ToString());
        public RomdDbContext Context { get; }
        public TitleRepository Titles { get; }
        public Romd.Persistence.Queries.TitleDetailReader TitleDetails { get; }
        public IUnitOfWork UnitOfWork { get; }
        public RematerializationScheduler Scheduler { get; }
        public ReferenceCatalogService ReferenceCatalog { get; }
        public CancellationTokenSource CancellationSource { get; } = new();
        public CancellationToken Cancellation => CancellationSource.Token;

        public RomdDbContext CreateReadContext() => new(_options);

        public async Task RotateRevisionConcurrentlyAsync(int titleId)
        {
            await using var writer = CreateReadContext();
            await writer.Titles.Where(title => title.Id == titleId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(title => title.Revision, Guid.NewGuid()));
        }

        public async Task<Guid> StoredRevisionAsync()
        {
            await using var read = CreateReadContext();
            return await read.Titles.Where(title => title.Id == TitleId).Select(title => title.Revision).SingleAsync();
        }

        /// <summary>Whether another transaction holds the title row: <c>FOR UPDATE NOWAIT</c> fails with 55P03.</summary>
        public async Task<bool> TitleRowLockedAsync(int titleId)
        {
            await using var probe = CreateReadContext();
            await using var transaction = await probe.Database.BeginTransactionAsync();
            try
            {
                await probe.Database.ExecuteSqlRawAsync(
                    "SELECT \"Id\" FROM romd.\"Titles\" WHERE \"Id\" = {0} FOR UPDATE NOWAIT", titleId);
                return false;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return true;
            }
        }

        /// <summary>Commits another writer's visible change and revision on its own connection.</summary>
        public async Task WriteDescriptionConcurrentlyAsync(string description)
        {
            await using var writer = CreateReadContext();
            await writer.Titles.Where(title => title.Id == TitleId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(title => title.Description, description)
                    .SetProperty(title => title.Revision, Guid.NewGuid()));
        }

        public static async Task<TestDatabase> CreateAsync(Failure failure = Failure.None, bool withSource = false)
        {
            var connection = PostgreSqlTestDatabase.Create();
            var options = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(connection.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            await using (var seed = new RomdDbContext(options))
            {
                Seed(seed, withSource);
                await seed.SaveChangesAsync();
            }

            await using var read = new RomdDbContext(options);
            var revisions = await read.Titles.Where(title => title.Id == TitleId || title.Id == SourceTitleId)
                .ToDictionaryAsync(title => title.Id, title => title.Revision);
            return new TestDatabase(connection, options, failure, revisions[TitleId],
                revisions.TryGetValue(SourceTitleId, out var source) ? source : null);
        }

        public async ValueTask DisposeAsync()
        {
            CancellationSource.Dispose();
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static void Seed(RomdDbContext context, bool withSource)
        {
            var now = DateTimeOffset.UtcNow;
            context.Platforms.Add(new PlatformEntity
            {
                Id = 3,
                Name = "Super Nintendo",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
                BuiltInVersion = 1,
                BaseName = "Super Nintendo",
                BaseCompactLabel = "Super Nintendo",
                CanonicalKey = "snes",
                ShortName = "snes",
                CreatedAt = now
            });
            context.Titles.Add(new TitleEntity
            {
                Id = TitleId,
                PlatformId = 3,
                Name = "Chrono",
                NormalizedName = "chrono",
                Description = "Curated description",
                EnrichmentStatus = nameof(EnrichmentStatus.LowConfidence),
                CatalogState = TitleCatalogState.UserOnly,
                HasLocalPayload = true,
                FieldProvenanceJson = """{"Description":"user"}""",
                FieldSourceOverridesJson = """{"Genre":"user"}""",
                ScreenshotPrefsJson = """{"preserve":true}""",
                CreatedAt = now
            });
            context.TrackedTitles.Add(new TrackedTitleEntity { TitleId = TitleId, CreatedAt = now, UpdatedAt = now });
            for (var id = 1; id <= 4; id++)
            {
                context.Files.Add(new FileEntityPersistence
                {
                    Id = id,
                    Sha256 = Sha256.FromBytes(Enumerable.Repeat((byte)id, Sha256.ByteLength).ToArray()),
                    Size = 1,
                    SizeOnDisk = 1,
                    CreatedAt = now
                });
            }

            context.TitleMedia.AddRange(
                new TitleMediaEntity
                {
                    Id = 101,
                    TitleId = TitleId,
                    Type = nameof(MediaType.Cover),
                    FileId = 1,
                    SourceId = "igdb",
                    IsPrimary = true,
                    Attribution = "Artist",
                    CreatedAt = now
                },
                new TitleMediaEntity
                {
                    Id = 102,
                    TitleId = TitleId,
                    Type = nameof(MediaType.Cover),
                    FileId = 2,
                    SourceId = "user",
                    CreatedAt = now
                },
                new TitleMediaEntity
                {
                    Id = 103,
                    TitleId = TitleId,
                    Type = nameof(MediaType.Screenshot),
                    FileId = 3,
                    SourceId = "igdb",
                    IsPrimary = true,
                    CreatedAt = now
                });
            context.TitleExternalIds.AddRange(
                new TitleExternalIdEntity { TitleId = TitleId, Provider = "igdb", ExternalId = "123", MatchConfidence = 0.5f, CreatedAt = now },
                new TitleExternalIdEntity { TitleId = TitleId, Provider = "steamgriddb", ExternalId = "77", MatchConfidence = 0.9f, CreatedAt = now });
            context.TitleMetadataLayers.AddRange(
                new TitleMetadataLayerEntity
                {
                    TitleId = TitleId,
                    SourceId = "igdb",
                    SourceType = (int)MetadataSourceType.Provider,
                    MetadataJson = """{"Description":"provider"}""",
                    CreatedAt = now
                },
                new TitleMetadataLayerEntity
                {
                    TitleId = TitleId,
                    SourceId = "user",
                    SourceType = (int)MetadataSourceType.User,
                    MetadataJson = """{"Description":"Curated description"}""",
                    CreatedAt = now
                });
            context.TitleContentRatings.Add(new TitleContentRatingEntity
            {
                TitleId = TitleId,
                Board = 1,
                Code = "E",
                Designation = 1,
                SourceId = "igdb",
                CreatedAt = now
            });
            if (!withSource)
                return;

            context.Titles.Add(new TitleEntity
            {
                Id = SourceTitleId,
                PlatformId = 3,
                Name = "Chrono Source",
                NormalizedName = "chrono source",
                EnrichmentStatus = nameof(EnrichmentStatus.Completed),
                CreatedAt = now
            });
            context.TitleExternalIds.Add(new TitleExternalIdEntity
            {
                TitleId = SourceTitleId,
                Provider = "screenscraper",
                ExternalId = "5",
                MatchConfidence = 0.8f,
                CreatedAt = now
            });
            context.TitleMetadataLayers.Add(new TitleMetadataLayerEntity
            {
                TitleId = SourceTitleId,
                SourceId = "screenscraper",
                SourceType = (int)MetadataSourceType.Provider,
                MetadataJson = """{"Description":"Source description"}""",
                CreatedAt = now
            });
            context.TitleMedia.Add(new TitleMediaEntity
            {
                Id = 104,
                TitleId = SourceTitleId,
                Type = nameof(MediaType.Background),
                FileId = 4,
                SourceId = "screenscraper",
                IsPrimary = true,
                CreatedAt = now
            });
        }
    }
}
