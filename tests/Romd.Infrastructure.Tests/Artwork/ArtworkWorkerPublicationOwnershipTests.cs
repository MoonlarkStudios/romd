using System.Net;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Artwork;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Tests.Enrichment.Helpers;
using Romd.Persistence;
using Romd.Persistence.Artwork;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class ArtworkWorkerPublicationOwnershipTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "romd-worker-artwork", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;
    private IContentAddressableStore Cas => _services.GetRequiredService<IContentAddressableStore>();

    public ArtworkWorkerPublicationOwnershipTests() => _services = new ServiceCollection()
        .AddContentAddressableStorage(options => options.RootPath = _root).BuildServiceProvider();

    public async Task InitializeAsync()
    {
        await using var db = _database.CreateContext();
        db.Platforms.Add(new PlatformEntity
        {
            Id = 7,
            Name = "Worker artwork",
            ShortName = "worker-artwork",
            CanonicalKey = "worker-artwork",
            BaseName = "Worker artwork",
            BaseCompactLabel = "Worker artwork",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1
        });
        db.Titles.Add(new TitleEntity
        {
            Id = 7,
            PlatformId = 7,
            Name = "Game",
            NormalizedName = "game",
            EnrichmentStatus = "None"
        });
        await db.SaveChangesAsync();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.FillPosters, true).SetProperty(row => row.FillHeroes, false)
            .SetProperty(row => row.FillLogos, false).SetProperty(row => row.FillBackdrops, false));
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("automatic")]
    [InlineData("linked")]
    [InlineData("import")]
    public async Task Publication_NewFiles_FlushesIdentitiesBeforeReferencesAndCommitsTheBatch(string worker)
    {
        await using var rig = await CreateAsync(worker);
        bool stagedUnrelated = false;
        rig.Work.AfterIdentityFlush = async token =>
        {
            stagedUnrelated.ShouldBeTrue();
            rig.Db.ChangeTracker.Entries<ArtworkAssetEntity>().ShouldBeEmpty();
            (await rig.Db.Platforms.AsNoTracking().SingleAsync(token)).Manufacturer.ShouldBe("Staged alongside artwork");
            await using var observer = _database.CreateContext();
            (await observer.Files.CountAsync(token)).ShouldBe(0);
            (await observer.ArtworkAssets.CountAsync(token)).ShouldBe(0);
            (await observer.Platforms.SingleAsync(token)).Manufacturer.ShouldBeNull();
        };
        rig.DuringDownload = async () =>
        {
            rig.Db.Database.CurrentTransaction.ShouldBeNull();
            var unrelated = await rig.Db.Platforms.AsTracking().SingleAsync();
            unrelated.Manufacturer = "Staged alongside artwork";
            stagedUnrelated = true;
        };

        await rig.RunAsync();

        rig.Work.IdentityFlushCount.ShouldBe(1);
        await AssertPublishedAsync(worker, 1, 2);
        await using var read = _database.CreateContext();
        (await read.Platforms.SingleAsync()).Manufacturer.ShouldBe("Staged alongside artwork");
        (await read.ArtworkVariants.CountAsync()).ShouldBe(2);
        // Original and two variants are one publication batch; identical variants share one identity.
        var variants = await read.ArtworkVariants.OrderBy(row => row.Name).ToListAsync();
        variants.Select(row => row.Name).ShouldBe(["card", "thumb"]);
        variants.Select(row => row.FileId).Distinct().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData("automatic", false)]
    [InlineData("automatic", true)]
    [InlineData("linked", false)]
    [InlineData("linked", true)]
    [InlineData("import", false)]
    [InlineData("import", true)]
    public async Task Publication_FaultOrCancellationAfterIdentityFlush_RollsBackFilesReferencesAndOutcome(string worker, bool cancel)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var rig = await CreateAsync(worker, cancellation.Token);
        var failure = new InvalidOperationException("Controlled failure after worker identity flush");
        bool stagedUnrelated = false;
        rig.Work.AfterIdentityFlush = async token =>
        {
            stagedUnrelated.ShouldBeTrue();
            (await rig.Db.Platforms.AsNoTracking().SingleAsync(token)).Manufacturer.ShouldBe("Must roll back");
            await using var observer = _database.CreateContext();
            (await observer.Platforms.SingleAsync(token)).Manufacturer.ShouldBeNull();
            if (!cancel)
                throw failure;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        };
        rig.DuringDownload = async () =>
        {
            rig.Db.Database.CurrentTransaction.ShouldBeNull();
            var unrelated = await rig.Db.Platforms.AsTracking().SingleAsync();
            unrelated.Manufacturer = "Must roll back";
            stagedUnrelated = true;
        };

        if (cancel)
            await Should.ThrowAsync<OperationCanceledException>(rig.RunAsync);
        else
            (await Should.ThrowAsync<InvalidOperationException>(rig.RunAsync)).ShouldBeSameAs(failure);

        rig.Work.IdentityFlushCount.ShouldBe(1);
        rig.Db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var observer = _database.CreateContext();
        (await observer.Files.CountAsync()).ShouldBe(0);
        (await observer.ArtworkAssets.CountAsync()).ShouldBe(0);
        (await observer.ArtworkVariants.CountAsync()).ShouldBe(0);
        (await observer.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
        (await observer.Set<ArtworkAcquisitionEntity>().AnyAsync(row => row.Status == "Updated")).ShouldBeFalse();
        (await observer.Platforms.SingleAsync()).Manufacturer.ShouldBeNull();
        if (rig.Import is { } import)
        {
            import.RetainedAssetId.ShouldBeNull();
            var durable = await observer.Jobs.OfType<ArtworkImportJobEntity>().SingleAsync();
            durable.RetainedAssetId.ShouldBeNull();
            durable.WasSuperseded.ShouldBeFalse();
            (await observer.ArtworkSelections.SingleAsync()).PendingRequestId.ShouldBe(import.Id);
        }
        // CAS publication may leave only unreferenced bytes after rollback, never durable references.
        (await rig.Storage.PruneOrphanedFilesAsync(default)).ShouldBe(2);
    }

    [Theory]
    [InlineData("automatic")]
    [InlineData("linked")]
    [InlineData("import")]
    public async Task Publication_PreexistingFiles_DeduplicatesWithoutAnIdentityFlush(string worker)
    {
        await using var rig = await CreateAsync(worker);
        await using (var transaction = await new EfUnitOfWork(rig.Db).BeginTransactionAsync())
        {
            await using var source = new MemoryStream(Original(ArtworkRole.Poster));
            await using var variant = new MemoryStream(Variant(ArtworkRole.Poster));
            await using var first = await rig.Storage.StageAsync(source);
            await using var second = await rig.Storage.StageAsync(variant);
            var publication = await rig.Storage.PublishStagedAsync([first, second]);
            await new EfUnitOfWork(rig.Db).FlushAsync();
            publication.ReadAfterFlush().Count.ShouldBe(2);
            await transaction.CommitAsync();
        }
        rig.Db.ChangeTracker.Clear();

        await rig.RunAsync();

        rig.Work.IdentityFlushCount.ShouldBe(0);
        await AssertPublishedAsync(worker, 1, 2);
    }

    [Theory]
    [InlineData("automatic")]
    [InlineData("linked")]
    [InlineData("import")]
    public async Task Publication_SelectionChangesDuringDownload_DoesNotPublishSupersededBytes(string worker)
    {
        await using var rig = await CreateAsync(worker);
        rig.DuringDownload = async () =>
        {
            await using var writer = _database.CreateContext();
            await writer.ArtworkSelections.Where(row => row.TitleId == 7 && row.Role == ArtworkRole.Poster)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Revision, row => row.Revision + 1)
                    .SetProperty(row => row.PendingRequestId, (Guid?)null));
        };

        await rig.RunAsync();

        rig.Work.IdentityFlushCount.ShouldBe(0);
        await using var read = _database.CreateContext();
        (await read.Files.CountAsync()).ShouldBe(0);
        (await read.ArtworkAssets.CountAsync()).ShouldBe(0);
        if (rig.Import is { } import)
            import.WasSuperseded.ShouldBeTrue();
        else
            (await read.Set<ArtworkAcquisitionEntity>().SingleAsync(row => row.Role == ArtworkRole.Poster)).Status.ShouldBe("Superseded");
    }

    [Theory]
    [InlineData("automatic")]
    [InlineData("linked")]
    [InlineData("import")]
    public async Task Publication_FenceChangesDuringDownload_RejectsOldOwnerBeforeFilePublication(string worker)
    {
        await using var rig = await CreateAsync(worker);
        rig.DuringDownload = async () =>
        {
            await using var writer = _database.CreateContext();
            await writer.Jobs.Where(row => row.Id == rig.JobId).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.ExecutionFenceToken, Guid.NewGuid()));
        };

        await Should.ThrowAsync<JobExecutionOwnershipLostException>(rig.RunAsync);

        rig.Work.IdentityFlushCount.ShouldBe(0);
        await using var read = _database.CreateContext();
        (await read.Files.CountAsync()).ShouldBe(0);
        (await read.ArtworkAssets.CountAsync()).ShouldBe(0);
        (await read.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("automatic")]
    [InlineData("linked")]
    public async Task Publication_LaterRoleFails_PreservesCommittedFirstRoleAndReplayFillsOnlyTheMissingRole(string worker)
    {
        await using (var settings = _database.CreateContext())
            await settings.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(setters => setters.SetProperty(row => row.FillHeroes, true));
        await using var rig = await CreateAsync(worker, includeHero: true);
        var failure = new InvalidOperationException("Controlled second-role identity failure");
        rig.Work.AfterIdentityFlush = _ => rig.Work.IdentityFlushCount == 2 ? Task.FromException(failure) : Task.CompletedTask;

        (await Should.ThrowAsync<InvalidOperationException>(rig.RunAsync)).ShouldBeSameAs(failure);

        await AssertPublishedAsync(worker, 1, 2);
        int firstPosterId;
        await using (var read = _database.CreateContext())
        {
            var poster = await read.ArtworkAssets.SingleAsync();
            poster.Role.ShouldBe(ArtworkRole.Poster);
            firstPosterId = poster.Id;
        }
        rig.Work.AfterIdentityFlush = null;
        await rig.RunAsync();
        rig.Work.IdentityFlushCount.ShouldBe(3);
        await AssertPublishedAsync(worker, 2, 4, updatedAcquisitions: worker == "automatic" ? 1 : 2);
        await using var verification = _database.CreateContext();
        (await verification.ArtworkAssets.SingleAsync(row => row.Role == ArtworkRole.Poster)).Id.ShouldBe(firstPosterId);
        (await verification.Set<ArtworkAcquisitionEntity>().SingleAsync(row => row.Role == ArtworkRole.Poster)).Status
            .ShouldBe(worker == "automatic" ? "AlreadyPresent" : "Updated");
        (await verification.Set<ArtworkAcquisitionEntity>().SingleAsync(row => row.Role == ArtworkRole.Hero)).Status.ShouldBe("Updated");
    }

    private async Task AssertPublishedAsync(string worker, int assets, int files, int? updatedAcquisitions = null)
    {
        await using var read = _database.CreateContext();
        (await read.Files.CountAsync()).ShouldBe(files);
        (await read.ArtworkAssets.CountAsync()).ShouldBe(assets);
        foreach (var row in await read.Files.ToListAsync())
            (await Cas.VerifyAsync(StorageKey.FromHash(row.Sha256))).ShouldBeTrue();
        if (worker == "import")
        {
            var job = await read.Jobs.OfType<ArtworkImportJobEntity>().SingleAsync();
            var selection = await read.ArtworkSelections.SingleAsync();
            selection.Mode.ShouldBe(ArtworkSelectionMode.Pinned);
            selection.PinnedAssetId.ShouldBe(job.RetainedAssetId);
            selection.PendingRequestId.ShouldBeNull();
            job.WasSuperseded.ShouldBeFalse();
        }
        else
        {
            (await read.ArtworkSelections.AllAsync(row => row.Mode == ArtworkSelectionMode.Automatic && row.PinnedAssetId == null)).ShouldBeTrue();
            (await read.Set<ArtworkAcquisitionEntity>().CountAsync(row => row.Status == "Updated")).ShouldBe(updatedAcquisitions ?? assets);
        }
    }

    private async Task<Rig> CreateAsync(string worker, CancellationToken ct = default, bool includeHero = false)
    {
        var db = _database.CreateContext();
        var fence = Guid.NewGuid();
        var now = TimeProvider.System.GetUtcNow();
        ArtworkImportJob? import = null;
        JobEntity job;
        if (worker == "import")
        {
            import = ArtworkImportJob.Create(Guid.NewGuid(), "Game", 7, 7, ArtworkRole.Poster, 1,
                "steamgriddb", "42", "poster", "https://cdn2.steamgriddb.com/grid/poster.png", "Artist", now);
            import.Start("delivery", now);
            job = ArtworkImportJobEntity.FromDomain(import, now);
        }
        else
        {
            var enrichment = EnrichmentJob.Create("Game", 7, 7, now, artworkOnly: true);
            enrichment.Start("delivery", now);
            job = EnrichmentJobEntity.FromDomain(enrichment, now);
        }
        job.ExecutionFenceToken = fence;
        job.ExecutionLeaseExpiresAtUtc = now.AddHours(1);
        db.Jobs.Add(job);
        db.ArtworkSelections.Add(new ArtworkSelectionEntity
        {
            TitleId = 7,
            Role = ArtworkRole.Poster,
            Revision = import is null ? 0 : 1,
            Mode = ArtworkSelectionMode.Automatic,
            PendingRequestId = import?.Id
        });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        var storage = new FileStorageService(Cas, new FileRepository(db), new EfUnitOfWork(db), new FileMutationLock(db),
            new AdminRealtimeOutbox(db, TimeProvider.System), NullLogger<FileStorageService>.Instance, TimeProvider.System);
        return new Rig(db, storage, worker, job.Id, fence, import, ct, includeHero);
    }

    private static byte[] Original(ArtworkRole role) => role == ArtworkRole.Poster ? [1, 2, 3] : [7, 8, 9];
    private static byte[] Variant(ArtworkRole role) => role == ArtworkRole.Poster ? [4, 5, 6] : [10, 11, 12];

    private sealed class Rig : IAsyncDisposable
    {
        public RomdDbContext Db { get; }
        public FileStorageService Storage { get; }
        public ObservedWork Work { get; }
        public Guid JobId { get; }
        public ArtworkImportJob? Import { get; }
        public Func<Task>? DuringDownload { get; set; }
        private readonly string _worker;
        private readonly Guid _fence;
        private readonly CancellationToken _ct;
        private readonly bool _includeHero;

        public Rig(RomdDbContext db, FileStorageService storage, string worker, Guid jobId, Guid fence,
            ArtworkImportJob? import, CancellationToken ct, bool includeHero)
        {
            Db = db;
            Storage = storage;
            Work = new ObservedWork(db);
            _worker = worker;
            JobId = jobId;
            _fence = fence;
            Import = import;
            _ct = ct;
            _includeHero = includeHero;
        }

        public async Task RunAsync()
        {
            using var fence = JobExecutionFenceScope.Enter(JobId, _fence, TimeProvider.System);
            var guard = new JobExecutionMutationGuard(Db, Work, TimeProvider.System, new JobExecutionClaimOptions());
            var context = new JobContext(null, _ => Task.CompletedTask, _ct,
                ownedMutationDelegate: (mutation, token) => guard.ExecuteAsync(JobId, _fence, mutation, token), jobId: JobId);
            var curation = new ArtworkCurationRepository(Db, TimeProvider.System);
            var acquisition = new ArtworkAcquisitionStore(Db, curation, TimeProvider.System);
            var processor = Substitute.For<IArtworkImageProcessor>();
            processor.ProcessAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ArtworkRole>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Db.Database.CurrentTransaction.ShouldBeNull();
                    var role = call.Arg<ArtworkRole>();
                    return new ProcessedArtworkImage("image/png", role == ArtworkRole.Poster ? 600 : 1920,
                        role == ArtworkRole.Poster ? 900 : 620,
                        [new("card", "image/png", 300, 200, Variant(role)), new("thumb", "image/png", 150, 100, Variant(role))]);
                });
            var source = Substitute.For<IArtworkAssetSource>();
            source.DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ArtworkRole>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    Db.Database.CurrentTransaction.ShouldBeNull();
                    if (DuringDownload is { } action)
                        await action();
                    return (ErrorOr<DownloadedArtworkAsset>)new DownloadedArtworkAsset(Original(call.Arg<ArtworkRole>()), "image/png", "Artist", null);
                });
            if (Import is { } import)
            {
                await new ArtworkImportJobExecutor(source, processor, Storage, curation, Work,
                    new ArtworkImportJobRepository(Db, TimeProvider.System)).ExecuteAsync(import, context);
                return;
            }
            var title = (await new TitleRepository(Db).GetAggregateAsync(7, _ct))!;
            var platform = Platform.Rehydrate(7, "Worker artwork", "worker-artwork", null, DateTimeOffset.UnixEpoch);
            var options = Options.Create(new EnrichmentOptions());
            var roles = _includeHero ? new[] { ArtworkRole.Poster, ArtworkRole.Hero } : [ArtworkRole.Poster];
            if (_worker == "automatic")
            {
                using var handler = new DownloadHandler(this);
                var clients = Substitute.For<IHttpClientFactory>();
                clients.CreateClient(AutomaticArtworkService.HttpClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));
                var result = EnrichmentResult.Found("42", 1, new EnrichmentData()) with
                {
                    Artwork = roles.Select(role => new EnrichmentArtworkCandidate(role.ToString().ToLowerInvariant(), role,
                        $"https://images.igdb.com/igdb/image/upload/t_original/{role}.png", role == ArtworkRole.Poster ? 600 : 1920,
                        role == ArtworkRole.Poster ? 900 : 620)).ToArray()
                };
                var provider = new FakeMetadataProvider("igdb", _ => result);
                await new AutomaticArtworkService(acquisition, curation, processor, Storage, Work, [provider],
                    Substitute.For<IEnrichmentContextFactory>(), options, clients, NullLogger<AutomaticArtworkService>.Instance)
                    .FillAsync(title, platform, context, _ct, [new("igdb", result)]);
            }
            else
            {
                var revision = Guid.NewGuid();
                var matches = Substitute.For<ITitleProviderMatchService>();
                matches.GetAsync(7, Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From<IReadOnlyList<TitleProviderMatch>>(value:
                    [new("steamgriddb", "SteamGridDB", true, ["artwork"], revision, "Confirmed", "42", null, null, null, false)]));
                matches.GetLinkRevisionAsync(7, "steamgriddb", "42", Arg.Any<CancellationToken>()).Returns(revision);
                var browser = Substitute.For<IArtworkProviderBrowser>();
                browser.Capabilities.Returns(new ArtworkProviderCapabilities("steamgriddb", "SteamGridDB", roles.Select(role => new ArtworkRoleCapabilities(role, [], [])).ToArray()));
                browser.GetCandidatesAsync("42", Arg.Any<ProviderArtworkQuery>(), 0, Arg.Any<CancellationToken>()).Returns(call =>
                {
                    var role = call.Arg<ProviderArtworkQuery>().Role;
                    return new ProviderArtworkPage([new("42", role.ToString().ToLowerInvariant(), role, role == ArtworkRole.Poster ? 600 : 1920,
                        role == ArtworkRole.Poster ? 900 : 620, null, new("https://example.test/thumb.png"),
                        new($"https://cdn2.steamgriddb.com/grid/{role.ToString().ToLowerInvariant()}.png"), "Artist", "https://www.steamgriddb.com/game/42")], null);
                });
                await new LinkedProviderArtworkService([browser], source, matches, acquisition, curation, processor, Storage, Work, options)
                    .FillAsync(title, platform, context, _ct);
            }
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
        private sealed class DownloadHandler(Rig rig) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                rig.Db.Database.CurrentTransaction.ShouldBeNull();
                if (rig.DuringDownload is { } action)
                    await action();
                var role = request.RequestUri!.AbsolutePath.Contains("Poster", StringComparison.Ordinal) ? ArtworkRole.Poster : ArtworkRole.Hero;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Original(role)) };
            }
        }
    }

    private sealed class ObservedWork(RomdDbContext db) : IUnitOfWork
    {
        private readonly IUnitOfWork _inner = new EfUnitOfWork(db);
        public int IdentityFlushCount { get; private set; }
        public Func<CancellationToken, Task>? AfterIdentityFlush { get; set; }
        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => _inner.BeginTransactionAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => _inner.ConflictOf(error);
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            bool identity = db.ChangeTracker.Entries<FileEntityPersistence>().Any(entry => entry.State == EntityState.Added);
            await _inner.FlushAsync(cancellationToken);
            if (!identity)
                return;
            IdentityFlushCount++;
            if (AfterIdentityFlush is { } action)
                await action(cancellationToken);
        }
    }
}
