using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Import;
using Romd.Infrastructure.Jobs;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

public sealed partial class AdminMutationOutboxAtomicityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueSafety_MoveJobsSharingSources_KeepProvenanceManifestsAndWorkspacesIsolated(bool bothStored)
    {
        await using var database = await TestDatabase.CreateAsync();
        string root = Directory.CreateTempSubdirectory("romd-queue-safety-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "temp", "jobs"));
            string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            string shared = Path.Combine(source, "shared.rom");
            string retained = Path.Combine(source, "retained.rom");
            await File.WriteAllTextAsync(shared, "durably stored");
            await File.WriteAllTextAsync(retained, "rejected");
            var entries = new[] { shared, retained }.Select(path =>
            {
                var file = new FileInfo(path);
                return new ImportManifestEntry(file.Name, file.Name, file.Length, file.LastWriteTimeUtc);
            }).ToArray();
            var jobs = new[] { UploadJob.Create("first"), UploadJob.Create("second") };
            foreach (var job in jobs)
            {
                job.SetImportSource(source, move: true);
                await new UploadJobRepository(database.Context, TimeProvider.System).AddAsync(job);
                database.Context.ChangeTracker.Clear();
                job.Start(job.Id.ToString());
                job.BeginClassification();
                job.BeginDatIngestion();
                job.BeginRomIngestion();
                job.Complete();
                await new UploadJobRepository(database.Context, TimeProvider.System).UpdateAsync(job);
                database.Context.ChangeTracker.Clear();
                await ImportManifest.WriteAsync(ImportManifest.PathFor(root, job.Id),
                    new ImportManifest(ImportManifest.CurrentVersion, source, ".", true, entries), CancellationToken.None);
                await new JobItemRepository(database.Context).AddRangeAsync([
                    JobItem.ForRom(job.Id, "shared.rom", entries[0].SizeBytes,
                        job == jobs[0] || bothStored ? RomIngestOutcome.Ingested : RomIngestOutcome.Rejected,
                        null, [], null, null),
                    JobItem.ForRom(job.Id, "retained.rom", entries[1].SizeBytes,
                        RomIngestOutcome.Rejected, null, [], null, null)
                ]);
                database.Context.ChangeTracker.Clear();
            }
            await using var firstLock = await JobWorkspace.AcquireExecutionLockAsync(root, jobs[0].Id, default);
            await using var secondLock = await JobWorkspace.AcquireExecutionLockAsync(root, jobs[1].Id, default);
            await using var firstWorkspace = JobWorkspace.Create(root, jobs[0].Id);
            await using var secondWorkspace = JobWorkspace.Create(root, jobs[1].Id);
            foreach (var workspace in new[] { firstWorkspace, secondWorkspace })
                await File.WriteAllTextAsync(Path.Combine(workspace.Path, "shared.rom"), "staged copy");
            var gates = new[] { new QueueOperationGate(), new QueueOperationGate() };
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(root);
            options.AllowedImportPaths.Returns([source]);
            async Task Cleanup(int index)
            {
                await using var db = database.CreateReadContext();
                var job = await new UploadJobRepository(db, TimeProvider.System).GetByIdAsync(jobs[index].Id);
                job.ShouldNotBeNull();
                var items = Substitute.For<IJobItemRepository>();
                items.GetAllByJobAsync(job.Id, Arg.Any<CancellationToken>()).Returns(async call =>
                {
                    var result = await new JobItemRepository(db).GetAllByJobAsync(job.Id, call.Arg<CancellationToken>());
                    await gates[index].WaitAsync(call.Arg<CancellationToken>());
                    return result;
                });
                await new ImportSourceCleanup(options, new PathValidator(options), items,
                    NullLogger<ImportSourceCleanup>.Instance).RunCompletedAsync(job, default);
            }
            var first = Cleanup(0);
            var second = Cleanup(1);
            await Task.WhenAll(gates.Select(g => g.Entered.Task)).WaitAsync(TimeSpan.FromSeconds(20));
            gates[0].Release.TrySetResult();
            await first;
            await firstWorkspace.DisposeAsync();
            File.Exists(Path.Combine(secondWorkspace.Path, "shared.rom")).ShouldBeTrue();
            gates[1].Release.TrySetResult();
            await second;
            File.Exists(shared).ShouldBeFalse();
            File.Exists(retained).ShouldBeTrue();
            File.Exists(ImportManifest.PathFor(root, jobs[0].Id)).ShouldBeFalse();
            // A second successful import sees the source as not visible and retains only its
            // own manifest for the existing bounded retry/age-out policy. It cannot delete
            // the rejected file or the other job's staged copy.
            File.Exists(ImportManifest.PathFor(root, jobs[1].Id)).ShouldBe(bothStored);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
