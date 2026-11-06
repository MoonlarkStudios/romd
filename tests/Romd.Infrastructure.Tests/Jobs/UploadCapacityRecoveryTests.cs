using Romd.Admin.Application.Titles.Commands.QueueBulkEnrichmentJob;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Data.Common;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Polly.Registry;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Classification;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.IngestRom;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.IngestDat;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Jobs.Processors;
using Romd.Infrastructure.Resilience;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Infrastructure.Tests.Storage;
using Romd.Media.TestSupport;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed partial class UploadCapacityRecoveryTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly string _root = Directory.CreateTempSubdirectory("romd-upload-capacity-").FullName;
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    private readonly IRomdOptions _options = Substitute.For<IRomdOptions>();
    private readonly ICommandHandler<IngestRomCommand, RomIngestResult> _handler =
        Substitute.For<ICommandHandler<IngestRomCommand, RomIngestResult>>();
    private readonly List<string> _attempts = [];
    private readonly ICommandHandler<IngestDatCommand, DatIngestResult> _datHandler =
        Substitute.For<ICommandHandler<IngestDatCommand, DatIngestResult>>();

    public UploadCapacityRecoveryTests()
    {
        _options.DataDirectory.Returns(_root);
        _options.MaxUploadBytes.Returns(8);
        _handler.HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var command = call.Arg<IngestRomCommand>();
                _attempts.Add(command.OriginalFilename);
                if (command.OriginalFilename == "z.rom")
                {
                    await using var staged = await Factory().CreateAsync(command.FileStream, call.Arg<CancellationToken>());
                }
                return (ErrorOr<RomIngestResult>)Error.Validation("Library.UnidentifiedRom", "No match");
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationRetry_SettlesSiblingAndJournalsOnlyCompletedFilesThenResumes(bool cancel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var job = await SeedAsync();
        job.SetMaxParallelRoms(2);
        await using (var db = _database.CreateContext())
            await new UploadJobRepository(db, _clock).CommitCheckpointAsync(db, job, ct);
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new ConcurrentDictionary<string, int>();
        bool pressure = true;
        _handler.HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            string name = call.Arg<IngestRomCommand>().OriginalFilename;
            attempts.AddOrUpdate(name, 1, (_, count) => count + 1);
            if (name == "z.rom" && pressure)
            {
                retried.TrySetResult();
                return (ErrorOr<RomIngestResult>)RomPublicationErrors.RetryRequired();
            }
            await retried.Task.WaitAsync(call.Arg<CancellationToken>());
            return (ErrorOr<RomIngestResult>)Error.Validation("Library.UnidentifiedRom", "No match");
        });
        await using (var host = Host())
            await Runner(host).RunAsync(job.Id, "publication-race", ct);
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync(ct);
            row.Phase.ShouldBe("IngestingRoms");
            row.RomsProcessed.ShouldBe(1);
            row.RomsRejected.ShouldBe(1);
            row.GetErrors().ShouldBeEmpty();
            row.NextAttemptAtUtc.ShouldNotBeNull();
            row.LastDeferralError.ShouldBe(RomPublicationErrors.RetryRequired().Description);
            (await db.JobItems.SingleAsync(ct)).FileName.ShouldBe("a.rom");
            (await db.JobDispatches.SingleAsync(ct)).AvailableAtUtc.ShouldBe(row.NextAttemptAtUtc.Value);
            _clock.SetUtcNow(row.NextAttemptAtUtc.Value);
            if (cancel)
            {
                var current = (await new JobRepository(db, _clock).GetByIdAsync(job.Id, ct)).ShouldBeOfType<UploadJob>();
                current.Cancel(cancelledAt: _clock.GetUtcNow());
                await new JobRepository(db, _clock).CommitCheckpointAsync(db, current, ct);
            }
        }
        File.Exists(Path.Combine(Workspace(job), "z.rom")).ShouldBeTrue();
        pressure = false;
        await using (var restarted = Host())
            await Runner(restarted).RunAsync(job.Id, "publication-restart", ct);
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync(ct);
            row.Phase.ShouldBe(cancel ? "Cancelled" : "Completed");
            row.RomsProcessed.ShouldBe(cancel ? 1 : 2);
            row.GetErrors().ShouldBeEmpty();
            (await db.JobItems.CountAsync(ct)).ShouldBe(cancel ? 1 : 2);
        }
        attempts["a.rom"].ShouldBe(1);
        attempts["z.rom"].ShouldBe(cancel ? 1 : 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapacityDeferral_RestartPreservesInputAndCompletedOutcomes_ThenResumesOrCancels(bool cancel)
    {
        var job = await SeedAsync();
        await using var occupied = await Factory().CreateAsync(new MemoryStream(new byte[8]));
        await using (var firstHost = Host())
            await Runner(firstHost).RunAsync(job.Id, "first", default);

        DateTimeOffset due;
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync(item => item.Id == job.Id);
            row.Phase.ShouldBe("IngestingRoms");
            row.RomsProcessed.ShouldBe(1);
            row.RomsRejected.ShouldBe(1);
            row.GetErrors().ShouldBeEmpty();
            row.NextAttemptAtUtc.ShouldNotBeNull();
            due = row.NextAttemptAtUtc.Value;
            due.ShouldBeGreaterThan(_clock.GetUtcNow());
            row.ExecutionFenceToken.ShouldBeNull();
            (await db.JobItems.SingleAsync()).FileName.ShouldBe("a.rom");
            (await db.JobDispatches.SingleAsync()).AvailableAtUtc.ShouldBe(due);
        }
        (await File.ReadAllBytesAsync(Path.Combine(Workspace(job), "z.rom"))).ShouldBe(new byte[] { 2 });
        _attempts.ShouldBe(new[] { "a.rom", "z.rom" }); // No synchronous Polly retry of capacity pressure.

        await occupied.DisposeAsync();
        await using var restartedHost = Host();
        await Runner(restartedHost).RunAsync(job.Id, "too-early", default);
        _attempts.Count.ShouldBe(2);
        if (cancel)
        {
            await using var db = _database.CreateContext();
            var repo = new JobRepository(db, _clock);
            var current = (await repo.GetByIdAsync(job.Id)).ShouldBeOfType<UploadJob>();
            current.Cancel(cancelledAt: _clock.GetUtcNow());
            await repo.CommitCheckpointAsync(db, current);
        }
        _clock.SetUtcNow(due);
        await Runner(restartedHost).RunAsync(job.Id, "due", default);
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync(item => item.Id == job.Id);
            row.Phase.ShouldBe(cancel ? "Cancelled" : "Completed");
            row.RomsProcessed.ShouldBe(cancel ? 1 : 2);
            row.GetErrors().ShouldBeEmpty();
            (await db.JobItems.CountAsync()).ShouldBe(cancel ? 1 : 2);
            _attempts.Count.ShouldBe(cancel ? 2 : 3);
        }
        if (!cancel)
            Directory.Exists(Workspace(job)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_AggregateCheckpointDiffersFromOutcomeLedger_UsesLedger(bool aggregateAhead)
    {
        var job = await SeedAsync();
        job.Start("interrupted", startedAt: _clock.GetUtcNow());
        job.BeginClassification();
        job.SetDiscoveredFiles(0, 2);
        job.BeginDatIngestion();
        job.BeginRomIngestion();
        if (aggregateAhead)
        {
            job.SetCurrentItem("a.rom");
            job.RecordRomResult(RomIngestOutcome.Failed, occurredAt: _clock.GetUtcNow(), "uncommitted outcome");
            job.RecordRomResult(RomIngestOutcome.Failed, occurredAt: _clock.GetUtcNow(), "uncommitted outcome");
        }
        await using (var db = _database.CreateContext())
        {
            await new UploadJobRepository(db, _clock).CommitCheckpointAsync(db, job);
            var item = JobItem.ForRom(job.Id, "a.rom", 1, RomIngestOutcome.Ingested, null, null, null, null, createdAt: _clock.GetUtcNow());
            var repo = new JobItemRepository(db);
            db.JobItems.Add(JobItemEntity.FromDomain(item));
            await db.SaveChangesAsync();
            (await repo.GetOutcomesAsync(job.Id)).Count.ShouldBe(1);
        }
        await using var host = Host();
        await Runner(host).RunAsync(job.Id, "restart", default);
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync(item => item.Id == job.Id);
            row.Phase.ShouldBe("Completed");
            row.RomsProcessed.ShouldBe(2);
            row.RomsIngested.ShouldBe(1);
            row.RomsRejected.ShouldBe(1);
            row.GetErrors().ShouldBeEmpty();
            (await db.JobItems.CountAsync()).ShouldBe(2);
        }
        _attempts.ShouldBe(new[] { "z.rom" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentAdmissionOrUncertainCommit_ExhaustsDurableDeferralBudget(bool uncertain)
    {
        var job = await SeedAsync();
        _handler.HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ErrorOr<RomIngestResult>>(uncertain ? (Exception)new UploadOutcomeUncertainException("persistent commit failure") : new ResourceCapacityException("persistent admission failure")));
        await using var host = Host();
        DateTimeOffset? firstDeferral = null;
        for (int attempt = 0; attempt <= JobContext.MaxUploadDeferrals; attempt++)
        {
            await Runner(host).RunAsync(job.Id, $"attempt-{attempt}", default);
            await using var db = _database.CreateContext();
            var row = await db.Set<UploadJobEntity>().SingleAsync();
            if (attempt < JobContext.MaxUploadDeferrals)
            {
                row.DeferralCount.ShouldBe(attempt + 1);
                row.LastDeferralError.ShouldNotBeNull().ShouldContain("persistent");
                row.LastDeferralAt.ShouldNotBeNull();
                row.LastDeferralAt.Value.ShouldBeLessThanOrEqualTo(row.NextAttemptAtUtc!.Value);
                firstDeferral ??= row.FirstDeferralAt;
                row.FirstDeferralAt.ShouldBe(firstDeferral);
                int minimumSeconds = Math.Min(900, 15 << Math.Min(attempt, 6));
                (row.NextAttemptAtUtc.Value - _clock.GetUtcNow()).TotalSeconds.ShouldBeInRange(minimumSeconds, minimumSeconds * 2);
                (await db.JobDispatches.SingleAsync()).LastError.ShouldNotBeNull().ShouldContain("persistent");
                _clock.SetUtcNow(row.NextAttemptAtUtc!.Value);
            }
            else
            {
                row.Phase.ShouldBe("Failed");
                row.GetErrors().Last().Message.ShouldContain("budget exhausted");
                row.GetErrors().Last().Message.ShouldContain("persistent");
                (_clock.GetUtcNow() - firstDeferral!.Value).ShouldBeGreaterThan(TimeSpan.FromMinutes(40));
            }
        }
        Directory.Exists(Workspace(job)).ShouldBeFalse();
    }

    [Fact]
    public async Task DeferredUpload_OlderThanAgeBudget_FailsBeforeRepeatingIngestion()
    {
        var job = await SeedAsync();
        _handler.HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ErrorOr<RomIngestResult>>(new ResourceCapacityException("still occupied")));
        await using var host = Host();
        await Runner(host).RunAsync(job.Id, "first", default);
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync();
            row.DeferralCount.ShouldBe(1);
            _clock.SetUtcNow(row.FirstDeferralAt!.Value.AddDays(1).AddSeconds(1));
        }
        await Runner(host).RunAsync(job.Id, "expired", default);
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync();
            row.Phase.ShouldBe("Failed");
            row.GetErrors().Last().Message.ShouldContain("age budget exhausted");
            row.GetErrors().Last().Message.ShouldContain("still occupied");
        }
        await _handler.Received(2).HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>());
        Directory.Exists(Workspace(job)).ShouldBeFalse();
    }

    private async Task<UploadJob> SeedAsync()
    {
        var job = UploadJob.Create("games", createdAt: _clock.GetUtcNow());
        job.SetMaxParallelRoms(1);
        Directory.CreateDirectory(Workspace(job));
        await File.WriteAllBytesAsync(Path.Combine(Workspace(job), "a.rom"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(Workspace(job), "z.rom"), [2]);
        await using var db = _database.CreateContext();
        await using (var fixtureAcceptance = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            await new UploadJobRepository(db, _clock).AddStagedAsync(job);
            await fixtureAcceptance.CommitAsync();
        }
        return job;
    }

    [Fact]
    public async Task InspectionFailure_RecordsFailedFileAndContinuesSiblingsWithoutDeferral()
    {
        var job = await SeedAsync();
        _handler.HandleAsync(Arg.Is<IngestRomCommand>(command => command.OriginalFilename == "a.rom"), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ErrorOr<RomIngestResult>>(new InvalidDataException("fixture inspection deadline")));
        await using var host = Host();
        await Runner(host).RunAsync(job.Id, "inspect", default);
        await using var db = _database.CreateContext();
        var row = await db.Set<UploadJobEntity>().SingleAsync();
        row.Phase.ShouldBe("CompletedWithErrors");
        row.RomsProcessed.ShouldBe(2);
        row.DeferralCount.ShouldBe(0);
        row.FirstDeferralAt.ShouldBeNull();
        var outcomes = await db.JobItems.OrderBy(item => item.FileName).ToArrayAsync();
        outcomes.Length.ShouldBe(2);
        outcomes[0].Outcome.ShouldBe((int)JobItemOutcome.Failed);
        outcomes[1].Outcome.ShouldBe((int)JobItemOutcome.Rejected);
        _attempts.ShouldContain("z.rom");
    }

    [Fact]
    public async Task LateFirstDeferral_LongRunningImportGetsFullRetryAgeWindow()
    {
        var job = await SeedAsync();
        job.Start("old-import", startedAt: _clock.GetUtcNow());
        job.BeginClassification();
        job.SetDiscoveredFiles(0, 2);
        job.BeginDatIngestion();
        job.BeginRomIngestion();
        await using (var db = _database.CreateContext())
        {
            await new UploadJobRepository(db, _clock).CommitCheckpointAsync(db, job);
            await db.Jobs.Where(row => row.Id == job.Id).ExecuteUpdateAsync(set =>
                set.SetProperty(row => row.StartedAt, _clock.GetUtcNow().AddDays(-2)));
        }
        _handler.HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ErrorOr<RomIngestResult>>(new ResourceCapacityException("late capacity pressure")));
        await using var host = Host();
        await Runner(host).RunAsync(job.Id, "late", default);
        DateTimeOffset first;
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync();
            first = row.FirstDeferralAt!.Value;
            first.ShouldBe(_clock.GetUtcNow());
            _clock.SetUtcNow(row.NextAttemptAtUtc!.Value);
        }
        await Runner(host).RunAsync(job.Id, "retry", default);
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync();
            row.Phase.ShouldBe("IngestingRoms");
            row.DeferralCount.ShouldBe(2);
            row.FirstDeferralAt.ShouldBe(first);
            row.LastDeferralAt.ShouldBe(_clock.GetUtcNow());
        }
        await _handler.Received(4).HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractionIoFailure_DefersWithCheckpoint_ThenFreshRunnerCompletes(bool sevenZip)
    {
        var job = await SeedAsync();
        IArchiveExtractor extractor = sevenZip ? new SevenZipArchiveExtractor() : new ZipArchiveExtractor();
        string source = Path.Combine(Workspace(job), sevenZip ? "game.7z" : "game.zip");
        if (sevenZip)
            File.Copy(SevenZipArchiveExtractorTests.Fixture("large.7z"), source);
        else
        {
            using var zip = ZipFile.Open(source, ZipArchiveMode.Create);
            using var output = zip.CreateEntry("game.rom").Open();
            output.Write([1, 2, 3]);
        }
        await using (var host = Host())
            await Runner(host, new ArchiveExtractorResolver([new FailingZipExtractor(inner: extractor)])).RunAsync(job.Id, "io-failure", default);
        _attempts.ShouldBeEmpty();
        File.Exists(source).ShouldBeTrue();
        File.Exists(Workspace(job) + ".extraction.json").ShouldBeTrue();
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync();
            row.Phase.ShouldBe("Extracting");
            row.NextAttemptAtUtc.ShouldNotBeNull();
            row.GetErrors().ShouldBeEmpty();
            _clock.SetUtcNow(row.NextAttemptAtUtc.Value);
        }
        await using (var host = Host())
            await Runner(host, new ArchiveExtractorResolver([extractor])).RunAsync(job.Id, "recovered", default);
        await using var observed = _database.CreateContext();
        (await observed.Set<UploadJobEntity>().SingleAsync()).Phase.ShouldBe("Completed");
        _attempts.Order().ShouldBe(new[] { "a.rom", sevenZip ? "large.rom" : "game.rom", "z.rom" });
        Directory.Exists(Workspace(job)).ShouldBeFalse();
        File.Exists(Workspace(job) + ".extraction.json").ShouldBeFalse();
    }

    [Theory]
    [InlineData("encrypted.7z")]
    [InlineData("encrypted-headers.7z")]
    [InlineData("case-collision.7z")]
    [InlineData("declared-size.7z")]
    [InlineData("corrupt.7z")]
    public async Task ExtractionSevenZipFailure_IsTerminalWithoutDeferral(string fixture)
    {
        var job = await SeedAsync();
        File.Copy(SevenZipArchiveExtractorTests.Fixture(fixture), Path.Combine(Workspace(job), "game.7z"));
        await using var host = Host();
        await Runner(host, new ArchiveExtractorResolver([new SevenZipArchiveExtractor()])).RunAsync(job.Id, "bad-7z", default);
        await using var db = _database.CreateContext();
        var row = await db.Set<UploadJobEntity>().SingleAsync();
        row.Phase.ShouldBe("Failed");
        row.DeferralCount.ShouldBe(0);
        row.NextAttemptAtUtc.ShouldBeNull();
        row.GetErrors().ShouldNotBeEmpty();
        _attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExtractionPathTooLong_FailsWithoutDeferral()
    {
        var job = await SeedAsync();
        using (var zip = ZipFile.Open(Path.Combine(Workspace(job), "game.zip"), ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("game.rom").Open())
            entry.Write([1]);
        await using var host = Host();
        await Runner(host, new ArchiveExtractorResolver([new FailingZipExtractor(pathTooLong: true)])).RunAsync(job.Id, "long-path", default);
        await using var db = _database.CreateContext();
        var row = await db.Set<UploadJobEntity>().SingleAsync();
        row.Phase.ShouldBe("Failed");
        row.DeferralCount.ShouldBe(0);
        row.GetErrors().Last().Message.ShouldContain("filesystem limit");
    }

    private sealed class FailingZipExtractor(bool pathTooLong = false, IArchiveExtractor? inner = null) : IArchiveExtractor
    {
        private readonly IArchiveExtractor _zip = inner ?? new ZipArchiveExtractor();
        public IReadOnlyList<string> SupportedExtensions => _zip.SupportedExtensions;
        public bool CanHandle(string name) => _zip.CanHandle(name);
        public IAsyncEnumerable<ArchiveEntry> EnumerateAsync(Stream stream, CancellationToken ct = default) => _zip.EnumerateAsync(stream, ct);
        public Task<Stream> ExtractAsync(Stream stream, string entry, CancellationToken ct = default) => _zip.ExtractAsync(stream, entry, ct);
        public Task<IReadOnlyList<ExtractedEntry>> ExtractAllAsync(Stream stream, string target, IExtractionQuota quota, CancellationToken ct = default)
        {
            quota.TryChargeEntry().ShouldBeTrue();
            quota.TryChargeBytes(1).ShouldBeTrue();
            File.WriteAllBytes(Path.Combine(target, "partial.rom"), [9]);
            if (pathTooLong)
                throw new PathTooLongException("fixture path exceeds filesystem limit");
            throw new IOException("fixture interrupted extraction");
        }
    }

    [Fact]
    public async Task ExtractionPhaseSaveFails_CompletedFilesystemCheckpointSurvivesRedelivery()
    {
        var job = await SeedAsync();
        string source = Path.Combine(Workspace(job), "game.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        using (var output = zip.CreateEntry("game.rom").Open())
            output.Write([1, 2, 3]);
        var failure = new FailClassificationCheckpoint();
        await using (var host = Host(failure))
            await Should.ThrowAsync<DbUpdateException>(() => Runner(host,
                new ArchiveExtractorResolver([new ZipArchiveExtractor()])).RunAsync(job.Id, "same-delivery", default));
        failure.Fired.ShouldBeTrue();
        _attempts.ShouldBeEmpty();
        File.Exists(source).ShouldBeFalse();
        File.ReadAllBytes(Path.Combine(Workspace(job), "game", "game.rom")).ShouldBe(new byte[] { 1, 2, 3 });
        File.Exists(Workspace(job) + ".extraction.json").ShouldBeTrue();
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync();
            row.Phase.ShouldBe("Extracting");
            row.GetErrors().ShouldBeEmpty();
            _clock.SetUtcNow(row.ExecutionLeaseExpiresAtUtc!.Value.AddSeconds(1));
        }
        await using (var host = Host())
            await Runner(host, new ArchiveExtractorResolver([new ZipArchiveExtractor()])).RunAsync(job.Id, "same-delivery", default);
        await using var observed = _database.CreateContext();
        (await observed.Set<UploadJobEntity>().SingleAsync()).Phase.ShouldBe("Completed");
        (await observed.JobItems.CountAsync()).ShouldBe(3);
        File.Exists(Workspace(job) + ".extraction.json").ShouldBeFalse();
    }

    private sealed class FailClassificationCheckpoint : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            FailIfClassification(command);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            FailIfClassification(command);
            return ValueTask.FromResult(result);
        }

        private void FailIfClassification(DbCommand command)
        {
            if (!Fired && command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is "Classifying"))
            {
                Fired = true;
                throw new InvalidOperationException("fixture classification checkpoint unavailable");
            }
        }
    }

    [Theory]
    [InlineData("legacy", true)]
    [InlineData("claimed", false)]
    [InlineData("deferred", false)]
    [InlineData("dispatch", false)]
    public async Task StaleSweep_OnlyTerminatesLegacyWork(string recovery, bool shouldFail)
    {
        var job = await SeedAsync();
        job.Start("original", startedAt: _clock.GetUtcNow());
        await using var db = _database.CreateContext();
        await new UploadJobRepository(db, _clock).CommitCheckpointAsync(db, job);
        db.ChangeTracker.Clear();
        var row = await db.Set<UploadJobEntity>().AsTracking().SingleAsync();
        row.UpdatedAt = _clock.GetUtcNow().AddHours(-5);
        if (recovery == "claimed")
        {
            row.ExecutionFenceToken = Guid.NewGuid();
            row.ExecutionLeaseExpiresAtUtc = _clock.GetUtcNow().AddHours(-4);
        }
        if (recovery == "deferred")
            row.NextAttemptAtUtc = _clock.GetUtcNow().AddHours(-1);
        // New repository inserts already include durable dispatch. Remove it only to model
        // legacy rows and independently exercise the claim/deferral retention guards.
        if (recovery != "dispatch")
            await db.JobDispatches.ExecuteDeleteAsync();
        await db.SaveChangesAsync();
        (await new UploadJobRepository(db, _clock).FailStaleJobsAsync(TimeSpan.FromMinutes(30)))
            .ShouldBe(shouldFail ? 1 : 0);
        db.ChangeTracker.Clear();
        (await db.Set<UploadJobEntity>().SingleAsync()).Phase.ShouldBe(shouldFail ? "Failed" : "Extracting");
    }

    [Fact]
    public async Task OutcomeJournalReadFails_DefersWithoutProcessingOrDeletingInputs()
    {
        var job = await SeedAsync();
        var failure = new FailOutcomeRead();
        await using (var host = Host(failure))
            await Runner(host).RunAsync(job.Id, "unavailable", default);
        failure.Fired.ShouldBeTrue();
        _attempts.ShouldBeEmpty();
        File.Exists(Path.Combine(Workspace(job), "z.rom")).ShouldBeTrue();
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync();
            row.Phase.ShouldBe("Classifying");
            row.GetErrors().ShouldBeEmpty();
            row.NextAttemptAtUtc.ShouldNotBeNull();
            _clock.SetUtcNow(row.NextAttemptAtUtc.Value);
            (await db.JobItems.CountAsync()).ShouldBe(0);
        }
        await using (var host = Host())
            await Runner(host).RunAsync(job.Id, "recovered", default);
        await using var observed = _database.CreateContext();
        (await observed.Set<UploadJobEntity>().SingleAsync()).Phase.ShouldBe("Completed");
        (await observed.JobItems.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Resume_MissingCompletedFile_FailsClosedInsteadOfDoubleCounting()
    {
        var job = await SeedAsync();
        job.Start("interrupted", startedAt: _clock.GetUtcNow());
        job.BeginClassification();
        job.SetDiscoveredFiles(0, 2);
        job.BeginDatIngestion();
        job.BeginRomIngestion();
        await using (var db = _database.CreateContext())
        {
            await new UploadJobRepository(db, _clock).CommitCheckpointAsync(db, job);
            db.JobItems.Add(JobItemEntity.FromDomain(JobItem.ForRom(job.Id, "missing.rom", 1,
                RomIngestOutcome.Ingested, null, null, null, null, createdAt: _clock.GetUtcNow())));
            await db.SaveChangesAsync();
        }
        await using var host = Host();
        await Runner(host).RunAsync(job.Id, "restart", default);
        await using var observed = _database.CreateContext();
        var row = await observed.Set<UploadJobEntity>().SingleAsync();
        row.Phase.ShouldBe("Failed");
        row.GetErrors().ShouldHaveSingleItem().Message.ShouldContain("no longer contains");
        _attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task InvalidMedia_RemainsAFileFailureWithoutCapacityDeferral()
    {
        var job = await SeedAsync();
        _handler.HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>())
            .Returns<ErrorOr<RomIngestResult>>(_ => throw new InvalidDataException("invalid media"));
        await using var host = Host();
        await Runner(host).RunAsync(job.Id, "invalid", default);
        await using var observed = _database.CreateContext();
        var row = await observed.Set<UploadJobEntity>().SingleAsync();
        row.Phase.ShouldBe("CompletedWithErrors");
        row.NextAttemptAtUtc.ShouldBeNull();
        row.RomsProcessed.ShouldBe(2);
        row.GetErrors().Count.ShouldBe(2);
        (await observed.JobItems.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task TypedContainmentFailure_JournalsPermanentFailuresWithoutCapacityDeferral()
    {
        var job = await SeedAsync();
        _handler.HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>())
            .Returns(Error.Validation("Rom.Media.Containment", "Media containment was not confirmed."));
        await using var host = Host();
        await Runner(host).RunAsync(job.Id, "containment", default);
        await using var observed = _database.CreateContext();
        var row = await observed.Set<UploadJobEntity>().SingleAsync();
        row.Phase.ShouldBe("CompletedWithErrors");
        row.NextAttemptAtUtc.ShouldBeNull();
        row.RomsProcessed.ShouldBe(2);
        row.GetErrors().Count.ShouldBe(2);
        (await observed.JobItems.ToListAsync()).ShouldAllBe(item => item.Outcome == (int)JobItemOutcome.Failed);
        (await observed.JobItems.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task DeferralTransactionFails_RollsBackHandoffAndPreservesWorkspaceForLeaseRecovery()
    {
        var job = await SeedAsync();
        await using var occupied = await Factory().CreateAsync(new MemoryStream(new byte[8]));
        var failure = new FailDeferralDispatchUpdate();
        await using (var host = Host(failure))
            await Should.ThrowAsync<IOException>(() => Runner(host).RunAsync(job.Id, "failed-handoff", default));
        failure.Fired.ShouldBeTrue();
        DateTimeOffset lease;
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync();
            row.Phase.ShouldBe("IngestingRoms");
            row.NextAttemptAtUtc.ShouldBeNull();
            row.ExecutionFenceToken.ShouldNotBeNull();
            lease = row.ExecutionLeaseExpiresAtUtc!.Value;
            row.GetErrors().ShouldBeEmpty();
            (await db.JobItems.SingleAsync()).FileName.ShouldBe("a.rom");
        }
        Directory.Exists(Workspace(job)).ShouldBeTrue();
        await occupied.DisposeAsync();
        _clock.SetUtcNow(lease);
        await using (var host = Host())
            await Runner(host).RunAsync(job.Id, "recovered", default);
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync();
            row.Phase.ShouldBe("Completed");
            row.RomsProcessed.ShouldBe(2);
            (await db.JobItems.CountAsync()).ShouldBe(2);
        }
        _attempts.ShouldBe(new[] { "a.rom", "z.rom", "z.rom" });
    }

    [Fact]
    public async Task DatCapacityDeferral_ResumesDatPhaseAndRetainsPermanentFileErrors()
    {
        var job = await SeedAsync();
        await File.WriteAllBytesAsync(Path.Combine(Workspace(job), "a.dat"), [3]);
        await File.WriteAllBytesAsync(Path.Combine(Workspace(job), "z.dat"), [4]);
        var datAttempts = new List<string>();
        _datHandler.HandleAsync(Arg.Any<IngestDatCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var command = call.Arg<IngestDatCommand>();
            datAttempts.Add(command.OriginalFilename);
            if (command.OriginalFilename == "z.dat")
            {
                await using var staged = await Factory().CreateAsync(command.FileStream, call.Arg<CancellationToken>());
            }
            return (ErrorOr<DatIngestResult>)Error.Validation("Fixture.Invalid", "invalid catalog");
        });
        await using var occupied = await Factory().CreateAsync(new MemoryStream(new byte[8]));
        await using (var host = Host())
            await Runner(host).RunAsync(job.Id, "first", default);
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync();
            row.Phase.ShouldBe("IngestingDats");
            row.DatsProcessed.ShouldBe(1);
            row.GetErrors().ShouldHaveSingleItem().Message.ShouldBe("invalid catalog");
            _clock.SetUtcNow(row.NextAttemptAtUtc!.Value);
            (await db.JobItems.CountAsync()).ShouldBe(1);
        }
        _attempts.ShouldBeEmpty();
        await occupied.DisposeAsync();
        await using (var host = Host())
            await Runner(host).RunAsync(job.Id, "restart", default);
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync();
            row.Phase.ShouldBe("CompletedWithErrors");
            row.DatsProcessed.ShouldBe(2);
            row.RomsProcessed.ShouldBe(2);
            row.GetErrors().Count.ShouldBe(2);
        }
        datAttempts.ShouldBe(new[] { "a.dat", "z.dat", "z.dat" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelRetryDeferral_SettlesAndRecordsCompletedSibling(bool uncertainOutcome)
    {
        var job = await SeedAsync();
        job.SetMaxParallelRoms(2);
        await using (var db = _database.CreateContext())
            await new UploadJobRepository(db, _clock).CommitCheckpointAsync(db, job);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pressureReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (call.Arg<IngestRomCommand>().OriginalFilename == "a.rom")
            {
                firstStarted.SetResult();
                await pressureReached.Task.WaitAsync(TimeSpan.FromSeconds(10), call.Arg<CancellationToken>());
                // Inspections honour cancellation, including while a sibling cannot get a slot.
                await Task.Delay(100, call.Arg<CancellationToken>());
                return (ErrorOr<RomIngestResult>)Error.Validation("Library.UnidentifiedRom", "No match");
            }
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            pressureReached.SetResult();
            if (uncertainOutcome)
                throw new UploadOutcomeUncertainException("fixture outcome acknowledgement lost");
            throw new ResourceCapacityException("fixture occupied");
        });
        await using var host = Host();
        await Runner(host).RunAsync(job.Id, "parallel", default);
        await using var observed = _database.CreateContext();
        var row = await observed.Set<UploadJobEntity>().SingleAsync();
        row.Phase.ShouldBe("IngestingRoms");
        row.NextAttemptAtUtc.ShouldNotBeNull();
        row.RomsProcessed.ShouldBe(1);
        row.GetErrors().ShouldBeEmpty();
        (await observed.JobItems.SingleAsync()).FileName.ShouldBe("a.rom");
        Directory.Exists(Workspace(job)).ShouldBeTrue();
    }

    [Fact]
    public async Task ParallelInspection_QueuedCapacityDoesNotCancelWorkers_RestartOnlyProcessesUnfinishedFiles()
    {
        var job = await SeedAsync();
        File.Delete(Path.Combine(Workspace(job), "a.rom"));
        File.Delete(Path.Combine(Workspace(job), "z.rom"));
        string[] files = ["a.rvz", "b.rvz", "c.rvz", "d.rvz"];
        byte[] disc = new SyntheticRvzBuilder(0x100000, 0x20000).Build().File;
        foreach (string file in files)
            await File.WriteAllBytesAsync(Path.Combine(Workspace(job), file), disc);
        job.SetMaxParallelRoms(4); // Default upload parallelism; the verifier admits only two.
        await using (var db = _database.CreateContext())
            await new UploadJobRepository(db, _clock).CommitCheckpointAsync(db, job);
        // Admitted inspections block on their artifact until queued files have hit capacity.
        using var child = new MediaWorkerTestFixture(inspectionMode: "gated");
        child.Options.MaxConcurrent = 2;
        using var verifier = child.Verifier();
        var pressure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new ConcurrentDictionary<string, int>();
        int completedInspections = 0;
        int pressuredFiles = 0;
        _handler.HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            string name = call.Arg<IngestRomCommand>().OriginalFilename;
            attempts.AddOrUpdate(name, 1, (_, count) => count + 1);
            try
            {
                await using var staged = child.Source(await File.ReadAllBytesAsync(Path.Combine(Workspace(job), name),
                    call.Arg<CancellationToken>()), Path.GetFileNameWithoutExtension(name));
                (await verifier.VerifyAsync(staged, call.Arg<CancellationToken>())).IsError.ShouldBeFalse();
                Interlocked.Increment(ref completedInspections);
                return (ErrorOr<RomIngestResult>)Error.Validation("Library.UnidentifiedRom", "No match");
            }
            catch (ResourceCapacityException)
            {
                if (Interlocked.Increment(ref pressuredFiles) == 2)
                    pressure.TrySetResult();
                throw;
            }
        });
        await using (var host = Host())
        {
            Task running = Runner(host).RunAsync(job.Id, "parallel-workers", default);
            try
            { await pressure.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
            finally { child.Release(); }
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
        completedInspections.ShouldBe(2);
        string[] completedFiles;
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync();
            row.Phase.ShouldBe("IngestingRoms");
            row.RomsProcessed.ShouldBe(2);
            row.DeferralCount.ShouldBe(0);
            row.GetErrors().ShouldBeEmpty();
            completedFiles = await db.JobItems.Select(item => item.FileName).ToArrayAsync();
            completedFiles.Length.ShouldBe(2);
            _clock.SetUtcNow(row.NextAttemptAtUtc!.Value);
        }
        await using (var restarted = Host())
            await Runner(restarted).RunAsync(job.Id, "restart-workers", default);
        await using (var db = _database.CreateContext())
        {
            var row = await db.Set<UploadJobEntity>().SingleAsync();
            row.Phase.ShouldBe("Completed");
            row.RomsProcessed.ShouldBe(4);
            (await db.JobItems.CountAsync()).ShouldBe(4);
        }
        completedInspections.ShouldBe(4);
        foreach (string file in files)
            attempts[file].ShouldBe(completedFiles.Contains(file) ? 1 : 2);
    }

    [Fact]
    public async Task DurableProgressAcrossMoreThanEightDeferrals_ReplenishesBudgetAndPreservesFirstRetryTime()
    {
        var job = await SeedAsync();
        foreach (char name in "bcdefghi")
            await File.WriteAllBytesAsync(Path.Combine(Workspace(job), name + ".rom"), [1]);
        string[] files = Directory.GetFiles(Workspace(job)).Select(Path.GetFileName).Cast<string>().Order().ToArray();
        string admitted = files[0];
        var attempts = new Dictionary<string, int>();
        _handler.HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            string file = call.Arg<IngestRomCommand>().OriginalFilename;
            attempts[file] = attempts.GetValueOrDefault(file) + 1;
            if (file != admitted)
                throw new ResourceCapacityException("other job occupies inspection slots");
            return Task.FromResult((ErrorOr<RomIngestResult>)Error.Validation("Library.UnidentifiedRom", "No match"));
        });
        DateTimeOffset? firstDeferral = null;
        for (int index = 0; index < files.Length; index++)
        {
            admitted = files[index];
            await using (var restarted = Host())
                await Runner(restarted).RunAsync(job.Id, $"progress-{index}", default);
            await using var db = _database.CreateContext();
            var row = await db.Set<UploadJobEntity>().SingleAsync();
            row.RomsProcessed.ShouldBe(index + 1);
            row.DeferralCount.ShouldBe(0);
            row.GetErrors().ShouldBeEmpty();
            firstDeferral ??= row.FirstDeferralAt;
            row.FirstDeferralAt.ShouldBe(firstDeferral);
            if (index < files.Length - 1)
            {
                row.Phase.ShouldBe("IngestingRoms");
                (row.NextAttemptAtUtc!.Value - _clock.GetUtcNow()).TotalSeconds.ShouldBeInRange(15, 30);
                _clock.SetUtcNow(row.NextAttemptAtUtc.Value);
            }
            else
                row.Phase.ShouldBe("Completed");
        }
        for (int index = 0; index < files.Length; index++)
            attempts[files[index]].ShouldBe(index + 1);
    }

    [Fact]
    public async Task RecoverOutcomeAheadOfCheckpoint_ReplenishesPersistedRetryBudget()
    {
        var job = await SeedAsync();
        job.Start("lost-checkpoint", startedAt: _clock.GetUtcNow());
        job.BeginClassification();
        job.SetDiscoveredFiles(0, 2);
        job.BeginDatIngestion();
        job.BeginRomIngestion();
        for (int index = 0; index < JobContext.MaxUploadDeferrals; index++)
            job.RecordDeferral("previous pressure", _clock.GetUtcNow());
        await using (var db = _database.CreateContext())
        {
            await new UploadJobRepository(db, _clock).CommitCheckpointAsync(db, job);
            db.JobItems.Add(JobItemEntity.FromDomain(JobItem.ForRom(job.Id, "a.rom", 1,
                RomIngestOutcome.Rejected, null, null, null, null, createdAt: _clock.GetUtcNow())));
            await db.SaveChangesAsync();
        }
        _handler.HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ErrorOr<RomIngestResult>>(new ResourceCapacityException("still occupied")));
        await using (var host = Host())
            await Runner(host).RunAsync(job.Id, "recover-progress", default);
        await using var observed = _database.CreateContext();
        var row = await observed.Set<UploadJobEntity>().SingleAsync();
        row.Phase.ShouldBe("IngestingRoms");
        row.RomsProcessed.ShouldBe(1);
        row.DeferralCount.ShouldBe(0);
        row.FirstDeferralAt.ShouldBe(job.FirstDeferralAt);
        (row.NextAttemptAtUtc!.Value - _clock.GetUtcNow()).TotalSeconds.ShouldBeInRange(15, 30);
        await _handler.Received(1).HandleAsync(Arg.Is<IngestRomCommand>(command => command.OriginalFilename == "z.rom"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FinalOutcomeFlushReplenishesBudget_RetryUsesInitialDelayInsteadOfStaleBackoff()
    {
        var job = await SeedAsync();
        for (int attempt = 0; attempt < JobContext.MaxUploadDeferrals; attempt++)
            job.RecordDeferral("prior stalled attempt", _clock.GetUtcNow());
        await using (var db = _database.CreateContext())
            await new UploadJobRepository(db, _clock).CommitCheckpointAsync(db, job);
        _handler.HandleAsync(Arg.Is<IngestRomCommand>(command => command.OriginalFilename == "z.rom"), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ErrorOr<RomIngestResult>>(new ResourceCapacityException("inspection occupied")));
        await using (var host = Host())
            await Runner(host).RunAsync(job.Id, "final-flush", default);
        await using var observed = _database.CreateContext();
        var row = await observed.Set<UploadJobEntity>().SingleAsync();
        row.Phase.ShouldBe("IngestingRoms");
        row.DeferralCount.ShouldBe(0);
        row.RomsProcessed.ShouldBe(1);
        (await observed.JobItems.SingleAsync()).FileName.ShouldBe("a.rom");
        (row.NextAttemptAtUtc!.Value - _clock.GetUtcNow()).TotalSeconds.ShouldBeInRange(15, 30);
        row.FirstDeferralAt.ShouldBe(job.FirstDeferralAt);
    }

    private string Workspace(UploadJob job) => Path.Combine(_root, "temp", "jobs", job.Id.ToString("N"));
    private LocalTempFileFactory Factory() => new(_options, new FileSystem(), new TempStorageOptions
    {
        MaximumBytes = 8,
        MaximumFiles = 1,
        MinimumFreeBytes = 0,
        AdmissionTimeoutSeconds = 1
    });

    private ServiceProvider Host(IInterceptor? interceptor = null, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => interceptor is null ? _database.CreateContext() : new RomdDbContext(
            new DbContextOptionsBuilder<RomdDbContext>().UseNpgsql(_database.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).AddInterceptors(interceptor).Options));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(new JobExecutionClaimOptions());
        services.AddScoped<UploadJobRepository>();
        services.AddScoped<IJobRepository<UploadJob>, ClaimedJobRepository<UploadJob, UploadJobRepository>>();
        services.AddScoped<IJobItemRepository, JobItemRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IAuditContext, JobAuditContext>();
        services.AddRomdJobLifecyclePolicies();
        services.AddSingleton(_handler);
        services.AddSingleton(_datHandler);
        services.AddResiliencePolicies();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private JobRunner<UploadJob> Runner(ServiceProvider host, IArchiveExtractorResolver? resolver = null,
        Action<Exception>? onExecutorFailure = null, JobExecutionClaimOptions? claimOptions = null)
    {
        var classifier = Substitute.For<IFileClassifier>();
        classifier.ClassifyAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new FileClassification(call.Arg<string>().EndsWith(".dat", StringComparison.Ordinal)
                ? FileType.Dat : FileType.Rom, null, 1));
        resolver ??= Substitute.For<IArchiveExtractorResolver>();
        var fs = Substitute.For<IFileSystem>();
        var drive = Substitute.For<IDriveInfo>();
        drive.AvailableFreeSpace.Returns(long.MaxValue);
        fs.DriveInfo.New(Arg.Any<string>()).Returns(drive);
        var extraction = new ArchiveExtractionStep(resolver, fs, new(), NullLogger<ArchiveExtractionStep>.Instance);
        var resilience = host.GetRequiredService<ResiliencePipelineProvider<string>>();
        var scopes = host.GetRequiredService<IServiceScopeFactory>();
        var executor = new UploadJobExecutor(extraction, classifier,
            new DatProcessor(host, resilience, NullLogger<DatProcessor>.Instance),
            new RomProcessor(host, resilience, NullLogger<RomProcessor>.Instance),
            null!, Substitute.For<ICommandHandler<QueueBulkEnrichmentJobCommand, Guid>>(), [], scopes, NullLogger<UploadJobExecutor>.Instance, _clock);
        IJobExecutor<UploadJob> observed = onExecutorFailure is null ? executor : new FailureCapturingUploadExecutor(executor, onExecutorFailure);
        return new JobRunner<UploadJob>(scopes, observed, Substitute.For<IJobNotifier>(), _options,
            _clock, claimOptions ?? new JobExecutionClaimOptions(), NullLogger<JobRunner<UploadJob>>.Instance);
    }

    public void Dispose()
    {
        _database.Dispose();
        Directory.Delete(_root, true);
    }

    private sealed class FailDeferralDispatchUpdate : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)
                && command.CommandText.Contains("\"JobDispatches\"", StringComparison.Ordinal))
            {
                Fired = true;
                throw new IOException("fixture dispatch write failed");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailOutcomeRead : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("FROM romd.\"JobItems\"", StringComparison.Ordinal))
            {
                Fired = true;
                throw new IOException("fixture journal unavailable");
            }
            return ValueTask.FromResult(result);
        }
    }
}
