using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Systems;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Storage.Files;
using Romd.Infrastructure.Export;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Domain.Jobs;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Tests.Persistence;
using Romd.Persistence.Entities;
using Romd.Infrastructure.Jobs.Executors;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class ExportJobExecutorFormatTests
{
    private static readonly RomDeliveryPolicy DeliveryPolicy = RomMediaPolicyComposition.CreateDeliveryPolicy();

    [Fact]
    public async Task ExecuteAsync_StoringReplayLosesOwnership_PreservesDurablePriorAndLiveSuccessorArtifacts()
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-archive-fence-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            string prior = Path.Combine(directory, "prior.zip");
            string successor = Path.Combine(directory, "successor.zip");
            await File.WriteAllBytesAsync(prior, new byte[] { 9 });
            await File.WriteAllBytesAsync(successor, new byte[] { 8 });
            var job = StoringJob(prior);
            var durable = job;
            var files = Substitute.For<IExportRepository>();
            files.GetExportFilesAsync(Arg.Any<ExportScope>(), Arg.Any<CancellationToken>())
                .Returns([new ExportFileData(1, "Title", 1, "Platform", 7, 3, "game.iso")]);
            var writer = Substitute.For<IExportArchiveWriter>();
            var catalog = IdentityCatalog();
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(directory);
            bool ownershipChecked = false;
            var context = new JobContext(null, _ =>
            {
                durable = ExportJobEntity.FromDomain(job).ToDomain();
                return Task.CompletedTask;
            }, CancellationToken.None, ownedMutationDelegate: (_, _) =>
            {
                ownershipChecked = true;
                throw new JobExecutionOwnershipLostException();
            });

            await Should.ThrowAsync<JobExecutionOwnershipLostException>(() =>
                new ExportJobExecutor(files, writer, options, Substitute.For<ILogger<ExportJobExecutor>>(), catalog, DeliveryPolicy, clock: TimeProvider.System, chdAdmission: new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null))
                    .ExecuteAsync(job, context));

            ownershipChecked.ShouldBeTrue();
            durable.ExportPath.ShouldBe(prior);
            File.ReadAllBytes(prior).ShouldBe(new byte[] { 9 });
            File.ReadAllBytes(successor).ShouldBe(new byte[] { 8 });
            Directory.Exists(Path.Combine(directory, "exports")).ShouldBeFalse();
            writer.ReceivedCalls().ShouldBeEmpty();
            catalog.ReceivedCalls().ShouldBeEmpty();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_StoringReplayCannotDeletePriorPath_KeepsDurableReferenceWithoutStartingWriter()
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-archive-delete-" + Guid.NewGuid().ToString("N"));
        try
        {
            // An externally replaced directory at the artifact path deterministically rejects File.Delete on every host.
            string prior = Path.Combine(directory, "prior.zip");
            Directory.CreateDirectory(prior);
            var job = StoringJob(prior);
            var durable = job;
            var files = Substitute.For<IExportRepository>();
            files.GetExportFilesAsync(Arg.Any<ExportScope>(), Arg.Any<CancellationToken>())
                .Returns([new ExportFileData(1, "Title", 1, "Platform", 7, 3, "game.iso")]);
            var writer = Substitute.For<IExportArchiveWriter>();
            var catalog = IdentityCatalog();
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(directory);
            var context = new JobContext(null, _ =>
            {
                durable = ExportJobEntity.FromDomain(job).ToDomain();
                return Task.CompletedTask;
            }, CancellationToken.None);

            var error = await Record.ExceptionAsync(() =>
                new ExportJobExecutor(files, writer, options, Substitute.For<ILogger<ExportJobExecutor>>(), catalog, DeliveryPolicy, clock: TimeProvider.System, chdAdmission: new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null))
                    .ExecuteAsync(job, context));

            (error is IOException or UnauthorizedAccessException).ShouldBeTrue();
            job.ExportPath.ShouldBe(prior);
            durable.ExportPath.ShouldBe(prior);
            Directory.Exists(prior).ShouldBeTrue();
            writer.ReceivedCalls().ShouldBeEmpty();
            catalog.ReceivedCalls().ShouldBeEmpty();
            Directory.Exists(Path.Combine(directory, "exports")).ShouldBeFalse();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_StoringReplayInitialCheckpointFails_LeavesPriorFileAndReferenceForRecovery()
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-archive-checkpoint-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            string prior = Path.Combine(directory, "prior.zip");
            await File.WriteAllBytesAsync(prior, new byte[] { 9 });
            var job = StoringJob(prior);
            var files = Substitute.For<IExportRepository>();
            files.GetExportFilesAsync(Arg.Any<ExportScope>(), Arg.Any<CancellationToken>())
                .Returns([new ExportFileData(1, "Title", 1, "Platform", 7, 3, "game.iso")]);
            var writer = Substitute.For<IExportArchiveWriter>();
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(directory);
            bool mutationInvoked = false;
            var context = new JobContext(null, _ => throw new IOException("fixture checkpoint unavailable"),
                CancellationToken.None, ownedMutationDelegate: (mutation, token) =>
                {
                    mutationInvoked = true;
                    return mutation(token);
                });

            await Should.ThrowAsync<IOException>(() =>
                new ExportJobExecutor(files, writer, options, Substitute.For<ILogger<ExportJobExecutor>>(), IdentityCatalog(), DeliveryPolicy, clock: TimeProvider.System, chdAdmission: new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null))
                    .ExecuteAsync(job, context));

            mutationInvoked.ShouldBeFalse();
            job.ExportPath.ShouldBe(prior);
            File.ReadAllBytes(prior).ShouldBe(new byte[] { 9 });
            writer.ReceivedCalls().ShouldBeEmpty();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_StoringReplayClearCheckpointFails_RepeatsDeletionFromDurableReferenceAndCompletes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-archive-clear-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            string prior = Path.Combine(directory, "prior.zip");
            await File.WriteAllBytesAsync(prior, new byte[] { 9 });
            var job = StoringJob(prior);
            var durable = job;
            var files = Substitute.For<IExportRepository>();
            files.GetExportFilesAsync(Arg.Any<ExportScope>(), Arg.Any<CancellationToken>())
                .Returns([new ExportFileData(1, "Title", 1, "Platform", 7, 3, "game.iso")]);
            var writer = Substitute.For<IExportArchiveWriter>();
            writer.WriteAsync(Arg.Any<Stream>(), Arg.Any<IReadOnlyList<ExportArchiveEntry>>(),
                Arg.Any<Func<ExportArchiveEntry, bool, Task>?>(), true, Arg.Any<CancellationToken>()).Returns(async call =>
                {
                    await call.ArgAt<Stream>(0).WriteAsync(new byte[] { 1, 2, 3 });
                    await call.ArgAt<Func<ExportArchiveEntry, bool, Task>>(2)(
                        call.ArgAt<IReadOnlyList<ExportArchiveEntry>>(1).Single(), true);
                });
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(directory);
            var executor = new ExportJobExecutor(files, writer, options, Substitute.For<ILogger<ExportJobExecutor>>(), IdentityCatalog(), DeliveryPolicy, clock: TimeProvider.System, chdAdmission: new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));
            int checkpoints = 0;
            var context = new JobContext(null, _ =>
            {
                if (++checkpoints == 2)
                    throw new IOException("fixture clearing checkpoint unavailable");
                durable = ExportJobEntity.FromDomain(job).ToDomain();
                return Task.CompletedTask;
            }, CancellationToken.None);

            await Should.ThrowAsync<IOException>(() => executor.ExecuteAsync(job, context));

            durable.ExportPath.ShouldBe(prior);
            File.Exists(prior).ShouldBeFalse();
            writer.ReceivedCalls().ShouldBeEmpty();
            job = durable;
            await executor.ExecuteAsync(job, new JobContext(null, _ => Task.CompletedTask, CancellationToken.None));

            job.PhaseEnum.ShouldBe(ExportPhase.Storing);
            job.ProcessedFiles.ShouldBe(1);
            File.ReadAllBytes(job.ExportPath.ShouldNotBeNull()).ShouldBe(new byte[] { 1, 2, 3 });
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_StoringReplayWithClosedPriorArtifact_ReclaimsItBeforeSuccessfulOrFailedNewAttempt(bool failWriter)
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-archive-prior-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            string prior = Path.Combine(directory, "prior.zip");
            string successor = Path.Combine(directory, "successor.zip");
            await File.WriteAllBytesAsync(prior, new byte[] { 9 });
            await File.WriteAllBytesAsync(successor, new byte[] { 8 });
            var job = StoringJob(prior);
            var files = Substitute.For<IExportRepository>();
            files.GetExportFilesAsync(Arg.Any<ExportScope>(), Arg.Any<CancellationToken>())
                .Returns([new ExportFileData(1, "Title", 1, "Platform", 7, 3, "game.iso")]);
            var catalog = IdentityCatalog();
            var writer = Substitute.For<IExportArchiveWriter>();
            bool priorWasPresentAtWriter = false;
            writer.WriteAsync(Arg.Any<Stream>(), Arg.Any<IReadOnlyList<ExportArchiveEntry>>(),
                Arg.Any<Func<ExportArchiveEntry, bool, Task>?>(), true, Arg.Any<CancellationToken>()).Returns(async call =>
                {
                    priorWasPresentAtWriter = File.Exists(prior);
                    await call.ArgAt<Stream>(0).WriteAsync(new byte[] { 1, 2, 3 });
                    if (failWriter)
                        throw new IOException("fixture writer failure");
                    await call.ArgAt<Func<ExportArchiveEntry, bool, Task>>(2)(
                        call.ArgAt<IReadOnlyList<ExportArchiveEntry>>(1).Single(), true);
                });
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(directory);
            var snapshots = new List<ExportJob>();
            var context = new JobContext(null, _ =>
            {
                snapshots.Add(ExportJobEntity.FromDomain(job).ToDomain());
                return Task.CompletedTask;
            }, CancellationToken.None);
            var executor = new ExportJobExecutor(files, writer, options, Substitute.For<ILogger<ExportJobExecutor>>(), catalog, DeliveryPolicy, clock: TimeProvider.System, chdAdmission: new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));

            if (failWriter)
                await Should.ThrowAsync<IOException>(() => executor.ExecuteAsync(job, context));
            else
                await executor.ExecuteAsync(job, context);

            File.Exists(prior).ShouldBeFalse();
            priorWasPresentAtWriter.ShouldBeFalse();
            snapshots[0].ExportPath.ShouldBe(prior);
            snapshots[1].ExportPath.ShouldBeNull();
            File.ReadAllBytes(successor).ShouldBe(new byte[] { 8 });
            if (failWriter)
            {
                job.ExportPath.ShouldBeNull();
                Directory.GetFiles(Path.Combine(directory, "exports")).ShouldBeEmpty();
            }
            else
                File.ReadAllBytes(job.ExportPath.ShouldNotBeNull()).ShouldBe(new byte[] { 1, 2, 3 });
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_RenamedPathsCollide_RecordsPlanningErrorWithoutCreatingArchive()
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-archive-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = Substitute.For<IExportRepository>();
            files.GetExportFilesAsync(Arg.Any<ExportScope>(), Arg.Any<CancellationToken>()).Returns([
                new ExportFileData(1, "Title", 1, "Platform", 7, 3, "game.iso"),
                new ExportFileData(1, "Title", 1, "Platform", 8, 4, "game.rvz")
            ]);
            var catalog = Substitute.For<IRomContentCatalog>();
            var logical = Sha256.Parse(new string('a', 64));
            var artifact = Sha256.Parse(new string('b', 64));
            SetCatalogContent(catalog, RvzContent(7, 3, logical, artifact, 100, 50),
            OriginalContent(8, 4, logical, 100));
            var writer = Substitute.For<IExportArchiveWriter>();
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(directory);
            var job = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow, format: RomDeliveryFormat.Rvz);
            job.Start("delivery", startedAt: DateTimeOffset.UtcNow);

            await new ExportJobExecutor(files, writer, options, Substitute.For<ILogger<ExportJobExecutor>>(), catalog, DeliveryPolicy, clock: TimeProvider.System, chdAdmission: new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null))
                .ExecuteAsync(job, new JobContext(null, _ => Task.CompletedTask, CancellationToken.None));

            job.Errors.ShouldHaveSingleItem().Item.ShouldBe("archive");
            job.ProcessedFiles.ShouldBe(0);
            job.ExportPath.ShouldBeNull();
            job.Complete(completedAt: DateTimeOffset.UtcNow);
            job.PhaseEnum.ShouldBe(ExportPhase.CompletedWithErrors);
            Directory.Exists(directory).ShouldBeFalse();
            await writer.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default, default, default);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_MissingBeforeAndAfterBinding_RecordsExplicitSkipsAndCompletesTitleProgress()
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-archive-progress-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = Substitute.For<IExportRepository>();
            files.GetExportFilesAsync(Arg.Any<ExportScope>(), Arg.Any<CancellationToken>()).Returns([
                new ExportFileData(1, "Title", 1, "Platform", 7, 3, "disc.iso"),
                new ExportFileData(1, "Title", 1, "Platform", 9, 5, "missing.iso"),
                new ExportFileData(2, "Other", 1, "Platform", 8, 4, "manual.bin")
            ]);
            var catalog = Substitute.For<IRomContentCatalog>();
            var logical = Sha256.Parse(new string('a', 64));
            var artifact = Sha256.Parse(new string('b', 64));
            SetCatalogContent(catalog, RvzContent(7, 3, logical, artifact, 100, 50),
            OriginalContent(8, 4, logical, 100));
            var writer = Substitute.For<IExportArchiveWriter>();
            writer.WriteAsync(Arg.Any<Stream>(), Arg.Any<IReadOnlyList<ExportArchiveEntry>>(),
                Arg.Any<Func<ExportArchiveEntry, bool, Task>?>(), true, Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var entries = call.ArgAt<IReadOnlyList<ExportArchiveEntry>>(1);
                    entries.Count.ShouldBe(2);
                    entries.Single(entry => entry.RomId == 7).Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Rvz);
                    entries.Single(entry => entry.RomId == 8).Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Original);
                    foreach (var entry in entries)
                        await call.ArgAt<Func<ExportArchiveEntry, bool, Task>>(2)(entry, entry.RomId != 7);
                });
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(directory);
            var job = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow, format: RomDeliveryFormat.Rvz);
            job.Start("delivery", startedAt: DateTimeOffset.UtcNow);

            await new ExportJobExecutor(files, writer, options, Substitute.For<ILogger<ExportJobExecutor>>(), catalog, DeliveryPolicy, clock: TimeProvider.System, chdAdmission: new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null))
                .ExecuteAsync(job, new JobContext(null, _ => Task.CompletedTask, CancellationToken.None));

            job.TotalFiles.ShouldBe(3);
            job.ProcessedFiles.ShouldBe(3);
            job.SkippedFiles.ShouldBe(2);
            job.TotalTitles.ShouldBe(2);
            job.ProcessedTitles.ShouldBe(2);
            job.PhaseEnum.ShouldBe(ExportPhase.Storing);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_FailedOrCancelledWriter_DeletesOnlyPartialAttempt(bool cancelled)
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-archive-cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var cancellation = new CancellationTokenSource();
            var files = Substitute.For<IExportRepository>();
            files.GetExportFilesAsync(Arg.Any<ExportScope>(), Arg.Any<CancellationToken>())
                .Returns([new ExportFileData(1, "Title", 1, "Platform", 7, 3, "game.iso")]);
            var catalog = Substitute.For<IRomContentCatalog>();
            var hash = Sha256.Parse(new string('a', 64));
            SetCatalogContent(catalog, OriginalContent(7, 3, hash, 100));
            var writer = Substitute.For<IExportArchiveWriter>();
            writer.WriteAsync(Arg.Any<Stream>(), Arg.Any<IReadOnlyList<ExportArchiveEntry>>(),
                Arg.Any<Func<ExportArchiveEntry, bool, Task>?>(), true, cancellation.Token).Returns(async call =>
                {
                    await call.ArgAt<Stream>(0).WriteAsync(new byte[] { 1, 2, 3 });
                    if (cancelled)
                    {
                        cancellation.Cancel();
                        cancellation.Token.ThrowIfCancellationRequested();
                    }
                    throw new ResourceCapacityException("occupied");
                });
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(directory);
            var job = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow);
            job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
            var executor = new ExportJobExecutor(files, writer, options, Substitute.For<ILogger<ExportJobExecutor>>(), catalog, DeliveryPolicy, clock: TimeProvider.System, chdAdmission: new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));
            var context = new JobContext(null, _ => Task.CompletedTask, cancellation.Token);

            if (cancelled)
                await Should.ThrowAsync<OperationCanceledException>(() => executor.ExecuteAsync(job, context));
            else
                await Should.ThrowAsync<ResourceCapacityException>(() => executor.ExecuteAsync(job, context));

            Directory.GetFiles(Path.Combine(directory, "exports")).ShouldBeEmpty();
            job.ExportPath.ShouldBeNull();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(ExportPhase.Packaging)]
    [InlineData(ExportPhase.Storing)]
    public async Task ExecuteAsync_RedeliveredPackagingOrStoring_RebuildsAttemptAndResetsProgress(ExportPhase phase)
    {
        var directory = Path.Combine(Path.GetTempPath(), "romd-archive-restart-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = Substitute.For<IExportRepository>();
            files.GetExportFilesAsync(Arg.Any<ExportScope>(), Arg.Any<CancellationToken>())
                .Returns([new ExportFileData(1, "Title", 1, "Platform", 7, 3, "game.iso")]);
            var writer = Substitute.For<IExportArchiveWriter>();
            writer.WriteAsync(Arg.Any<Stream>(), Arg.Any<IReadOnlyList<ExportArchiveEntry>>(),
                Arg.Any<Func<ExportArchiveEntry, bool, Task>?>(), true, Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    await call.ArgAt<Stream>(0).WriteAsync(new byte[] { 1, 2, 3 });
                    await call.ArgAt<Func<ExportArchiveEntry, bool, Task>>(2)(
                        call.ArgAt<IReadOnlyList<ExportArchiveEntry>>(1).Single(), true);
                });
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(directory);
            var job = ExportJob.Rehydrate(Guid.NewGuid(), Guid.NewGuid(), "Library Export", null, phase,
                "delivery", ExportScopeKind.Library, 8, 13, 2, 1, 4, 3, 1,
                null, "old title", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false, null, null,
                RomDeliveryFormat.Rvz);
            job = ExportJobEntity.FromDomain(job).ToDomain();
            var catalog = Substitute.For<IRomContentCatalog>();
            var logical = Sha256.Parse(new string('a', 64));
            var artifact = Sha256.Parse(new string('b', 64));
            SetCatalogContent(catalog, RvzContent(7, 3, logical, artifact, 100, 50));
            var executor = new ExportJobExecutor(files, writer, options, Substitute.For<ILogger<ExportJobExecutor>>(), catalog, DeliveryPolicy, clock: TimeProvider.System, chdAdmission: new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));

            await executor.ExecuteAsync(job, new JobContext(null, _ => Task.CompletedTask, CancellationToken.None));

            job.PhaseEnum.ShouldBe(ExportPhase.Storing);
            job.TotalTitles.ShouldBe(1);
            job.ProcessedTitles.ShouldBe(1);
            job.TotalFiles.ShouldBe(1);
            job.ProcessedFiles.ShouldBe(1);
            job.SkippedFiles.ShouldBe(0);
            job.Format.ShouldBe(RomDeliveryFormat.Rvz);
            await files.Received(1).GetExportFilesAsync(new ExportScope.Library(8, 13), CancellationToken.None);
            await writer.Received(1).WriteAsync(Arg.Any<Stream>(),
                Arg.Is<IReadOnlyList<ExportArchiveEntry>>(entries => entries.Single().Path == "Platform/Title/game.rvz"
                    && entries.Single().Target!.Format == RomDeliveryFormat.Rvz && entries.Single().Target!.Sha256 == artifact),
                Arg.Any<Func<ExportArchiveEntry, bool, Task>?>(), true, CancellationToken.None);
            File.ReadAllBytes(job.ExportPath.ShouldNotBeNull()).ShouldBe(new byte[] { 1, 2, 3 });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
    private static ExportJob StoringJob(string path)
    {
        var job = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow, format: RomDeliveryFormat.Rvz);
        job.Start("prior", startedAt: DateTimeOffset.UtcNow);
        job.BeginPackaging();
        job.BeginStoring();
        job.SetExportPath(path);
        return ExportJobEntity.FromDomain(job).ToDomain();
    }

    private static IRomContentCatalog IdentityCatalog()
    {
        var catalog = Substitute.For<IRomContentCatalog>();
        var hash = Sha256.Parse(new string('a', 64));
        SetCatalogContent(catalog, OriginalContent(7, 3, hash, 100));
        return catalog;
    }
    [Fact]
    public async Task ExecuteAsync_AdmittedPs2Chd_UsesRealPlatformKeyAndExactBoundArtifact()
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-chd-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var content = SyntheticChdContent(7, 12);
            var files = Substitute.For<IExportRepository>();
            files.GetExportFilesAsync(Arg.Any<ExportScope>(), Arg.Any<CancellationToken>())
                .Returns([new ExportFileData(1, "Title", 41, "Display Name", 7, 12, "game.iso")]);
            var catalog = Substitute.For<IRomContentCatalog>();
            SetCatalogContent(catalog, content);
            var reference = Substitute.For<IReferenceCatalogService>();
            reference.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(new SystemKeys(new Dictionary<int, string> { [41] = "ps2" }));
            reference.ResolveSystemIdAsync("ps2", Arg.Any<CancellationToken>()).Returns((int?)41);
            var writer = Substitute.For<IExportArchiveWriter>();
            IReadOnlyList<ExportArchiveEntry>? written = null;
            writer.WriteAsync(Arg.Any<Stream>(), Arg.Any<IReadOnlyList<ExportArchiveEntry>>(),
                Arg.Any<Func<ExportArchiveEntry, bool, Task>?>(), true, Arg.Any<CancellationToken>()).Returns(async call =>
                {
                    written = call.ArgAt<IReadOnlyList<ExportArchiveEntry>>(1);
                    await call.ArgAt<Func<ExportArchiveEntry, bool, Task>>(2)(written.Single(), true);
                });
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(directory);
            var job = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow, format: RomDeliveryFormat.Chd);
            job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
            await new ExportJobExecutor(files, writer, options, Substitute.For<ILogger<ExportJobExecutor>>(), catalog, DeliveryPolicy, clock: TimeProvider.System, reference,
                new SyntheticAdmission(content.Artifacts![0].ChdEvidence![0].Decoder))
                .ExecuteAsync(job, new JobContext(null, _ => Task.CompletedTask, CancellationToken.None));

            var entry = written.ShouldNotBeNull().ShouldHaveSingleItem();
            entry.Path.ShouldBe("Display Name/Title/game.chd");
            entry.PlatformKeys.ShouldBe(["ps2"]);
            entry.FileId.ShouldBe(12);
            entry.Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Chd);
            entry.Target.Sha256.ShouldBe(content.ArtifactSha256);
            entry.Target.SizeBytes.ShouldBe(4096);
            entry.Target.ReadBinding.ShouldBe(content.Artifacts![0].ChdEvidence![0].Bind());
            job.ProcessedFiles.ShouldBe(1);
            job.SkippedFiles.ShouldBe(0);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
    private static RomContentDescriptor SyntheticChdContent(int romId = 7, int fileId = 12)
    {
        var identity = Identity(8192, Sha256.Parse(new string('a', 64)));
        var container = new RomContainerFacts.Chd(5, 8192, 4096, 2048, 2, [RomChdCodecs.Zlib, 0, 0, 0], false, identity.Sha1, Sha1.Parse(new string('c', 40)), [new(0x44564420, 1, [0])]);
        return Describe(romId, MeasuredArtifact(romId, fileId, identity, Sha256.Parse(new string('b', 64)), 4096, container,
            new RomDecoderIdentity.Native(new("linux-x64", Sha1.Parse(new string('f', 40)), 1)), true));
    }
    private static RomLogicalIdentity Identity(long bytes, Sha256 sha) => new(bytes, sha, Sha1.Parse(new string('a', 40)), Md5.Parse(new string('a', 32)), Crc32.Parse("aaaaaaaa"));
    private static RomContentArtifact MeasuredArtifact(int romId, int fileId, RomLogicalIdentity identity, Sha256 artifact, long bytes, RomContainerFacts container, RomDecoderIdentity decoder, bool preferred)
    {
        var proof = new RomRepresentationVerification(Guid.NewGuid(), artifact, bytes, decoder, "complete-output/v1", "export-fixture-verifier", DateTimeOffset.UnixEpoch.AddTicks(47),
            [new(RomOutputSelector.Whole.Instance, identity)], container, (container as RomContainerFacts.Chd)?.StoredRawSha1, (container as RomContainerFacts.Chd)?.StoredOverallSha1);
        var facts = new RomRepresentationFacts(fileId, fileId, artifact, bytes, container, RomReconstructionManifest.WholeFile.Instance,
            [new(fileId, romId, RomOutputSelector.Whole.Instance, identity)], [proof], []);
        return new(facts, fileId, preferred, container is RomContainerFacts.Identity ? identity.Md5 : Md5.Parse(new string('b', 32)),
            container is RomContainerFacts.Identity ? identity.Sha1 : Sha1.Parse(new string('b', 40)));
    }
    private static RomContentDescriptor Describe(int romId, params RomContentArtifact[] artifacts) => new(romId, artifacts.Single(artifact => artifact.IsPreferred).OutputId,
        artifacts[0].Output.Identity, "game.iso", artifacts, [], []);
    private static RomContentDescriptor OriginalContent(int romId, int fileId, Sha256 sha, long bytes) => Describe(romId,
        MeasuredArtifact(romId, fileId, Identity(bytes, sha), sha, bytes, RomContainerFacts.Identity.Instance, new RomDecoderIdentity.Managed(ConsumerMediaFixture.ManagedBuild), true));
    private static RomContentDescriptor RvzContent(int romId, int fileId, Sha256 logical, Sha256 artifact, long logicalBytes, long artifactBytes) => Describe(romId,
        MeasuredArtifact(romId, fileId, Identity(logicalBytes, logical), artifact, artifactBytes, new RomContainerFacts.Rvz(0x01000000, 1, 5, 19, 131072, false, logicalBytes),
            new RomDecoderIdentity.Managed(ConsumerMediaFixture.ManagedBuild), true));
    private static RomContentDescriptor MixedContent()
    {
        var logical = Sha256.Parse(new string('a', 64));
        var identity = Identity(4, logical);
        return Describe(7, MeasuredArtifact(7, 11, identity, logical, 4, RomContainerFacts.Identity.Instance, new RomDecoderIdentity.Managed(ConsumerMediaFixture.ManagedBuild), true),
            MeasuredArtifact(7, 12, identity, Sha256.Parse(new string('b', 64)), 2, new RomContainerFacts.Rvz(0x01000000, 1, 5, 19, 131072, false, 4),
                new RomDecoderIdentity.Managed(ConsumerMediaFixture.ManagedBuild), false));
    }

    private sealed class SyntheticAdmission(RomChdDecoderBuild decoder) : IRomChdDeliveryAdmission
    {
        private readonly ConsumerMediaAdmission _inner = new(ConsumerMediaFixture.ManagedBuild, decoder);
        public RomChdDeliveryAssessment? Assess(RomChdDeliveryEvidence evidence) => _inner.Assess(evidence);
        public RomManagedReadBinding? AssessManaged(RomContentArtifact artifact) => _inner.AssessManaged(artifact);
        public bool AdmitsCd(RomReconstruction.CdMember member) => _inner.AdmitsCd(member);
        public RomChdDecoderBuild? AssessCd(RomReconstruction.CdMember member) => _inner.AssessCd(member);
    }


    [Theory]
    [InlineData(RomDeliveryFormat.Original, "game.iso", 4, 11)]
    [InlineData(RomDeliveryFormat.Rvz, "game.rvz", 2, 12)]
    public async Task ExecuteAsync_UnregisteredPlatform_PreservesOriginalAndRvz(RomDeliveryFormat format,
        string filename, long size, int fileId)
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-unregistered-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = Substitute.For<IExportRepository>();
            files.GetExportFilesAsync(Arg.Any<ExportScope>(), Arg.Any<CancellationToken>()).Returns([
                new ExportFileData(1, "Title", 99, "Unregistered", 7, 11, "game.iso")]);
            var logical = Sha256.Parse(new string('a', 64));
            var artifact = Sha256.Parse(new string('b', 64));
            var catalog = Substitute.For<IRomContentCatalog>();
            SetCatalogContent(catalog, MixedContent());
            var reference = Substitute.For<IReferenceCatalogService>();
            reference.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(new SystemKeys(new Dictionary<int, string>()));
            reference.ResolveSystemIdAsync("ps2", Arg.Any<CancellationToken>()).Returns((int?)41);
            var writer = Substitute.For<IExportArchiveWriter>();
            IReadOnlyList<ExportArchiveEntry>? written = null;
            writer.WriteAsync(Arg.Any<Stream>(), Arg.Any<IReadOnlyList<ExportArchiveEntry>>(),
                Arg.Any<Func<ExportArchiveEntry, bool, Task>?>(), true, Arg.Any<CancellationToken>()).Returns(async call =>
                {
                    written = call.ArgAt<IReadOnlyList<ExportArchiveEntry>>(1);
                    await call.ArgAt<Func<ExportArchiveEntry, bool, Task>>(2)(written.Single(), true);
                });
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(directory);
            var export = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow, format: format);
            export.Start("delivery", startedAt: DateTimeOffset.UtcNow);
            var error = await Record.ExceptionAsync(() => new ExportJobExecutor(files, writer, options,
                Substitute.For<ILogger<ExportJobExecutor>>(), catalog, DeliveryPolicy, clock: TimeProvider.System, reference, chdAdmission: new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null)).ExecuteAsync(export,
                    new JobContext(null, _ => Task.CompletedTask, CancellationToken.None)));

            error.ShouldBeNull();
            var entry = written.ShouldNotBeNull().ShouldHaveSingleItem();
            entry.PlatformKeys.ShouldBe([null]);
            entry.Path.ShouldBe("Unregistered/Title/" + filename);
            var target = entry.Target.ShouldNotBeNull();
            target.Format.ShouldBe(format);
            target.FileId.ShouldBe(fileId);
            target.Filename.ShouldBe(filename);
            target.SizeBytes.ShouldBe(size);
            target.Sha256.ShouldBe(format == RomDeliveryFormat.Rvz ? artifact : logical);
            target.ContentSha256.ShouldBe(logical);
            target.ContentSizeBytes.ShouldBe(4);
            export.ProcessedFiles.ShouldBe(1);
            export.SkippedFiles.ShouldBe(0);
            await reference.DidNotReceiveWithAnyArgs().GetSystemKeysAsync(default);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    /// <summary>
    ///     An export member reached through a platform no media kind claims, or through platforms that disagree, resolves no
    ///     kind, yet its verified CHD is bound from evidence alone, under each title that holds it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ChdWithoutAUniqueKind_BindsTheVerifiedArtifact(bool alsoOnPs2)
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-kindless-chd-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = Substitute.For<IExportRepository>();
            ExportFileData[] exported = alsoOnPs2
                ? [new(1, "Title", 99, "Unregistered", 7, 12, "game.iso"), new(2, "Twin", 41, "PlayStation 2", 7, 12, "game.iso")]
                : [new(1, "Title", 99, "Unregistered", 7, 12, "game.iso")];
            files.GetExportFilesAsync(Arg.Any<ExportScope>(), Arg.Any<CancellationToken>()).Returns(exported);
            var content = SyntheticChdContent();
            var catalog = Substitute.For<IRomContentCatalog>();
            SetCatalogContent(catalog, content);
            var reference = Substitute.For<IReferenceCatalogService>();
            reference.ResolveSystemIdAsync("ps2", Arg.Any<CancellationToken>()).Returns((int?)41);
            var writer = Substitute.For<IExportArchiveWriter>();
            IReadOnlyList<ExportArchiveEntry>? written = null;
            writer.WriteAsync(Arg.Any<Stream>(), Arg.Any<IReadOnlyList<ExportArchiveEntry>>(),
                Arg.Any<Func<ExportArchiveEntry, bool, Task>?>(), true, Arg.Any<CancellationToken>()).Returns(call =>
                {
                    written = call.ArgAt<IReadOnlyList<ExportArchiveEntry>>(1);
                    return Task.CompletedTask;
                });
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(directory);
            var job = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow, format: RomDeliveryFormat.Chd);
            job.Start("delivery", startedAt: DateTimeOffset.UtcNow);

            await new ExportJobExecutor(files, writer, options, Substitute.For<ILogger<ExportJobExecutor>>(), catalog, DeliveryPolicy, clock: TimeProvider.System, reference,
                new SyntheticAdmission(content.Artifacts![0].ChdEvidence![0].Decoder))
                .ExecuteAsync(job, new JobContext(null, _ => Task.CompletedTask, CancellationToken.None));

            written.ShouldNotBeNull().Count.ShouldBe(exported.Length);
            written.ShouldAllBe(entry => entry.Target!.Format == RomDeliveryFormat.Chd &&
                entry.Target.ReadBinding == content.Artifacts![0].ChdEvidence![0].Bind());
            written[0].PlatformKeys.ShouldBe(alsoOnPs2 ? [null, "ps2"] : [null]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExecuteAsync_UnregisteredPlatform_ChdWithoutServingAuthority_SkipsWithoutBinding()
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-unregistered-chd-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = Substitute.For<IExportRepository>();
            files.GetExportFilesAsync(Arg.Any<ExportScope>(), Arg.Any<CancellationToken>()).Returns([
                new ExportFileData(1, "Title", 99, "Unregistered", 7, 12, "game.iso")]);
            var content = SyntheticChdContent();
            var catalog = Substitute.For<IRomContentCatalog>();
            SetCatalogContent(catalog, content);
            var reference = Substitute.For<IReferenceCatalogService>();
            reference.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(new SystemKeys(new Dictionary<int, string>()));
            reference.ResolveSystemIdAsync("ps2", Arg.Any<CancellationToken>()).Returns((int?)41);
            var storage = Substitute.For<IFileStorageService>();
            var reader = Substitute.For<IRomContentReader>();
            var actualWriter = new ExportArchiveWriter(storage, reader);
            var writer = Substitute.For<IExportArchiveWriter>();
            IReadOnlyList<ExportArchiveEntry>? written = null;
            writer.WriteAsync(Arg.Any<Stream>(), Arg.Any<IReadOnlyList<ExportArchiveEntry>>(),
                Arg.Any<Func<ExportArchiveEntry, bool, Task>?>(), true, Arg.Any<CancellationToken>()).Returns(async call =>
                {
                    written = call.ArgAt<IReadOnlyList<ExportArchiveEntry>>(1);
                    await actualWriter.WriteAsync(call.ArgAt<Stream>(0), written,
                        call.ArgAt<Func<ExportArchiveEntry, bool, Task>?>(2), true, call.ArgAt<CancellationToken>(4));
                });
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(directory);
            var export = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow, format: RomDeliveryFormat.Chd);
            export.Start("delivery", startedAt: DateTimeOffset.UtcNow);
            await new ExportJobExecutor(files, writer, options, Substitute.For<ILogger<ExportJobExecutor>>(), catalog, DeliveryPolicy, clock: TimeProvider.System, reference,
                Substitute.For<IRomChdDeliveryAdmission>()).ExecuteAsync(export,
                    new JobContext(null, _ => Task.CompletedTask, CancellationToken.None));

            written.ShouldNotBeNull().ShouldBeEmpty();
            export.ProcessedFiles.ShouldBe(1);
            export.SkippedFiles.ShouldBe(1);
            (export.ProcessedFiles - export.SkippedFiles).ShouldBe(0);
            storage.ReceivedCalls().ShouldBeEmpty();
            reader.ReceivedCalls().ShouldBeEmpty();
            await reference.DidNotReceiveWithAnyArgs().GetSystemKeysAsync(default);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void SetCatalogContent(IRomContentCatalog catalog, params RomContentDescriptor[] descriptors)
    {
        catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<int, RomContentDescriptor>)descriptors
                .Where(content => call.ArgAt<IReadOnlyCollection<int>>(0).Contains(content.RomId))
                .ToDictionary(content => content.RomId));
    }
}
