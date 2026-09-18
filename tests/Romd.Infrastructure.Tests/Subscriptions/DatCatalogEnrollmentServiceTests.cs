using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenIddict.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Dat;
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
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Subscriptions;

public sealed class DatCatalogEnrollmentServiceTests
{
    private const string Catalog = "redump/psx/discs";
    private sealed class Client : ISignedDatCatalogClient
    {
        public bool Enabled { get; set; } = true;
        public bool Fail { get; set; }
        public bool FailDirectory { get; set; }
        public byte[] Bytes { get; set; } = Encoding.UTF8.GetBytes("<datafile><header><name>Sony - PlayStation</name></header><game name='Test Disc'><rom name='track.bin' size='1' crc='11111111'/></game></datafile>");
        public Task<ErrorOr<byte[]>> FetchPlayStationAsync(CancellationToken ct) => FetchAsync(Catalog, "psx", "Sony - PlayStation", ct);
        public Task<ErrorOr<byte[]>> FetchAsync(string id, string system, string name, CancellationToken ct) =>
            Task.FromResult<ErrorOr<byte[]>>(Fail ? Error.Failure("test", "private diagnostic") : Bytes);
        public Task<ErrorOr<IReadOnlyList<PublishedDatCatalog>>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<ErrorOr<IReadOnlyList<PublishedDatCatalog>>>(FailDirectory ? Error.Failure("test", "failed") : new[] {
                new PublishedDatCatalog(Catalog, "psx", "Sony - PlayStation", "redump", "healthy", Convert.ToHexStringLower(SHA256.HashData(Bytes)), 1, 1, DateTimeOffset.UtcNow) });
    }
    private sealed class Harness : IAsyncDisposable
    {
        public RomdDbContext Db { get; }
        public Client Client { get; } = new();
        public DatCatalogEnrollmentService Service { get; }
        public DatSubscriptionService LegacyService { get; }
        public bool FailAfterJobSave { get; set; }
        public byte[]? ApprovedBytes { get; private set; }
        private readonly Dictionary<int, byte[]> _contents = [];
        public Harness(PostgreSqlTestDatabase database, TimeProvider? time = null)
        {
            Db = new(new DbContextOptionsBuilder<RomdDbContext>().UseNpgsql(database.ConnectionString).UseOpenIddict().Options);
            var storage = Substitute.For<IFileStorageService>();
            storage.StoreAsync(Arg.Any<Stream>(), Arg.Any<IProgress<StoreProgress>?>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                using var copy = new MemoryStream(); await call.Arg<Stream>().CopyToAsync(copy);
                var bytes = copy.ToArray(); var hash = Sha256.FromSpan(SHA256.HashData(bytes));
                var file = await Db.Files.SingleOrDefaultAsync(x => x.Sha256 == hash);
                if (file is null)
                {
                    file = new FileEntityPersistence { Sha256 = hash, Size = bytes.Length, SizeOnDisk = bytes.Length };
                    Db.Files.Add(file); await Db.SaveChangesAsync();
                }
                _contents[file.Id] = bytes;
                return new FileStoreResult(file.ToDomain(), true, false);
            });
            storage.RetrieveByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
                Task.FromResult<Stream?>(_contents.TryGetValue(call.Arg<int>(), out var bytes) ? new MemoryStream(bytes, writable: false) : null));
            var uploads = Substitute.For<IUploadJobCreator>();
            uploads.CreateAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<UploadJobOptions>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                using var copy = new MemoryStream(); await call.Arg<Stream>().CopyToAsync(copy); ApprovedBytes = copy.ToArray();
                var options = call.Arg<UploadJobOptions>();
                var job = UploadJob.Create(call.Arg<string>(), options.PlatformId, options.CreatedByUserId);
                await new UploadJobRepository(Db, TimeProvider.System).AddAsync(job);
                if (FailAfterJobSave) throw new IOException("Injected failure after durable job save");
                return new UploadJobCreationResult(job.Id, job.Id.ToString("N"), $"/jobs/{job.Id}");
            });
            var parser = new DatParser([new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance)]);
            var repository = new DatRepository(Db, TimeProvider.System);
            var review = new DatReplacementReview(repository, storage, parser, parser, Substitute.For<IReplaceDatJobCreator>());
            LegacyService = new(Db, repository, storage, review, Client, time ?? TimeProvider.System);
            Service = new(Db, Client, repository, storage,
                review, uploads,
                Substitute.For<ICurrentUser>(), time ?? TimeProvider.System);
        }
        public async Task InitializeAsync() { await SharedReferenceDataSeeder.InitializeAsync(Db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance); Db.ChangeTracker.Clear(); }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
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
        var status = (await h.Service.CheckAsync(Catalog, default)).Value;
        var preview = (await h.Service.PreviewAsync(status.Id, default)).Value;
        var definition = await h.Db.Platforms.AsTracking()
            .SingleAsync(x => x.CanonicalKey == "psx");
        definition.Retired = true;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        (await h.Service.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default))
            .FirstError.Code.ShouldBe("DatEnrollment.PlatformMissing");
        (await h.Service.CheckAsync(Catalog, default)).FirstError.Code.ShouldBe("DatEnrollment.PlatformMissing");
        (await h.Db.Platforms.AnyAsync(x => x.Id == status.PlatformId)).ShouldBeTrue();
        (await h.Db.Jobs.CountAsync()).ShouldBe(0);
        (await h.Db.DatSubscriptions.SingleAsync()).CandidateSha256.ShouldBe(preview.CandidateSha256);
    }

    [Fact]
    public async Task ScheduledCheck_FailuresBackOff_RecoveryWaitsForReviewWithoutActivation()
    {
        using var database = PostgreSqlTestDatabase.Create(); var clock = new Clock();
        await using var h = new Harness(database, clock); await h.InitializeAsync();
        h.Client.Fail = true;
        var status = (await h.Service.CheckAsync(Catalog, default)).Value;
        foreach (var hours in new[] { 1, 2, 4, 8, 16, 24, 24 })
        {
            status.State.ShouldBe("CheckFailed");
            status.NextCheckAt.ShouldBe(clock.GetUtcNow().AddHours(hours));
            (await h.Service.GetDueCheckIdsAsync(default)).ShouldBeEmpty();
            clock.Advance(TimeSpan.FromHours(hours));
            (await h.Service.GetDueCheckIdsAsync(default)).ShouldBe(new[] { status.Id });
            status = (await h.Service.CheckScheduledAsync(status.Id, default)).Value;
        }
        h.Client.Fail = false;
        clock.Advance(TimeSpan.FromDays(1));
        status = (await h.Service.CheckScheduledAsync(status.Id, default)).Value;
        status.State.ShouldBe("ReadyToImport"); status.NextCheckAt.ShouldBeNull();
        status.AutomaticChecksPaused.ShouldBeFalse();
        var candidate = status.CandidateSha256;
        clock.Advance(TimeSpan.FromDays(3));
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBeEmpty();
        (await h.Service.CheckScheduledAsync(status.Id, default)).FirstError.Code.ShouldBe("DatEnrollment.Busy");
        (await h.Service.GetAsync(status.Id, default)).Value.CandidateSha256.ShouldBe(candidate);
        (await h.Db.Jobs.CountAsync()).ShouldBe(0);
        (await h.Db.DatSubscriptions.SingleAsync()).ConsecutiveCheckFailures.ShouldBe(0);
    }

    [Fact]
    public async Task ScheduledCheck_RemovedSystemAndDisabledReaderPause_RestoringResumes()
    {
        using var database = PostgreSqlTestDatabase.Create(); var clock = new Clock();
        await using var h = new Harness(database, clock); await h.InitializeAsync();
        h.Client.Fail = true;
        var status = (await h.Service.CheckAsync(Catalog, default)).Value;
        clock.Advance(TimeSpan.FromHours(1));
        await h.Db.Platforms.Where(p => p.Id == status.PlatformId).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsEnabled, false));
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBeEmpty();
        var paused = (await h.Service.GetAsync(status.Id, default)).Value;
        paused.AutomaticChecksPaused.ShouldBeTrue(); paused.NextCheckAt.ShouldBeNull();
        (await h.Service.CheckScheduledAsync(status.Id, default)).FirstError.Code.ShouldBe("DatEnrollment.Busy");
        await h.Db.Platforms.Where(p => p.Id == status.PlatformId).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsEnabled, true));
        h.Client.Enabled = false;
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBeEmpty();
        h.Client.Enabled = true;
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBe(new[] { status.Id });
        (await h.Service.CheckScheduledAsync(status.Id, default)).Value.LastCheckedAt.ShouldBe(clock.GetUtcNow());
    }

    [Fact]
    public async Task ScheduledCheck_ManualRetryWins_StaleScheduledAttemptAndFailureCannotOverwriteIt()
    {
        using var database = PostgreSqlTestDatabase.Create(); var clock = new Clock();
        await using var h = new Harness(database, clock); await h.InitializeAsync();
        h.Client.Fail = true;
        var status = (await h.Service.CheckAsync(Catalog, default)).Value;
        clock.Advance(TimeSpan.FromHours(1));
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBe(new[] { status.Id });
        h.Client.Fail = false;
        var reviewed = (await h.Service.CheckAsync(Catalog, default)).Value;
        h.Client.Fail = true;
        (await h.Service.CheckScheduledAsync(status.Id, default)).FirstError.Code.ShouldBe("DatEnrollment.Busy");
        await h.Service.RecordScheduledFailureAsync(status.Id, default);
        var final = (await h.Service.GetAsync(status.Id, default)).Value;
        final.State.ShouldBe("ReadyToImport"); final.CandidateSha256.ShouldBe(reviewed.CandidateSha256);
        (await h.Db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task FirstEnrollment_ReviewHasNoClaims_ApprovalCommitsOneJobAndReceipt()
    {
        using var database = PostgreSqlTestDatabase.Create(); await using var h = new Harness(database); await h.InitializeAsync();
        var status = (await h.Service.CheckAsync(Catalog, default)).Value;
        status.State.ShouldBe("ReadyToImport");
        (await h.Db.DatSources.CountAsync()).ShouldBe(0); (await h.Db.SourceEntries.CountAsync()).ShouldBe(0); (await h.Db.Jobs.CountAsync()).ShouldBe(0);
        var preview = (await h.Service.PreviewAsync(status.Id, default)).Value;
        preview.ActiveSha256.ShouldBeEmpty(); preview.EntriesAdded.ShouldBe(1);
        var accepted = (await h.Service.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default)).Value;
        var repeat = (await h.Service.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default)).Value;
        repeat.JobId.ShouldBe(accepted.JobId); h.ApprovedBytes.ShouldBe(h.Client.Bytes);
        (await h.Db.Jobs.CountAsync()).ShouldBe(1); (await h.Db.JobDispatches.CountAsync()).ShouldBe(1);
        (await h.Db.DatSubscriptions.SingleAsync()).JobId.ShouldBe(accepted.JobId);
        (await h.Service.CheckAsync(Catalog, default)).FirstError.Code.ShouldBe("DatEnrollment.Busy");
    }

    [Fact]
    public async Task Stop_RejectsRunningImport_ThenPreservesInstalledSourceAndFiles()
    {
        using var database = PostgreSqlTestDatabase.Create(); await using var h = new Harness(database); await h.InitializeAsync();
        var status = (await h.Service.CheckAsync(Catalog, default)).Value;
        var preview = (await h.Service.PreviewAsync(status.Id, default)).Value;
        var accepted = (await h.Service.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default)).Value;
        (await h.Service.StopAsync(status.Id, default)).FirstError.Code.ShouldBe("DatEnrollment.Busy");
        var row = await h.Db.DatSubscriptions.SingleAsync();
        var dat = DatFile.CreateNew("Sony - PlayStation", "Catalog", DatType.Redump, "catalog.dat", row.CandidateFileId!.Value, status.PlatformId);
        await new DatRepository(h.Db, TimeProvider.System).AddStagedAsync(dat, DatSource.CreateNew()); await h.Db.SaveChangesAsync();
        await h.Db.Jobs.Where(x => x.Id == accepted.JobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Phase, "Completed")); h.Db.ChangeTracker.Clear();
        await h.Service.CheckAsync(Catalog, default); // persist the installed source binding
        (await h.Service.StopAsync(status.Id, default)).IsError.ShouldBeFalse();
        (await h.Db.DatSubscriptions.CountAsync()).ShouldBe(0);
        (await h.Db.DatFiles.CountAsync()).ShouldBe(1);
        (await h.Db.DatSources.CountAsync()).ShouldBe(1);
        (await h.Db.Files.AnyAsync(x => x.Id == row.CandidateFileId)).ShouldBeTrue();
        (await h.Db.Jobs.CountAsync()).ShouldBe(1);
        (await h.Service.StopAsync(status.Id, default)).FirstError.Code.ShouldBe("DatEnrollment.NotFound");
    }

    [Fact]
    public async Task FirstEnrollment_FailureAfterJobSave_RollsBackJobDispatchAndReceipt()
    {
        using var database = PostgreSqlTestDatabase.Create(); await using var h = new Harness(database); await h.InitializeAsync();
        var status = (await h.Service.CheckAsync(Catalog, default)).Value;
        var preview = (await h.Service.PreviewAsync(status.Id, default)).Value;
        h.FailAfterJobSave = true;
        await Should.ThrowAsync<IOException>(() => h.Service.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default));
        h.Db.ChangeTracker.Clear();
        (await h.Db.Jobs.CountAsync()).ShouldBe(0); (await h.Db.JobDispatches.CountAsync()).ShouldBe(0);
        (await h.Db.DatSubscriptions.SingleAsync()).JobId.ShouldBeNull();
        h.FailAfterJobSave = false;
        (await h.Service.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task FirstEnrollment_FailedDownloadAndDeletedPlatform_PreserveCandidateAndPreventApproval()
    {
        using var database = PostgreSqlTestDatabase.Create(); await using var h = new Harness(database); await h.InitializeAsync();
        var status = (await h.Service.CheckAsync(Catalog, default)).Value;
        var preview = (await h.Service.PreviewAsync(status.Id, default)).Value;
        h.Client.Fail = true;
        var failed = (await h.Service.CheckAsync(Catalog, default)).Value;
        failed.State.ShouldBe("CheckFailed"); failed.CandidateSha256.ShouldBe(status.CandidateSha256);
        failed.Message!.ShouldNotContain("private");
        (await h.Service.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default)).IsError.ShouldBeTrue();
        h.Client.Fail = false; await h.Service.CheckAsync(Catalog, default);
        // Simulate external identity removal; normal reference commands reject built-in deletion.
        await h.Db.SystemCompanies.Where(x => x.PlatformId == status.PlatformId).ExecuteDeleteAsync();
        await h.Db.Platforms.Where(x => x.Id == status.PlatformId).ExecuteDeleteAsync(); h.Db.ChangeTracker.Clear();
        (await h.Service.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default)).FirstError.Code.ShouldBe("DatEnrollment.PlatformMissing");
        (await h.Db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task CompletedInitialJob_StatusResolvesExactDocument_ThenCheckPersistsBinding()
    {
        using var database = PostgreSqlTestDatabase.Create(); var clock = new Clock();
        await using var h = new Harness(database, clock); await h.InitializeAsync();
        var status = (await h.Service.CheckAsync(Catalog, default)).Value;
        var preview = (await h.Service.PreviewAsync(status.Id, default)).Value;
        var accepted = (await h.Service.ApplyAsync(status.Id, preview.ActiveSha256, preview.CandidateSha256, default)).Value;
        var row = await h.Db.DatSubscriptions.SingleAsync();
        // Simulate the existing executor's completed durable result, not its execution.
        var dat = DatFile.CreateNew("Sony - PlayStation", "Catalog", DatType.Redump, "catalog.dat", row.CandidateFileId!.Value, status.PlatformId);
        await new DatRepository(h.Db, TimeProvider.System).AddStagedAsync(dat, DatSource.CreateNew()); await h.Db.SaveChangesAsync();
        await h.Db.Jobs.Where(x => x.Id == accepted.JobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Phase, "Completed")); h.Db.ChangeTracker.Clear();
        var current = (await h.Service.GetAsync(status.Id, default)).Value;
        current.State.ShouldBe("UpToDate"); current.ActiveDatId.ShouldNotBeNull();
        (await h.Db.DatSubscriptions.SingleAsync()).DatSourceId.ShouldBeNull();
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBeEmpty();
        clock.Advance(TimeSpan.FromDays(1));
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBe(new[] { status.Id });
        var unchanged = (await h.Service.CheckScheduledAsync(status.Id, default)).Value;
        unchanged.State.ShouldBe("UpToDate");
        unchanged.NextCheckAt.ShouldBe(clock.GetUtcNow().AddDays(1));
        (await h.Db.Jobs.CountAsync()).ShouldBe(1);
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBeEmpty();
        (await h.Db.DatSubscriptions.SingleAsync()).DatSourceId.ShouldNotBeNull();
        clock.Advance(TimeSpan.FromDays(1));
        // The older per-DAT check route must also supersede an already selected scheduled check.
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBe(new[] { status.Id });
        (await h.LegacyService.CheckAsync(current.ActiveDatId!.Value, default)).Value.State.ShouldBe("UpToDate");
        (await h.Service.CheckScheduledAsync(status.Id, default)).FirstError.Code.ShouldBe("DatEnrollment.Busy");
        await h.Service.RecordScheduledFailureAsync(status.Id, default);
        (await h.Service.GetAsync(status.Id, default)).Value.State.ShouldBe("UpToDate");
        clock.Advance(TimeSpan.FromDays(1));
        h.Client.FailDirectory = true;
        var failed = (await h.Service.CheckScheduledAsync(status.Id, default)).Value;
        failed.State.ShouldBe("CheckFailed"); failed.ActiveDatId.ShouldBe(current.ActiveDatId);
        failed.NextCheckAt.ShouldBe(clock.GetUtcNow().AddHours(1));
        var directory = (await h.Service.DiscoverAsync(default)).Value;
        directory.Message.ShouldNotBeNull(); directory.Subscriptions.Single().ActiveDatId.ShouldBe(current.ActiveDatId);
        h.Client.FailDirectory = false;
        h.Client.Bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(h.Client.Bytes).Replace("Test Disc", "New Disc"));
        clock.Advance(TimeSpan.FromHours(1));
        var update = (await h.Service.CheckScheduledAsync(status.Id, default)).Value;
        update.State.ShouldBe("UpdateAvailable"); update.ActiveDatId.ShouldBe(current.ActiveDatId);
        var diff = (await h.Service.PreviewAsync(status.Id, default)).Value;
        diff.EntriesAdded.ShouldBe(1); diff.EntriesRemoved.ShouldBe(1);
        (await h.Db.Jobs.CountAsync()).ShouldBe(1); // only the previously approved initial import
        clock.Advance(TimeSpan.FromDays(2));
        (await h.Service.GetDueCheckIdsAsync(default)).ShouldBeEmpty();
    }
}
