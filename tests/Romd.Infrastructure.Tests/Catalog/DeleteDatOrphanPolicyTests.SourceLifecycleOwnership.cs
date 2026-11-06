using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.ApplyDatSubscription;
using Romd.Admin.Application.Source.Dat.Commands.CheckDatSubscription;
using Romd.Admin.Application.Source.Dat.Commands.StopDatSubscription;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Dat.Parsing;
using Romd.Dat.Parsing.Formats.Logiqx;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Dats;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.Subscriptions;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Catalog;

public sealed partial class DeleteDatOrphanPolicyTests
{
    private static readonly Guid ReviewedLibraryRevision = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string ReviewedCatalog = "redump/psx/discs";

    [Fact]
    public async Task SourcePreview_NonemptyReviewedGraph_PreservesExactV1TokenBytesAndReadOnlyBoundary()
    {
        await using var db = CreateDb();
        await SeedReviewedGraphAsync(db);
        await using var provider = BuildHandlerProvider(db, false);
        var preview = await ActivatorUtilities.CreateInstance<DatSourceManagement>(provider).PreviewAsync(7, "Delete", default);
        const string material = "v1:7:1:Delete:Active:False:DAT 7\nsubscription:1:\n7:17:Active\n9:19:Superseded\n201:1\n202:0\n203:3\n204:2\n";

        preview.Value.ReviewToken.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material))));
        (preview.Value.Versions, preview.Value.StillCovered, preview.Value.OwnedWithoutDefinition,
            preview.Value.PersonalWithoutDefinition, preview.Value.CatalogOnlyWithoutDefinition).ShouldBe((2, 1, 1, 1, 1));
        db.Database.CurrentTransaction.ShouldBeNull();
        await AssertReviewedGraphAsync(committed: false, "Delete");
    }

    [Theory]
    [InlineData("Delete")]
    [InlineData("Disabled")]
    [InlineData("Discontinued")]
    [InlineData("Active")]
    public async Task SourceApply_NonemptyGraph_CommitsRetainedIdentitiesProjectionLibraryAndOrderedIntents(string action)
    {
        await using var db = CreateDb();
        await SeedReviewedGraphAsync(db);
        await using var provider = BuildHandlerProvider(db, false);
        var preview = (await ActivatorUtilities.CreateInstance<DatSourceManagement>(provider).PreviewAsync(7, action, default)).Value;

        (await ApplySourceAsync(provider, 7, action, preview.ReviewToken, default)).IsError.ShouldBeFalse();

        await AssertReviewedGraphAsync(committed: true, action);
    }

    [Theory]
    [InlineData("Delete", false)]
    [InlineData("Delete", true)]
    [InlineData("Disabled", false)]
    [InlineData("Disabled", true)]
    [InlineData("Discontinued", false)]
    [InlineData("Discontinued", true)]
    [InlineData("Active", false)]
    [InlineData("Active", true)]
    public async Task SourceApply_FaultOrCancellationAfterRealOwnerFlush_RollsBackWholeNonemptyGraphAndIntents(string action, bool cancel)
    {
        await using var db = CreateDb();
        await SeedReviewedGraphAsync(db);
        using var cancellation = new CancellationTokenSource();
        var primary = new IOException("Injected after the real source lifecycle flush.");
        var work = new SourceCommitProbe(new EfUnitOfWork(db))
        {
            AfterFlush = async ct =>
            {
                // Mutations and outbox rows actually exist in this transaction, while observers see the old graph.
                (await db.AdminRealtimeOutboxEvents.CountAsync(ct)).ShouldBe(3);
                (await db.Libraries.SingleAsync(ct)).NeedsMaterialization.ShouldBeTrue();
                (await db.DatFiles.CountAsync(row => row.DatSourceId == 1, ct)).ShouldBe(action == "Delete" ? 0 : 2);
                await AssertReviewedGraphAsync(committed: false, action);
                if (cancel)
                { await cancellation.CancelAsync(); ct.ThrowIfCancellationRequested(); }
                throw primary;
            }
        };
        await using var provider = BuildHandlerProvider(db, false, work);
        var preview = (await ActivatorUtilities.CreateInstance<DatSourceManagement>(provider).PreviewAsync(7, action, default)).Value;
        Exception? failure = null;
        try
        { await ApplySourceAsync(provider, 7, action, preview.ReviewToken, cancellation.Token); }
        catch (Exception error) { failure = error; }

        if (cancel)
            failure.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        else
            failure.ShouldBeSameAs(primary);
        work.Committed.ShouldBeFalse();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await db.SaveChangesAsync();
        await AssertReviewedGraphAsync(committed: false, action);
    }

    [Fact]
    public async Task SourceApply_CommittedCleanupFailure_ReturnsDurableSuccessWithThreeIntents()
    {
        await using var db = CreateDb();
        await SeedReviewedGraphAsync(db);
        var work = new SourceCommitProbe(new EfUnitOfWork(db)) { CleanupFailure = new IOException("committed cleanup") };
        await using var provider = BuildHandlerProvider(db, false, work);
        var preview = (await ActivatorUtilities.CreateInstance<DatSourceManagement>(provider).PreviewAsync(7, "Delete", default)).Value;

        (await ApplySourceAsync(provider, 7, "Delete", preview.ReviewToken, default)).IsError.ShouldBeFalse();

        work.Committed.ShouldBeTrue();
        await AssertReviewedGraphAsync(committed: true, "Delete");
    }

    [Fact]
    public async Task SourceApply_CommitPending_ReturnsOnlyAfterTheWholeGraphIsDurable()
    {
        await using var db = CreateDb();
        await SeedReviewedGraphAsync(db);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new SourceCommitProbe(new EfUnitOfWork(db))
        { AfterFlush = async ct => { flushed.SetResult(); await release.Task.WaitAsync(ct); } };
        await using var provider = BuildHandlerProvider(db, false, work);
        var preview = (await ActivatorUtilities.CreateInstance<DatSourceManagement>(provider).PreviewAsync(7, "Delete", default)).Value;
        var operation = ApplySourceAsync(provider, 7, "Delete", preview.ReviewToken, cancellation.Token);
        Exception? failure = null;
        try
        {
            await flushed.Task.WaitAsync(cancellation.Token);
            operation.IsCompleted.ShouldBeFalse();
            await AssertReviewedGraphAsync(committed: false, "Delete");
            release.SetResult();
            (await operation).IsError.ShouldBeFalse();
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, operation); }
        await AssertReviewedGraphAsync(committed: true, "Delete");
    }

    [Theory]
    [InlineData("disc")]
    [InlineData("artwork")]
    public async Task SourceApply_ReviewCategoryIsCatalogOnly_StillUsesAuthoritativeDiscAndArtworkRetention(string retained)
    {
        await using var db = CreateDb();
        await SeedReviewedGraphAsync(db);
        if (retained == "disc")
            db.DiscSets.Add(new DiscSetEntity
            {
                CatalogSourceId = 1,
                PlatformId = PlatformId,
                TitleIds = [203],
                ProposalKey = "retained-disc",
                ProposalJson = "{}",
                Decision = "accepted"
            });
        else
            db.ArtworkAssets.Add(new ArtworkAssetEntity
            {
                TitleId = 203,
                Role = ArtworkRole.Hero,
                SourceId = "user",
                OriginalFileId = 17,
                ContentVersion = new string('a', 64),
                ContentType = "image/png",
                Width = 800,
                Height = 400,
                IsEligible = false,
                CreatedAt = DateTimeOffset.UnixEpoch
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await using var provider = BuildHandlerProvider(db, false);
        var preview = (await ActivatorUtilities.CreateInstance<DatSourceManagement>(provider).PreviewAsync(7, "Delete", default)).Value;
        preview.CatalogOnlyWithoutDefinition.ShouldBe(1);

        (await ApplySourceAsync(provider, 7, "Delete", preview.ReviewToken, default)).IsError.ShouldBeFalse();

        await using var read = CreateDb();
        (await read.Titles.SingleAsync(row => row.Id == 203)).CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        (await read.CatalogReleases.AnyAsync(row => row.Id == 902)).ShouldBeTrue();
        (await read.DatSources.AnyAsync(row => row.Id == 1)).ShouldBeFalse();
        if (retained == "disc")
            (await read.DiscSets.SingleAsync()).Decision.ShouldBe("accepted");
        else
            (await read.ArtworkAssets.SingleAsync()).TitleId.ShouldBe(203);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceApply_HeldCatalogOrSourceTryLock_ReturnsBusyAndReleasesItsEarlierLocks(bool catalog)
    {
        await using var db = CreateDb();
        await SeedReviewedGraphAsync(db);
        await using var provider = BuildHandlerProvider(db, false);
        var preview = (await ActivatorUtilities.CreateInstance<DatSourceManagement>(provider).PreviewAsync(7, "Delete", default)).Value;
        await using var hold = CreateDb();
        await using var holdProvider = BuildHandlerProvider(hold, false);
        await using var transaction = await new EfUnitOfWork(hold).BeginTransactionAsync();
        var locking = ActivatorUtilities.CreateInstance<DatSourceManagement>(holdProvider);
        (catalog ? await locking.TryLockCatalogAsync(ReviewedCatalog, default) : await locking.TryLockSourceAsync(1, default)).ShouldBeTrue();

        (await ApplySourceAsync(provider, 7, "Delete", "stale", default)).FirstError.Code.ShouldBe("SourceLifecycle.Busy");

        await AssertReviewedGraphAsync(committed: false, "Delete");
        if (!catalog)
        {
            await using var observer = CreateDb();
            await using var observerProvider = BuildHandlerProvider(observer, false);
            await using var boundary = await new EfUnitOfWork(observer).BeginTransactionAsync();
            (await ActivatorUtilities.CreateInstance<DatSourceManagement>(observerProvider).TryLockCatalogAsync(ReviewedCatalog, default)).ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData("check")]
    [InlineData("apply")]
    [InlineData("stop")]
    public async Task SourceApply_UncommittedDelete_ExcludesActualSubscriptionMutationsAndCannotResurrectItsSubscription(string competitor)
    {
        await using var db = CreateDb();
        await SeedReviewedGraphAsync(db);
        using var workspace = new SourceSubscriptionWorkspace();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new SourceCommitProbe(new EfUnitOfWork(db))
        { AfterFlush = async ct => { flushed.SetResult(); await release.Task.WaitAsync(ct); } };
        await using var provider = BuildHandlerProvider(db, false, work);
        var preview = (await ActivatorUtilities.CreateInstance<DatSourceManagement>(provider).PreviewAsync(7, "Delete", default)).Value;
        var operation = ApplySourceAsync(provider, 7, "Delete", preview.ReviewToken, cancellation.Token);
        Exception? failure = null;
        try
        {
            await flushed.Task.WaitAsync(cancellation.Token);
            await using var other = CreateDb();
            var client = Substitute.For<ISignedDatCatalogClient>();
            client.Enabled.Returns(true);
            var dats = new DatRepository(other, TimeProvider.System);
            var storage = Substitute.For<IFileStorageService>();
            var parser = new DatParser([new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance)]);
            var review = new DatReplacementReview(dats, storage, parser, parser);
            var enrollment = new DatCatalogEnrollmentService(other, client, dats, storage, review, TimeProvider.System);
            var owner = new EfUnitOfWork(other);
            var check = new CheckDatSubscriptionCommandHandler(enrollment, enrollment, dats, client, review, storage,
                owner, TimeProvider.System, NullLogger<CheckDatSubscriptionCommandHandler>.Instance);
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(workspace.Root);
            var apply = new ApplyDatSubscriptionCommandHandler(enrollment, enrollment, enrollment, dats, client, storage,
                review, review, new JobRepository(other, TimeProvider.System), new JobWorkspaceExclusion(options),
                new UploadWorkspacePreparation(options), new ReplaceDatWorkspacePreparation(options),
                new UploadJobRepository(other, TimeProvider.System), new ReplaceDatJobRepository(other, TimeProvider.System),
                owner, Substitute.For<ICurrentUser>(), TimeProvider.System, NullLogger<ApplyDatSubscriptionCommandHandler>.Instance);
            var stop = new StopDatSubscriptionCommandHandler(enrollment, owner, NullLogger<StopDatSubscriptionCommandHandler>.Instance);
            var error = competitor switch
            {
                "check" => (await check.HandleAsync(new(1), cancellation.Token)).FirstError,
                "apply" => (await apply.HandleAsync(new(1, "active", "candidate"), cancellation.Token)).FirstError,
                _ => (await stop.HandleAsync(new(1), cancellation.Token)).FirstError
            };
            error.Code.ShouldBe("DatEnrollment.Busy");
            await client.DidNotReceiveWithAnyArgs().DiscoverAsync(default);
            release.SetResult();
            (await operation).IsError.ShouldBeFalse();
            (await check.HandleAsync(new(1), cancellation.Token)).FirstError.Code.ShouldBe("DatEnrollment.NotFound");
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, operation); }
        await AssertReviewedGraphAsync(committed: true, "Delete");
    }

    private sealed class SourceSubscriptionWorkspace : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("romd-source-subscription-").FullName;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [Fact]
    public async Task SourceApply_PendingVersionCommitsWhileWaitingForTopology_IsBusyBeforeStale()
    {
        await using var db = CreateDb();
        await SeedReviewedGraphAsync(db);
        await using var provider = BuildHandlerProvider(db, false);
        var preview = (await ActivatorUtilities.CreateInstance<DatSourceManagement>(provider).PreviewAsync(7, "Delete", default)).Value;
        await using var writer = CreateDb();
        await using var transaction = await new EfUnitOfWork(writer).BeginTransactionAsync();
        await new DatRepository(writer, TimeProvider.System).AcquireMutationWriteLockAsync(7);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (operation, waited) = await _connection.StartWaitingForALockAsync(db,
            ct => ApplySourceAsync(provider, 7, "Delete", preview.ReviewToken, ct), cancellation.Token);
        Exception? failure = null;
        try
        {
            waited.ShouldBeTrue();
            SeedVersion(writer, 10, 1, 20, DatFileLifecycle.PendingActivation);
            await transaction.CommitAsync(cancellation.Token);
            (await operation).FirstError.Code.ShouldBe("SourceLifecycle.Busy");
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, operation); }
        await using var read = CreateDb();
        (await read.DatFiles.CountAsync(row => row.DatSourceId == 1)).ShouldBe(3);
        (await read.DatSubscriptions.SingleAsync()).DatSourceId.ShouldBe(1);
        (await read.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
        (await read.Libraries.SingleAsync()).NeedsMaterialization.ShouldBeFalse();
    }

    [Fact]
    public async Task SourceApply_UnrelatedStagedEntity_RemainsTrackedForLaterEdits()
    {
        await using var db = CreateDb();
        await SeedReviewedGraphAsync(db);
        await using var provider = BuildHandlerProvider(db, false);
        var preview = (await ActivatorUtilities.CreateInstance<DatSourceManagement>(provider).PreviewAsync(7, "Delete", default)).Value;
        var unrelated = new TitleEntity
        {
            Id = 205,
            PlatformId = PlatformId,
            Name = "Unrelated",
            NormalizedName = "unrelated",
            EnrichmentStatus = "None",
            Description = "Before acceptance",
            RetainWithoutCatalog = true,
            CreatedAt = DateTimeOffset.UnixEpoch
        };
        db.Titles.Add(unrelated);

        (await ApplySourceAsync(provider, 7, "Delete", preview.ReviewToken, default)).IsError.ShouldBeFalse();

        db.Entry(unrelated).State.ShouldBe(EntityState.Unchanged);
        unrelated.Description = "After acceptance";
        await using (var next = await new EfUnitOfWork(db).BeginTransactionAsync())
            await next.CommitAsync();
        await using var read = CreateDb();
        (await read.Titles.SingleAsync(row => row.Id == 205)).Description.ShouldBe("After acceptance");
    }

    [Fact]
    public async Task SourceStagedCapabilities_WithoutCallerTransaction_RejectBeforeReadingOrWriting()
    {
        await using var db = CreateDb();
        await using var provider = BuildHandlerProvider(db, false);
        var store = ActivatorUtilities.CreateInstance<DatSourceManagement>(provider);
        await Should.ThrowAsync<InvalidOperationException>(() => store.ReadRelatedCatalogIdsAsync(1, default));
        await Should.ThrowAsync<InvalidOperationException>(() => store.TryLockCatalogAsync(ReviewedCatalog, default));
        await Should.ThrowAsync<InvalidOperationException>(() => store.TryLockSourceAsync(1, default));
        await Should.ThrowAsync<InvalidOperationException>(() => store.LockAffectedTitlesAsync(1, default));
        await Should.ThrowAsync<InvalidOperationException>(() => store.ReadImpactAsync(7, 1, "DAT 7", "Delete", default));
        await Should.ThrowAsync<InvalidOperationException>(() => store.ReadVersionIdsAsync(1, default));
        await Should.ThrowAsync<InvalidOperationException>(() => store.BindRelatedSubscriptionsStagedAsync(1, default));
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    private static async Task SeedReviewedGraphAsync(RomdDbContext db)
    {
        SeedSourceWithVersion(db, 1, 1, 7, 17);
        SeedSourceWithVersion(db, 2, 2, 8, 18);
        SeedVersion(db, 9, 1, 19, DatFileLifecycle.Superseded);
        for (var i = 0; i < 4; i++)
        {
            SeedEntryWithGame(db, 100 + i, 1, 70 + i, 7, $"Game {i}");
            SeedLinkedTitle(db, 201 + i, 100 + i);
        }
        SeedEntryWithGame(db, 200, 2, 80, 8, "Shared");
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = 200, TitleId = 202 });
        db.DatGames.Add(NewGame(90, 9, 100));
        db.Titles.Local.Single(row => row.Id == 204).FieldSourceOverridesJson = "{\"Name\":\"manual\"}";
        db.StageOriginalRom(new OriginalRomFixture
        {
            Id = 90,
            FileId = SeedFile(db, 91),
            OriginalFilename = "owned.rom",
            Sha1 = Sha1.Parse(new string('a', 40)),
            Md5 = Md5.Parse(new string('b', 32)),
            Crc32 = Crc32.FromUInt32(1)
        });
        db.DatRoms.Add(new DatRomEntity { Id = 80, DatGameId = 70, Name = "owned.rom", Size = 1, RomFileId = 90 });
        db.DatSubscriptions.Add(new DatSubscriptionEntity { Id = 1, CatalogId = ReviewedCatalog, DatSourceId = 1, CandidateFileId = 17 });
        db.CatalogReleases.AddRange(
            new CatalogReleaseEntity { Id = 900, PlatformId = PlatformId, CatalogTitleId = 202, Fingerprint = "sha1:shared", Name = "Shared release", SourceCount = 2, FileCount = 1, SizeBytes = 1 },
            new CatalogReleaseEntity { Id = 902, PlatformId = PlatformId, CatalogTitleId = 203, Fingerprint = "sha1:stale", Name = "Stale release", SourceCount = 1, FileCount = 1, SizeBytes = 1 });
        db.CatalogReleaseSources.AddRange(
            new CatalogReleaseSourceEntity { CatalogReleaseId = 900, SourceEntryId = 101, ProviderClaimKey = "source:71", AssertedTitleId = 202 },
            new CatalogReleaseSourceEntity { CatalogReleaseId = 900, SourceEntryId = 200, ProviderClaimKey = "source:80", AssertedTitleId = 202 },
            new CatalogReleaseSourceEntity { CatalogReleaseId = 902, SourceEntryId = 102, ProviderClaimKey = "source:72", AssertedTitleId = 203 });
        db.CatalogReleaseFiles.AddRange(
            new CatalogReleaseFileEntity { CatalogReleaseId = 900, FileFingerprint = "sha1:shared-file", Name = "shared.rom", Size = 1, Sha1 = Sha1.Parse(new string('a', 40)) },
            new CatalogReleaseFileEntity { CatalogReleaseId = 902, FileFingerprint = "name:stale", Name = "stale.rom", Size = 1 });
        db.Libraries.Add(new LibraryEntity
        {
            Id = 1,
            Name = "Reviewed library",
            ConfigurationJson = JsonSerializer.Serialize(new LibraryConfiguration { AllowedPlatformIds = [PlatformId], IncludeUntrackedTitles = true }),
            MaterializationRevision = ReviewedLibraryRevision,
            MaterializationGeneration = 1,
            ItemCount = 2,
            LastMaterializedAt = DateTimeOffset.UnixEpoch
        });
        db.MaterializedLibraryReleases.AddRange(new[] { (Title: 202, Release: 900, Game: 71), (Title: 203, Release: 902, Game: 72) }.Select(row => new MaterializedLibraryReleaseEntity
        {
            LibraryId = 1,
            TitleId = row.Title,
            CatalogReleaseId = row.Release,
            DatGameId = row.Game,
            DatFileId = 7,
            PlatformId = PlatformId,
            IsOwned = true,
            IsComplete = true,
            IsEligible = true,
            IsPlayable = true,
            IsExposed = true
        }));
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private async Task AssertReviewedGraphAsync(bool committed, string action)
    {
        await using var read = CreateDb();
        bool deleted = committed && action == "Delete";
        (await read.DatFiles.CountAsync(row => row.DatSourceId == 1)).ShouldBe(deleted ? 0 : 2);
        (await read.DatSources.AnyAsync(row => row.Id == 1)).ShouldBe(!deleted);
        (await read.DatSubscriptions.AnyAsync(row => row.Id == 1)).ShouldBe(!deleted);
        (await read.SourceEntries.CountAsync(row => row.CatalogSourceId == 1)).ShouldBe(deleted ? 0 : 4);
        (await read.Titles.AnyAsync(row => row.Id == 203)).ShouldBe(!deleted);
        (await read.Titles.SingleAsync(row => row.Id == 201)).RetainWithoutCatalog.ShouldBe(committed);
        (await read.Titles.SingleAsync(row => row.Id == 204)).FieldSourceOverridesJson.ShouldBe("{\"Name\":\"manual\"}");
        (await read.RomFiles.AnyAsync(row => row.Id == 90)).ShouldBeTrue();
        (await read.Files.AnyAsync(row => row.Id == 91)).ShouldBeTrue();
        (await read.CatalogReleases.SingleAsync(row => row.Id == 900)).CatalogTitleId.ShouldBe(202);
        (await read.CatalogReleases.AnyAsync(row => row.Id == 902)).ShouldBe(!deleted);
        (await read.MaterializedLibraryReleases.CountAsync()).ShouldBe(deleted ? 1 : 2);
        var library = await read.Libraries.SingleAsync();
        library.NeedsMaterialization.ShouldBe(committed);
        if (committed)
            library.MaterializationRevision.ShouldNotBe(ReviewedLibraryRevision);
        else
            library.MaterializationRevision.ShouldBe(ReviewedLibraryRevision);
        (await read.Platforms.SingleAsync()).CatalogRebuildState.ShouldBe(committed ? CatalogRebuildState.Dirty : CatalogRebuildState.Clean);
        if (!deleted)
            (await read.CatalogSources.SingleAsync(row => row.Id == 1)).Status.ShouldBe(committed ? action : "Active");
        (await read.AdminRealtimeOutboxEvents.OrderBy(row => row.Id).Select(row => row.EventType).ToArrayAsync()).ShouldBe(committed
            ? new[] { AdminRealtimeEventTypes.CoverageStatsChanged, AdminRealtimeEventTypes.HealthStatsChanged, AdminRealtimeEventTypes.StorageStatsChanged } : []);
    }

    private sealed class SourceCommitProbe(IUnitOfWork inner) : IUnitOfWork
    {
        public Func<CancellationToken, Task>? AfterFlush { get; init; }
        public Exception? CleanupFailure { get; init; }
        public bool Committed { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(this, await inner.BeginTransactionAsync(cancellationToken));
        private sealed class Transaction(SourceCommitProbe owner, ITransaction transaction) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await owner.FlushAsync(cancellationToken);
                if (owner.AfterFlush is not null)
                    await owner.AfterFlush(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                owner.Committed = true;
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public async ValueTask DisposeAsync()
            {
                await transaction.DisposeAsync();
                if (owner.Committed && owner.CleanupFailure is { } error)
                    throw error;
            }
        }
    }
}
