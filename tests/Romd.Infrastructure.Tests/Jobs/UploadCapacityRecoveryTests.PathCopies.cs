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
using Romd.Infrastructure.Tests.Import;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed partial class UploadCapacityRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PathImport_PartialCopyNeverReachesWorkerEnumerationOrDurableOutcome(bool move)
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
            var preparation = new PathImportPreparation(_options, new PathValidator(_options), files.FileSystem,
                _clock, NullLogger<PathImportPreparation>.Instance);
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
            string workspace = Path.Combine(_root, "temp", "jobs", id.ToString("N"));
            Directory.GetFiles(workspace, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(workspace, path)).ShouldBe(["a-good.rom"]);
            (await File.ReadAllBytesAsync(Path.Combine(workspace, "a-good.rom"))).ShouldBe(good);
            files.DeleteAttempts.ShouldHaveSingleItem().Bytes.ShouldBe(new byte[] { 5, 6 });
            if (move)
            {
                var manifest = (await ImportManifest.ReadAsync(ImportManifest.PathFor(_root, id), default)).ShouldNotBeNull();
                manifest.Entries.ShouldHaveSingleItem().WorkspaceRelativePath.ShouldBe("a-good.rom");
            }
            await using (var read = _database.CreateContext())
            {
                (await read.JobDispatches.SingleAsync()).JobId.ShouldBe(id);
                var job = await read.Jobs.OfType<UploadJobEntity>().SingleAsync();
                job.GetErrors().ShouldContain(error => error.Item.EndsWith("z-bad.rom", StringComparison.Ordinal));
            }

            // The real executor discovers workspace files itself and journals through the real scoped sink.
            await using (var host = Host(configure: services =>
                services.AddScoped<IJobCompletionCleanup<UploadJob>>(provider =>
                    new MoveImportCompletionCleanup(new ImportSourceCleanup(_options, new PathValidator(_options),
                        provider.GetRequiredService<IJobItemRepository>(), NullLogger<ImportSourceCleanup>.Instance)))))
                await Runner(host).RunAsync(id, "path-partial-copy", default);

            _attempts.ShouldBe(["a-good.rom"]);
            await using (var read = _database.CreateContext())
            {
                var job = await read.Jobs.OfType<UploadJobEntity>().SingleAsync();
                job.Phase.ShouldBe("CompletedWithErrors");
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
