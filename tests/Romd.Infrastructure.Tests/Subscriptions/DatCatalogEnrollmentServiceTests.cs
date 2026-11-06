using System.Security.Cryptography;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Dat.Commands.CreateDatSubscription;
using Romd.Admin.Application.Source.Dat.Commands.CheckDatSubscription;
using System.Text;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenIddict.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.StopDatSubscription;
using Romd.Admin.Application.Source.Dat.Commands.ApplyDatSubscription;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Jobs;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Security;
using Romd.Dat.Parsing;
using Romd.Dat.Parsing.Formats.Logiqx;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Dats;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.Persistence.Subscriptions;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Subscriptions;

public sealed partial class DatCatalogEnrollmentServiceTests
{
    private const string Catalog = "redump/psx/discs";
    private sealed class Client : ISignedDatCatalogClient
    {
        public bool Enabled { get; set; } = true;
        public bool Fail { get; set; }
        public bool FailDirectory { get; set; }
        public string Health { get; set; } = "healthy";
        public bool MissingArtifact { get; set; }
        public byte[] Bytes { get; set; } = Encoding.UTF8.GetBytes("<datafile><header><name>Sony - PlayStation</name></header><game name='Test Disc'><rom name='track.bin' size='1' crc='11111111'/></game></datafile>");
        public int Fetches { get; private set; }
        public int Discoveries { get; private set; }
        public Func<Task>? BeforeFetch { get; set; }
        public async Task<ErrorOr<byte[]>> FetchAsync(string id, string system, string name, CancellationToken ct)
        {
            Fetches++;
            if (BeforeFetch is not null)
                await BeforeFetch();
            return Fail ? Error.Failure("test", "private diagnostic") : Bytes;
        }
        public Task<ErrorOr<IReadOnlyList<PublishedDatCatalog>>> DiscoverAsync(CancellationToken ct)
        {
            Discoveries++;
            return Task.FromResult<ErrorOr<IReadOnlyList<PublishedDatCatalog>>>(FailDirectory ? Error.Failure("test", "failed") : new[] {
                new PublishedDatCatalog(Catalog, "psx", "Sony - PlayStation", "redump", Health, MissingArtifact ? null : Convert.ToHexStringLower(SHA256.HashData(Bytes)), 1, 1, DateTimeOffset.UtcNow) });
        }
    }
    [Fact]
    public async Task Check_FailedRefreshWithRetainedArtifact_PreparesVerifiedReview()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        h.Client.Health = "failed";
        var result = await h.CreateAsync(Catalog, null, default);
        result.IsError.ShouldBeFalse();
        result.Value.Created.ShouldBeTrue();
        result.Value.Subscription.State.ShouldBe("ReadyToImport");
        (await h.Db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Check_FailedRefreshWithoutArtifact_IsUnavailable()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        h.Client.Health = "failed";
        h.Client.MissingArtifact = true;
        (await h.CreateAsync(Catalog, null, default)).FirstError.Code.ShouldBe("DatEnrollment.Unavailable");
        (await h.Db.DatSubscriptions.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Check_RetainedArtifactFailsVerification_FailsClosed()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        h.Client.Health = "failed";
        h.Client.Fail = true;
        var result = (await h.CreateAsync(Catalog, null, default)).Value.Subscription;
        result.State.ShouldBe("CheckFailed");
        result.CandidateSha256.ShouldBeNull();
        (await h.Db.Jobs.CountAsync()).ShouldBe(0);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public RomdDbContext Db { get; }
        public Client Client { get; } = new();
        public DatCatalogEnrollmentService Service { get; }
        public IFileStorageService Storage { get; }
        public string ConnectionString { get; }
        public bool FailAfterJobSave { get; set; }
        public byte[]? ApprovedBytes { get; private set; }
        public IUnitOfWork? UnitOverride { get; set; }
        private readonly IDatRepository repository;
        private readonly DatReplacementReview review;
        private readonly string workspace = Directory.CreateTempSubdirectory("romd-subscription-fixture-").FullName;
        private readonly IRomdOptions options = Substitute.For<IRomdOptions>();
        private readonly TimeProvider time;
        private readonly Dictionary<int, byte[]> _contents = [];
        public Harness(PostgreSqlTestDatabase database, TimeProvider? time = null)
        {
            ConnectionString = database.ConnectionString;
            Db = new(new DbContextOptionsBuilder<RomdDbContext>().UseNpgsql(database.ConnectionString).UseOpenIddict().Options);
            this.time = time ?? TimeProvider.System;
            options.DataDirectory.Returns(workspace);
            var storage = Storage = Substitute.For<IFileStorageService>();
            storage.StageAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                using var copy = new MemoryStream();
                await call.Arg<Stream>().CopyToAsync(copy, call.Arg<CancellationToken>());
                return (IStagedFile)new StagedBytes(copy.ToArray());
            });
            storage.PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                var staged = (StagedBytes)call.Arg<IStagedFile>();
                var ct = call.Arg<CancellationToken>();
                await new FileMutationLock(Db).AcquireAsync(staged.Sha256, ct);
                var existing = await Db.Files.SingleOrDefaultAsync(file => file.Sha256 == staged.Sha256, ct);
                var creation = existing is null ? await new FileRepository(Db).AddStagedAsync(
                    Romd.Domain.Storage.FileEntity.CreateNew(staged.Sha256, staged.Size, staged.Size, false, DateTimeOffset.UnixEpoch), ct) : null;
                var publication = Substitute.For<IStagedFilePublication>();
                publication.RequiresFlush.Returns(creation?.RequiresFlush ?? false);
                publication.ReadAfterFlush().Returns(_ =>
                {
                    var file = creation?.ReadAfterFlush() ?? existing!.ToDomain();
                    _contents[file.Id] = staged.Bytes;
                    return new FileStoreResult(file, creation is not null, creation is null);
                });
                return publication;
            });
            storage.RetrieveByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
                Task.FromResult<Stream?>(_contents.TryGetValue(call.Arg<int>(), out var bytes) ? new MemoryStream(bytes, writable: false) : null));
            var parser = new DatParser([new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance)]);
            repository = new DatRepository(Db, TimeProvider.System);
            review = new DatReplacementReview(repository, storage, parser, parser);
            Service = new(Db, Client, repository, storage, review, this.time);
        }
        public Task<ErrorOr<DatSubscriptionEnrollment>> CreateAsync(string catalog, int? datId, CancellationToken ct) =>
            new CreateDatSubscriptionCommandHandler(Service, Service, repository, Client, review, Storage,
                UnitOverride ?? new EfUnitOfWork(Db), time, NullLogger<CreateDatSubscriptionCommandHandler>.Instance).HandleAsync(new(catalog, datId), ct);
        public Task<ErrorOr<DatCatalogSubscription>> CheckAsync(int id, CancellationToken ct) => CheckAsync(id, DatSubscriptionCheckMode.Manual, ct);
        public Task<ErrorOr<DatCatalogSubscription>> CheckScheduledAsync(int id, CancellationToken ct) => CheckAsync(id, DatSubscriptionCheckMode.Scheduled, ct);
        private Task<ErrorOr<DatCatalogSubscription>> CheckAsync(int id, DatSubscriptionCheckMode mode, CancellationToken ct) =>
            new CheckDatSubscriptionCommandHandler(Service, Service, repository, Client, review, Storage,
                UnitOverride ?? new EfUnitOfWork(Db), time, NullLogger<CheckDatSubscriptionCommandHandler>.Instance).HandleAsync(new(id, mode), ct);
        public async Task<ErrorOr<Guid>> ApplyAsync(int id, string active, string candidate, CancellationToken ct)
        {
            var handler = new ApplyDatSubscriptionCommandHandler(Service, Service, Service, repository, Client, Storage,
                review, review, new JobRepository(Db, time), new JobWorkspaceExclusion(options),
                new UploadWorkspacePreparation(options), new ReplaceDatWorkspacePreparation(options),
                new UploadJobRepository(Db, time), new ReplaceDatJobRepository(Db, time),
                new ApplyWork(UnitOverride ?? new EfUnitOfWork(Db), () => FailAfterJobSave),
                Substitute.For<ICurrentUser>(), time, NullLogger<ApplyDatSubscriptionCommandHandler>.Instance);
            var result = await handler.HandleAsync(new(id, active, candidate), ct);
            if (!result.IsError)
            {
                var job = await Db.Jobs.SingleAsync(job => job.Id == result.Value, ct);
                ApprovedBytes = await File.ReadAllBytesAsync(Path.Combine(workspace, "temp", "jobs", $"{job.Id:N}", job.SourceFilename), ct);
            }
            return result;
        }

        private sealed class ApplyWork(IUnitOfWork inner, Func<bool> failAfterFlush) : IUnitOfWork
        {
            public Task FlushAsync(CancellationToken ct = default) => inner.FlushAsync(ct);
            public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
            public async Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default) =>
                new Transaction(await inner.BeginTransactionAsync(ct), inner, failAfterFlush);
            private sealed class Transaction(ITransaction transaction, IUnitOfWork work, Func<bool> failAfterFlush) : ITransaction
            {
                public async Task CommitAsync(CancellationToken ct = default)
                {
                    await work.FlushAsync(ct);
                    if (failAfterFlush())
                        throw new IOException("Injected failure after durable job save");
                    await transaction.CommitAsync(ct);
                }
                public Task RollbackAsync(CancellationToken ct = default) => transaction.RollbackAsync(ct);
                public ValueTask DisposeAsync() => transaction.DisposeAsync();
            }
        }

        public async Task<DatCatalogSubscription> EnrollAsync() => (await CreateAsync(Catalog, null, default)).Value.Subscription;

        private sealed class StagedBytes : IStagedFile
        {
            public StagedBytes(byte[] bytes)
            {
                Bytes = bytes;
                Sha256 = Sha256.FromSpan(SHA256.HashData(bytes));
            }
            public byte[] Bytes { get; }
            public Sha256 Sha256 { get; }
            public long Size => Bytes.Length;
            public Md5? ArtifactMd5 => null;
            public Sha1? ArtifactSha1 => null;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        /// <summary>Adds a row the retired per-source tracking created: bound by source, without the catalog's system identity.</summary>
        public async Task<int> AddRetiredRowAsync(int sourceId, string state = "UpToDate", byte[]? candidate = null, string? activeSha256 = null)
        {
            var row = new DatSubscriptionEntity { CatalogId = Catalog, DatSourceId = sourceId, State = state, ActiveSha256 = activeSha256 };
            if (candidate is not null)
            {
                await using var source = new MemoryStream(candidate);
                row.CandidateFileId = (await Storage.StageAndCommitAsync(Db, source, default)).File.Id;
                row.CandidateSha256 = Convert.ToHexStringLower(SHA256.HashData(candidate));
            }
            Db.DatSubscriptions.Add(row);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return row.Id;
        }

        /// <summary>Installs an active source holding <paramref name="bytes" />, as an earlier import would.</summary>
        public async Task<(int DatId, int SourceId)> InstallAsync(byte[] bytes, string name = "Sony - PlayStation")
        {
            await using var source = new MemoryStream(bytes);
            var stored = await Storage.StageAndCommitAsync(Db, source, default);
            int platformId = await Db.Platforms.Where(p => p.CanonicalKey == "psx").Select(p => p.Id).SingleAsync();
            var dat = DatFile.CreateNew(name, "Catalog", DatType.Redump, "catalog.dat", stored.File.Id, now: DateTimeOffset.UnixEpoch, platformId);
            await new DatRepository(Db, TimeProvider.System).AddStagedAsync(dat, DatSource.CreateNew(now: DateTimeOffset.UnixEpoch));
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            var installed = await Db.DatFiles.Where(d => d.Name == name && d.FileId == stored.File.Id && d.Lifecycle == "Active")
                .OrderByDescending(d => d.Id).Select(d => new { d.Id, d.DatSourceId }).FirstAsync();
            return (installed.Id, installed.DatSourceId);
        }
        public async Task InitializeAsync() { await SharedReferenceDataSeeder.InitializeAsync(Db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.For(Db), clock: TimeProvider.System); Db.ChangeTracker.Clear(); }
        public async ValueTask DisposeAsync()
        {
            try
            { await Db.DisposeAsync(); }
            finally { Directory.Delete(workspace, recursive: true); }
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    [Fact]
    public async Task FirstEnrollment_RetiredAfterReview_PreventsApprovalWithoutRemovingIdentity()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        var status = await h.EnrollAsync();
        var preview = (await h.Service.PreviewAsync(status.Id, default)).Value;
        var definition = await h.Db.Platforms.AsTracking()
            .SingleAsync(x => x.CanonicalKey == "psx");
        definition.Retired = true;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        (await h.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default))
            .FirstError.Code.ShouldBe("DatEnrollment.PlatformMissing");
        (await h.CheckAsync(status.Id, default)).FirstError.Code.ShouldBe("DatEnrollment.PlatformMissing");
        (await h.Db.Platforms.AnyAsync(x => x.Id == status.PlatformId)).ShouldBeTrue();
        (await h.Db.Jobs.CountAsync()).ShouldBe(0);
        (await h.Db.DatSubscriptions.SingleAsync()).CandidateSha256.ShouldBe(preview.CandidateSha256);
    }

    [Fact]
    public async Task ScheduledCheck_FailuresBackOff_RecoveryWaitsForReviewWithoutActivation()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var clock = new Clock();
        await using var h = new Harness(database, clock);
        await h.InitializeAsync();
        h.Client.Fail = true;
        var status = await h.EnrollAsync();
        foreach (var hours in new[] { 1, 2, 4, 8, 16, 24, 24 })
        {
            status.State.ShouldBe("CheckFailed");
            status.NextCheckAt.ShouldBe(clock.GetUtcNow().AddHours(hours));
            (await h.Service.GetDueCheckIdsAsync(default)).ShouldBeEmpty();
            clock.Advance(TimeSpan.FromHours(hours));
            (await h.Service.GetDueCheckIdsAsync(default)).ShouldBe(new[] { status.Id });
            status = (await h.CheckScheduledAsync(status.Id, default)).Value;
        }
        h.Client.Fail = false;
        clock.Advance(TimeSpan.FromDays(1));
        status = (await h.CheckScheduledAsync(status.Id, default)).Value;
        status.State.ShouldBe("ReadyToImport");
        status.NextCheckAt.ShouldBeNull();
        status.AutomaticChecksPaused.ShouldBeFalse();
        var candidate = status.CandidateSha256;
        clock.Advance(TimeSpan.FromDays(3));
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBeEmpty();
        (await h.CheckScheduledAsync(status.Id, default)).FirstError.Code.ShouldBe("DatEnrollment.Busy");
        (await h.Service.GetAsync(status.Id, default)).Value.CandidateSha256.ShouldBe(candidate);
        (await h.Db.Jobs.CountAsync()).ShouldBe(0);
        (await h.Db.DatSubscriptions.SingleAsync()).ConsecutiveCheckFailures.ShouldBe(0);
    }

    [Fact]
    public async Task ScheduledCheck_RemovedSystemAndDisabledReaderPause_RestoringResumes()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var clock = new Clock();
        await using var h = new Harness(database, clock);
        await h.InitializeAsync();
        h.Client.Fail = true;
        var status = await h.EnrollAsync();
        clock.Advance(TimeSpan.FromHours(1));
        await h.Db.Platforms.Where(p => p.Id == status.PlatformId).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsEnabled, false));
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBeEmpty();
        var paused = (await h.Service.GetAsync(status.Id, default)).Value;
        paused.AutomaticChecksPaused.ShouldBeTrue();
        paused.NextCheckAt.ShouldBeNull();
        (await h.CheckScheduledAsync(status.Id, default)).FirstError.Code.ShouldBe("DatEnrollment.Busy");
        await h.Db.Platforms.Where(p => p.Id == status.PlatformId).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsEnabled, true));
        h.Client.Enabled = false;
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBeEmpty();
        h.Client.Enabled = true;
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBe(new[] { status.Id });
        (await h.CheckScheduledAsync(status.Id, default)).Value.LastCheckedAt.ShouldBe(clock.GetUtcNow());
    }

    [Fact]
    public async Task ScheduledCheck_ManualRetryWins_StaleScheduledAttemptAndFailureCannotOverwriteIt()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var clock = new Clock();
        await using var h = new Harness(database, clock);
        await h.InitializeAsync();
        h.Client.Fail = true;
        var status = await h.EnrollAsync();
        clock.Advance(TimeSpan.FromHours(1));
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBe(new[] { status.Id });
        h.Client.Fail = false;
        var reviewed = (await h.CheckAsync(status.Id, default)).Value;
        h.Client.Fail = true;
        (await h.CheckScheduledAsync(status.Id, default)).FirstError.Code.ShouldBe("DatEnrollment.Busy");
        await h.Service.RecordScheduledFailureAsync(status.Id, default);
        var final = (await h.Service.GetAsync(status.Id, default)).Value;
        final.State.ShouldBe("ReadyToImport");
        final.CandidateSha256.ShouldBe(reviewed.CandidateSha256);
        (await h.Db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task FirstEnrollment_ReviewHasNoClaims_ApprovalCommitsOneJobAndReceipt()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        var status = await h.EnrollAsync();
        status.State.ShouldBe("ReadyToImport");
        (await h.Db.DatSources.CountAsync()).ShouldBe(0);
        (await h.Db.SourceEntries.CountAsync()).ShouldBe(0);
        (await h.Db.Jobs.CountAsync()).ShouldBe(0);
        var preview = (await h.Service.PreviewAsync(status.Id, default)).Value;
        preview.ActiveSha256.ShouldBeEmpty();
        preview.EntriesAdded.ShouldBe(1);
        var accepted = (await h.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default)).Value;
        var repeat = (await h.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default)).Value;
        repeat.ShouldBe(accepted);
        h.ApprovedBytes.ShouldBe(h.Client.Bytes);
        (await h.Db.Jobs.CountAsync()).ShouldBe(1);
        (await h.Db.JobDispatches.CountAsync()).ShouldBe(1);
        (await h.Db.DatSubscriptions.SingleAsync()).JobId.ShouldBe(accepted);
        (await h.CheckAsync(status.Id, default)).FirstError.Code.ShouldBe("DatEnrollment.Busy");
    }

    [Fact]
    public async Task Stop_RejectsRunningImport_ThenPreservesInstalledSourceAndFiles()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        var status = await h.EnrollAsync();
        var preview = (await h.Service.PreviewAsync(status.Id, default)).Value;
        var accepted = (await h.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default)).Value;
        var stop = new StopDatSubscriptionCommandHandler(h.Service, new EfUnitOfWork(h.Db), NullLogger<StopDatSubscriptionCommandHandler>.Instance);
        (await stop.HandleAsync(new(status.Id))).FirstError.Code.ShouldBe("DatEnrollment.Busy");
        var row = await h.Db.DatSubscriptions.SingleAsync();
        var dat = DatFile.CreateNew("Sony - PlayStation", "Catalog", DatType.Redump, "catalog.dat", row.CandidateFileId!.Value, now: DateTimeOffset.UnixEpoch, status.PlatformId);
        await new DatRepository(h.Db, TimeProvider.System).AddStagedAsync(dat, DatSource.CreateNew(now: DateTimeOffset.UnixEpoch));
        await h.Db.SaveChangesAsync();
        await h.Db.Jobs.Where(x => x.Id == accepted).ExecuteUpdateAsync(s => s.SetProperty(x => x.Phase, "Completed"));
        h.Db.ChangeTracker.Clear();
        await h.CheckAsync(status.Id, default); // persist the installed source binding
        (await stop.HandleAsync(new(status.Id))).IsError.ShouldBeFalse();
        (await h.Db.DatSubscriptions.CountAsync()).ShouldBe(0);
        (await h.Db.DatFiles.CountAsync()).ShouldBe(1);
        (await h.Db.DatSources.CountAsync()).ShouldBe(1);
        (await h.Db.Files.AnyAsync(x => x.Id == row.CandidateFileId)).ShouldBeTrue();
        (await h.Db.Jobs.CountAsync()).ShouldBe(1);
        (await stop.HandleAsync(new(status.Id))).FirstError.Code.ShouldBe("DatEnrollment.NotFound");
    }

    [Fact]
    public async Task FirstEnrollment_FailureAfterJobSave_RollsBackJobDispatchAndReceipt()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        var status = await h.EnrollAsync();
        var preview = (await h.Service.PreviewAsync(status.Id, default)).Value;
        h.FailAfterJobSave = true;
        await Should.ThrowAsync<IOException>(() => h.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default));
        h.Db.ChangeTracker.Clear();
        (await h.Db.Jobs.CountAsync()).ShouldBe(0);
        (await h.Db.JobDispatches.CountAsync()).ShouldBe(0);
        (await h.Db.DatSubscriptions.SingleAsync()).JobId.ShouldBeNull();
        h.FailAfterJobSave = false;
        (await h.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task FirstEnrollment_FailedDownloadAndDeletedPlatform_PreserveCandidateAndPreventApproval()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        var status = await h.EnrollAsync();
        var preview = (await h.Service.PreviewAsync(status.Id, default)).Value;
        h.Client.Fail = true;
        var failed = (await h.CheckAsync(status.Id, default)).Value;
        failed.State.ShouldBe("CheckFailed");
        failed.CandidateSha256.ShouldBe(status.CandidateSha256);
        failed.Message!.ShouldNotContain("private");
        (await h.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default)).IsError.ShouldBeTrue();
        h.Client.Fail = false;
        await h.CheckAsync(status.Id, default);
        // Simulate external identity removal; normal reference commands reject built-in deletion.
        await h.Db.SystemCompanies.Where(x => x.PlatformId == status.PlatformId).ExecuteDeleteAsync();
        await h.Db.Platforms.Where(x => x.Id == status.PlatformId).ExecuteDeleteAsync();
        h.Db.ChangeTracker.Clear();
        (await h.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default)).FirstError.Code.ShouldBe("DatEnrollment.PlatformMissing");
        (await h.Db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task CompletedInitialJob_StatusResolvesExactDocument_ThenCheckPersistsBinding()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var clock = new Clock();
        await using var h = new Harness(database, clock);
        await h.InitializeAsync();
        var status = await h.EnrollAsync();
        var preview = (await h.Service.PreviewAsync(status.Id, default)).Value;
        var accepted = (await h.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default)).Value;
        var row = await h.Db.DatSubscriptions.SingleAsync();
        // Simulate the existing executor's completed durable result, not its execution.
        var dat = DatFile.CreateNew("Sony - PlayStation", "Catalog", DatType.Redump, "catalog.dat", row.CandidateFileId!.Value, now: DateTimeOffset.UnixEpoch, status.PlatformId);
        await new DatRepository(h.Db, TimeProvider.System).AddStagedAsync(dat, DatSource.CreateNew(now: DateTimeOffset.UnixEpoch));
        await h.Db.SaveChangesAsync();
        await h.Db.Jobs.Where(x => x.Id == accepted).ExecuteUpdateAsync(s => s.SetProperty(x => x.Phase, "Completed"));
        h.Db.ChangeTracker.Clear();
        var current = (await h.Service.GetAsync(status.Id, default)).Value;
        current.State.ShouldBe("UpToDate");
        current.ActiveDatId.ShouldNotBeNull();
        (await h.Db.DatSubscriptions.SingleAsync()).DatSourceId.ShouldBeNull();
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBeEmpty();
        clock.Advance(TimeSpan.FromDays(1));
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBe(new[] { status.Id });
        var unchanged = (await h.CheckScheduledAsync(status.Id, default)).Value;
        unchanged.State.ShouldBe("UpToDate");
        unchanged.NextCheckAt.ShouldBe(clock.GetUtcNow().AddDays(1));
        (await h.Db.Jobs.CountAsync()).ShouldBe(1);
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBeEmpty();
        (await h.Db.DatSubscriptions.SingleAsync()).DatSourceId.ShouldNotBeNull();
        clock.Advance(TimeSpan.FromDays(1));
        // A manual check, which ignores the due gate, supersedes an already selected scheduled check.
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBe(new[] { status.Id });
        (await h.CheckAsync(status.Id, default)).Value.State.ShouldBe("UpToDate");
        (await h.CheckScheduledAsync(status.Id, default)).FirstError.Code.ShouldBe("DatEnrollment.Busy");
        await h.Service.RecordScheduledFailureAsync(status.Id, default);
        (await h.Service.GetAsync(status.Id, default)).Value.State.ShouldBe("UpToDate");
        clock.Advance(TimeSpan.FromDays(1));
        h.Client.FailDirectory = true;
        var failed = (await h.CheckScheduledAsync(status.Id, default)).Value;
        failed.State.ShouldBe("CheckFailed");
        failed.ActiveDatId.ShouldBe(current.ActiveDatId);
        failed.NextCheckAt.ShouldBe(clock.GetUtcNow().AddHours(1));
        (await h.Service.ListCatalogsAsync(default)).Message.ShouldNotBeNull();
        (await h.Service.ListAsync(default)).Single().ActiveDatId.ShouldBe(current.ActiveDatId);
        h.Client.FailDirectory = false;
        h.Client.Bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(h.Client.Bytes).Replace("Test Disc", "New Disc"));
        clock.Advance(TimeSpan.FromHours(1));
        var update = (await h.CheckScheduledAsync(status.Id, default)).Value;
        update.State.ShouldBe("UpdateAvailable");
        update.ActiveDatId.ShouldBe(current.ActiveDatId);
        var diff = (await h.Service.PreviewAsync(status.Id, default)).Value;
        diff.EntriesAdded.ShouldBe(1);
        diff.EntriesRemoved.ShouldBe(1);
        (await h.Db.Jobs.CountAsync()).ShouldBe(1); // only the previously approved initial import
        clock.Advance(TimeSpan.FromDays(2));
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_ExistingSubscription_ReplaysWithoutCheckingAgain()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var clock = new Clock();
        await using var h = new Harness(database, clock);
        await h.InitializeAsync();
        var created = (await h.CreateAsync(Catalog, null, default)).Value;
        created.Created.ShouldBeTrue();
        clock.Advance(TimeSpan.FromHours(2));
        h.Client.Bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(h.Client.Bytes).Replace("Test Disc", "Other Disc"));
        var replay = (await h.CreateAsync(Catalog, null, default)).Value;
        replay.Created.ShouldBeFalse();
        replay.Subscription.Id.ShouldBe(created.Subscription.Id);
        replay.Subscription.LastCheckedAt.ShouldBe(created.Subscription.LastCheckedAt);
        replay.Subscription.CandidateSha256.ShouldBe(created.Subscription.CandidateSha256);
        h.Client.Fetches.ShouldBe(1);
        h.Client.Enabled = false;
        (await h.CreateAsync(Catalog, null, default)).Value.Created.ShouldBeFalse();
        (await h.Db.DatSubscriptions.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task ManualCheck_NotDue_RunsWhereTheScheduledCheckWaits()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var clock = new Clock();
        await using var h = new Harness(database, clock);
        await h.InitializeAsync();
        h.Client.Fail = true;
        var status = await h.EnrollAsync();
        status.State.ShouldBe("CheckFailed");
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBeEmpty();
        (await h.CheckScheduledAsync(status.Id, default)).FirstError.Code.ShouldBe("DatEnrollment.Busy");
        clock.Advance(TimeSpan.FromMinutes(5));
        h.Client.Fail = false;
        var manual = (await h.CheckAsync(status.Id, default)).Value;
        manual.State.ShouldBe("ReadyToImport");
        manual.LastCheckedAt.ShouldBe(clock.GetUtcNow());
        (await h.CheckAsync(int.MaxValue, default)).FirstError.Code.ShouldBe("DatEnrollment.NotFound");
    }

    [Fact]
    public async Task Create_ExplicitSource_BindsThatSourceWhenNamesAreAmbiguous()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        var first = await h.InstallAsync(h.Client.Bytes);
        var second = await h.InstallAsync(h.Client.Bytes);
        (await h.CreateAsync(Catalog, null, default)).FirstError.Code.ShouldBe("DatEnrollment.Ambiguous");
        (await h.Db.DatSubscriptions.CountAsync()).ShouldBe(0);
        var created = (await h.CreateAsync(Catalog, second.DatId, default)).Value;
        created.Created.ShouldBeTrue();
        created.Subscription.ActiveDatId.ShouldBe(second.DatId);
        created.Subscription.State.ShouldBe("UpToDate");
        (await h.Db.DatSubscriptions.SingleAsync()).DatSourceId.ShouldBe(second.SourceId);
        // Replays are idempotent on the catalog and never rebind it to another source.
        (await h.CreateAsync(Catalog, second.DatId, default)).Value.Created.ShouldBeFalse();
        (await h.CreateAsync(Catalog, null, default)).Value.Subscription.Id.ShouldBe(created.Subscription.Id);
        (await h.CreateAsync(Catalog, first.DatId, default)).FirstError.Code.ShouldBe("DatEnrollment.Ambiguous");
        h.Client.Fetches.ShouldBe(1);
    }

    [Fact]
    public async Task Create_ExplicitSourceThatCannotTrackTheCatalog_IsUnavailable()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        var other = await h.InstallAsync(Encoding.UTF8.GetBytes("<datafile><header><name>Other</name></header><game name='Other Disc'><rom name='track.bin' size='1' crc='11111111'/></game></datafile>"), "Other");
        (await h.CreateAsync(Catalog, other.DatId, default)).FirstError.Code.ShouldBe("DatEnrollment.Unavailable");
        (await h.CreateAsync(Catalog, int.MaxValue, default)).FirstError.Code.ShouldBe("DatEnrollment.Unavailable");
        (await h.Db.DatSubscriptions.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task RetiredPerSourceRow_IsListedAndReplayed_ThenItsCheckAdoptsTheCatalogIdentity()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        var installed = await h.InstallAsync(h.Client.Bytes);
        await h.AddRetiredRowAsync(installed.SourceId);
        var listed = (await h.Service.ListAsync(default)).Single();
        listed.ActiveDatId.ShouldBe(installed.DatId);
        listed.Name.ShouldBe("Sony - PlayStation");
        var replay = (await h.CreateAsync(Catalog, installed.DatId, default)).Value;
        replay.Created.ShouldBeFalse();
        replay.Subscription.Id.ShouldBe(listed.Id);
        h.Client.Fetches.ShouldBe(0);
        (await h.CheckAsync(listed.Id, default)).Value.State.ShouldBe("UpToDate");
        var row = await h.Db.DatSubscriptions.SingleAsync();
        row.SystemId.ShouldBe("psx");
        row.ExpectedName.ShouldBe("Sony - PlayStation");
        row.PlatformId.ShouldBe(listed.PlatformId);
        row.DatSourceId.ShouldBe(installed.SourceId);
    }

    [Fact]
    public async Task Changes_StalePinsConflict_AndPagesFollowTheReviewedPair()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        var status = await h.EnrollAsync();
        var preview = (await h.Service.PreviewAsync(status.Id, default)).Value;
        var page = DatChangePageRequest.ForEntries(preview.ActiveSha256, preview.CandidateSha256, null, null, null, null).Value;
        (await h.Service.ListChangesAsync(status.Id, page, default)).Value.Items.Single().Name.ShouldBe("Test Disc");
        var detail = (await h.Service.GetChangeAsync(status.Id, "Test Disc",
            DatChangePageRequest.ForFiles(preview.ActiveSha256, preview.CandidateSha256, "Test Disc", null, null).Value, default)).Value;
        detail.Change.ShouldBe("Added");
        detail.Files.Items.Single().Name.ShouldBe("track.bin");
        var stale = DatChangePageRequest.ForEntries(preview.ActiveSha256, new string('0', 64), null, null, null, null).Value;
        (await h.Service.ListChangesAsync(status.Id, stale, default)).FirstError.Code.ShouldBe("DatEnrollment.Stale");
        (await h.Service.ListChangesAsync(int.MaxValue, page, default)).FirstError.Code.ShouldBe("DatEnrollment.NotFound");
    }

    [Fact]
    public async Task RetiredPerSourceRowWithPendingCandidate_IsReviewedAndAppliedWithoutAdoption()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        var installed = await h.InstallAsync(h.Client.Bytes);
        byte[] candidate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(h.Client.Bytes).Replace("Test Disc", "New Disc"));
        int id = await h.AddRetiredRowAsync(installed.SourceId, "UpdateAvailable", candidate, Convert.ToHexStringLower(SHA256.HashData(h.Client.Bytes)));
        var preview = (await h.Service.PreviewAsync(id, default)).Value;
        preview.EntriesAdded.ShouldBe(1);
        preview.EntriesRemoved.ShouldBe(1);
        var changes = (await h.Service.ListChangesAsync(id,
            DatChangePageRequest.ForEntries(preview.ActiveSha256, preview.CandidateSha256, null, null, null, null).Value, default)).Value;
        changes.Items.Select(x => x.Name).ShouldBe(["New Disc", "Test Disc"]);
        var job = (await h.ApplyAsync(id, preview.ActiveSha256, preview.CandidateSha256, default)).Value;
        var accepted = await h.Db.Jobs.OfType<ReplaceDatJobEntity>().SingleAsync();
        accepted.Id.ShouldBe(job);
        accepted.ExistingDatId.ShouldBe(installed.DatId);
        accepted.SourceFilename.ShouldBe("reviewed-catalog.dat");
        accepted.PlatformId.ShouldBeNull();
        accepted.CreatedByUserId.ShouldBeNull();
        (await h.Db.JobDispatches.SingleAsync()).JobId.ShouldBe(job);
        h.ApprovedBytes.ShouldBe(candidate);
        var row = await h.Db.DatSubscriptions.SingleAsync();
        row.State.ShouldBe("Applying");
        row.JobId.ShouldBe(job);
        row.PlatformId.ShouldBeNull(); // only a check adopts the catalog's system identity
        h.Client.Fetches.ShouldBe(0);
    }

    [Fact]
    public async Task RetiredPerSourceRowWithInvalidSavedCandidate_IsAConflictNotInvalidInput()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        var installed = await h.InstallAsync(h.Client.Bytes);
        int id = await h.AddRetiredRowAsync(installed.SourceId, "UpdateAvailable", Encoding.UTF8.GetBytes("<html>not a catalog</html>"),
            Convert.ToHexStringLower(SHA256.HashData(h.Client.Bytes)));
        var error = (await h.Service.PreviewAsync(id, default)).FirstError;
        error.Code.ShouldBe("DatReview.InvalidDocument");
        error.Type.ShouldBe(ErrorType.Conflict);
    }

    [Fact]
    public async Task Create_ExplicitSource_ReplaysItsRowAmongManyRetiredPerSourceRows()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        var rows = new List<(int DatId, int RowId)>();
        for (int i = 0; i < 3; i++)
        {
            var installed = await h.InstallAsync(h.Client.Bytes);
            rows.Add((installed.DatId, await h.AddRetiredRowAsync(installed.SourceId)));
        }
        foreach (var (datId, rowId) in rows)
        {
            var replay = (await h.CreateAsync(Catalog, datId, default)).Value;
            replay.Created.ShouldBeFalse();
            replay.Subscription.Id.ShouldBe(rowId);
        }
        (await h.CreateAsync(Catalog, null, default)).FirstError.Code.ShouldBe("DatEnrollment.Ambiguous");
        h.Client.Fetches.ShouldBe(0);
    }

    [Fact]
    public async Task Create_WhileAnotherEnrollmentHoldsTheCatalog_IsBusyAndCreatesNothing()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var h = new Harness(database);
        await h.InitializeAsync();
        await using var other = new RomdDbContext(new DbContextOptionsBuilder<RomdDbContext>().UseNpgsql(h.ConnectionString).UseOpenIddict().Options);
        await using (var held = await other.Database.BeginTransactionAsync())
        {
            await other.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(146, hashtext({Catalog}))");
            (await h.CreateAsync(Catalog, null, default)).FirstError.Code.ShouldBe("DatEnrollment.Busy");
            (await h.Db.DatSubscriptions.CountAsync()).ShouldBe(0);
        }
        (await h.CreateAsync(Catalog, null, default)).Value.Created.ShouldBeTrue();
    }
}
