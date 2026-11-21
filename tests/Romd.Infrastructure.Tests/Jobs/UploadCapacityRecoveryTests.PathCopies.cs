using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Import.Commands.ImportFromPath;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Import;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Lifecycle;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Infrastructure.Tests.Import;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed partial class UploadCapacityRecoveryTests
{
    [Fact]
    public async Task PathImport_RedeliveryAfterCopyFailure_KeepsIssueAndBothMoveSources()
    {
        var job = await SeedAsync();
        string source = Directory.CreateDirectory(Path.Combine(_root, "interrupted-originals")).FullName;
        _options.AllowedImportPaths.Returns([source]);
        string good = Path.Combine(source, "a.rom");
        string bad = Path.Combine(source, "z.rom");
        await File.WriteAllBytesAsync(good, [1]);
        await File.WriteAllBytesAsync(bad, [2]);
        File.Delete(Path.Combine(Workspace(job), "z.rom"));
        var info = new FileInfo(good);
        await ImportManifest.WriteAsync(ImportManifest.PathFor(_root, job.Id),
            new(ImportManifest.CurrentVersion, source, ".", true, [new("a.rom", "a.rom", info.Length, info.LastWriteTimeUtc)]), default);
        var now = _clock.GetUtcNow();
        job.SetImportSource(source, move: true);
        job.Start("interrupted", now);
        job.BeginExtraction([new JobError(bad, "Could not read source.", now)]);
        job.BeginClassification();
        job.SetDiscoveredFiles(0, 1);
        job.BeginDatIngestion();
        job.BeginRomIngestion();
        var stored = JobItem.ForRom(job.Id, "a.rom", 1, RomIngestOutcome.Ingested, 1, [], null, null, now);
        await using (var db = _database.CreateContext())
        {
            await new UploadJobRepository(db, _clock).CommitCheckpointAsync(db, job);
            db.JobItems.Add(JobItemEntity.FromDomain(stored));
            await db.SaveChangesAsync();
        }

        await using (var host = Host(configure: services =>
            services.AddScoped<IJobCompletionCleanup<UploadJob>>(provider =>
                new MoveImportCompletionCleanup(new ImportSourceCleanup(_options, new PathValidator(_options),
                    provider.GetRequiredService<IJobItemRepository>(), NullLogger<ImportSourceCleanup>.Instance)))))
            await Runner(host).RunAsync(job.Id, "redelivered", default);

        _attempts.ShouldBeEmpty();
        await using var observed = _database.CreateContext();
        var row = await observed.Jobs.OfType<UploadJobEntity>().SingleAsync();
        row.Phase.ShouldBe("CompletedWithErrors");
        row.GetErrors().ShouldHaveSingleItem().Reason.ShouldBe(JobErrorReason.ImportSourceCopy);
        row.RomsIngested.ShouldBe(1);
        (await observed.JobItems.SingleAsync()).Id.ShouldBe(stored.Id);
        (await File.ReadAllBytesAsync(good)).ShouldBe([1]);
        (await File.ReadAllBytesAsync(bad)).ShouldBe([2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PathImport_WorkerCopyAdmitsOnlyCompleteFilesToEnumerationAndDurableOutcome(bool move)
    {
        string source = Directory.CreateTempSubdirectory("romd-worker-copy-source-").FullName;
        try
        {
            byte[] good = [1, 2, 3, 4];
            byte[] bad = [5, 6, 7, 8];
            await File.WriteAllBytesAsync(Path.Combine(source, "a-good.rom"), good);
            await File.WriteAllBytesAsync(Path.Combine(source, "z-bad.rom"), bad);
            _options.AllowedImportPaths.Returns([source]);
            var files = new PathCopyFailureFileSystem();
            files.CopyFailures["z-bad.rom"] = new IOException("source failed after two bytes");
            var preparation = new PathImportPreparation(_options, new PathValidator(_options), files.FileSystem);
            Guid id;
            await using (var db = _database.CreateContext())
            {
                var handler = new ImportFromPathCommandHandler(Substitute.For<ITrackedTitleReader>(),
                    Substitute.For<IReferenceCatalogService>(), Substitute.For<ICurrentUser>(), preparation,
                    new JobWorkspaceExclusion(_options), new UploadJobRepository(db, _clock), new EfUnitOfWork(db),
                    _clock, NullLogger<ImportFromPathCommandHandler>.Instance);

                var accepted = await handler.HandleAsync(new(source, Move: move));

                accepted.IsError.ShouldBeFalse();
                id = accepted.Value;
            }
            // Acceptance answers before any bytes move: the workspace is reserved and empty.
            string workspace = Path.Combine(_root, "temp", "jobs", id.ToString("N"));
            Directory.EnumerateFileSystemEntries(workspace).ShouldBeEmpty();
            File.Exists(ImportManifest.PathFor(_root, id)).ShouldBeFalse();
            files.Streams.ShouldBeEmpty();
            await using (var read = _database.CreateContext())
            {
                (await read.JobDispatches.SingleAsync()).JobId.ShouldBe(id);
                var accepted = await read.Jobs.OfType<UploadJobEntity>().SingleAsync();
                accepted.Phase.ShouldBe("Pending");
                accepted.GetErrors().ShouldBeEmpty();
            }

            // The real executor discovers workspace files itself and journals through the real scoped sink.
            await using (var host = Host(configure: services =>
                services.AddScoped<IJobCompletionCleanup<UploadJob>>(provider =>
                    new MoveImportCompletionCleanup(new ImportSourceCleanup(_options, new PathValidator(_options),
                        provider.GetRequiredService<IJobItemRepository>(), NullLogger<ImportSourceCleanup>.Instance)))))
                await Runner(host, sourceFiles: files.FileSystem).RunAsync(id, "path-partial-copy", default);

            _attempts.ShouldBe(["a-good.rom"]);
            files.DeleteAttempts.ShouldHaveSingleItem().Bytes.ShouldBe(new byte[] { 5, 6 });
            await using (var read = _database.CreateContext())
            {
                var job = await read.Jobs.OfType<UploadJobEntity>().SingleAsync();
                job.Phase.ShouldBe("CompletedWithErrors");
                job.GetErrors().ShouldContain(error => error.Item.EndsWith("z-bad.rom", StringComparison.Ordinal));
                job.DatsDiscovered.ShouldBe(0);
                job.RomsDiscovered.ShouldBe(1);
                job.RomsProcessed.ShouldBe(1);
                (await read.JobItems.Select(item => item.FileName).ToListAsync()).ShouldBe(["a-good.rom"]);
            }
            (await File.ReadAllBytesAsync(Path.Combine(source, "a-good.rom"))).ShouldBe(good);
            (await File.ReadAllBytesAsync(Path.Combine(source, "z-bad.rom"))).ShouldBe(bad);
            foreach (var stream in files.Streams)
                stream.DisposeCount.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }
}
