using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.IngestRom;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Import;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed partial class UploadCapacityRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JournalBatch_FinalFlushFailsAfterWriting_DoesNotReportProgressCompleteOrCleanSources(bool cancelled)
    {
        var job = await SeedAsync();
        string source = Directory.CreateDirectory(Path.Combine(_root, "move-sources")).FullName;
        _options.AllowedImportPaths.Returns([source]);
        var entries = new List<ImportManifestEntry>();
        foreach (string name in new[] { "a.rom", "z.rom" })
        {
            string path = Path.Combine(source, name);
            File.Copy(Path.Combine(Workspace(job), name), path);
            var file = new FileInfo(path);
            entries.Add(new(name, name, file.Length, file.LastWriteTimeUtc));
        }
        job.SetImportSource(source, move: true);
        for (int count = 0; count < 3; count++)
            job.RecordDeferral("Earlier pressure", _clock.GetUtcNow());
        await using (var db = _database.CreateContext())
            await new UploadJobRepository(db, _clock).CommitCheckpointAsync(db, job);
        string manifestPath = ImportManifest.PathFor(_root, job.Id);
        await ImportManifest.WriteAsync(manifestPath,
            new ImportManifest(ImportManifest.CurrentVersion, source, ".", true, entries), default);
        var cleanup = Substitute.For<IJobCompletionCleanup<UploadJob>>();
        cleanup.RunAfterCompletionPersistedAsync(Arg.Any<UploadJob>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await using var read = _database.CreateContext();
            (await read.Jobs.SingleAsync()).Phase.ShouldBe("Completed");
            (await read.JobItems.CountAsync()).ShouldBe(2);
            await new ImportSourceCleanup(_options, new PathValidator(_options), new JobItemRepository(read),
                NullLogger<ImportSourceCleanup>.Instance).RunCompletedAsync(call.Arg<UploadJob>(), call.Arg<CancellationToken>());
        });
        using var shutdown = new CancellationTokenSource();
        var failure = cancelled ? (Exception)new OperationCanceledException("Injected final journal cancellation.", shutdown.Token)
            : new IOException("Injected final journal failure.");
        await using (var host = Host(configure: services =>
        {
            services.AddSingleton(cleanup);
            services.AddScoped<IUnitOfWork>(provider => new JournalBoundaryProbe(
                new EfUnitOfWork(provider.GetRequiredService<RomdDbContext>()), async () =>
                {
                    var writer = provider.GetRequiredService<RomdDbContext>();
                    (await writer.JobItems.CountAsync()).ShouldBe(2);
                    await using var observer = _database.CreateContext();
                    (await observer.JobItems.CountAsync()).ShouldBe(0);
                    if (cancelled)
                        shutdown.Cancel();
                    throw failure;
                }));
        }))
        {
            if (cancelled)
            {
                OperationCanceledException? observed = null;
                try
                {
                    await Runner(host).RunAsync(job.Id, "failed-journal", shutdown.Token);
                }
                catch (OperationCanceledException error)
                {
                    observed = error;
                }
                observed.ShouldBeSameAs(failure);
                observed.ShouldNotBeNull().CancellationToken.ShouldBe(shutdown.Token);
            }
            else
                (await Should.ThrowAsync<IOException>(() => Runner(host).RunAsync(job.Id, "failed-journal", default)))
                    .ShouldBeSameAs(failure);
        }
        cleanup.ReceivedCalls().ShouldBeEmpty();
        File.Exists(manifestPath).ShouldBeTrue();
        Directory.Exists(Workspace(job)).ShouldBeTrue();
        entries.ShouldAllBe(entry => File.Exists(Path.Combine(source, entry.SourceRelativePath)));
        await using (var verify = _database.CreateContext())
        {
            var saved = await verify.Jobs.SingleAsync();
            saved.Phase.ShouldBe("IngestingRoms");
            saved.CompletedAt.ShouldBeNull();
            saved.DeferralCount.ShouldBe(3);
            (await verify.JobItems.CountAsync()).ShouldBe(0);
        }
        _clock.Advance(new JobExecutionClaimOptions().LeaseDuration + TimeSpan.FromSeconds(1));
        await using (var retry = Host(configure: services => services.AddSingleton(cleanup)))
            await Runner(retry).RunAsync(job.Id, "recovered-journal", default);
        await cleanup.Received(1).RunAfterCompletionPersistedAsync(Arg.Any<UploadJob>(), CancellationToken.None);
        File.Exists(manifestPath).ShouldBeFalse();
        // Rejected files remain at the source even after the successful cleanup pass.
        entries.ShouldAllBe(entry => File.Exists(Path.Combine(source, entry.SourceRelativePath)));
    }

    [Fact]
    public async Task JournalBatch_LostCommitAcknowledgement_RequeuesOriginalIdsAndReportsOnlyDurableProgress()
    {
        var job = await SeedAsync();
        _options.MaxUploadBytes.Returns(1024);
        for (int number = 0; number < 48; number++)
            await File.WriteAllBytesAsync(Path.Combine(Workspace(job), $"extra-{number:D2}.rom"), [3]);
        for (int count = 0; count < 3; count++)
            job.RecordDeferral("Earlier pressure", _clock.GetUtcNow());
        await using (var db = _database.CreateContext())
            await new UploadJobRepository(db, _clock).CommitCheckpointAsync(db, job);
        bool interrupted = false;
        Guid[] firstIds = [];
        int commits = 0;
        await using var host = Host(configure: services => services.AddScoped<IUnitOfWork>(provider =>
            new JournalBoundaryProbe(new EfUnitOfWork(provider.GetRequiredService<RomdDbContext>()), null, async () =>
            {
                commits++;
                if (interrupted)
                    return;
                interrupted = true;
                await using var observer = _database.CreateContext();
                firstIds = await observer.JobItems.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync();
                firstIds.Length.ShouldBeGreaterThan(0);
                (await observer.Jobs.SingleAsync()).DeferralCount.ShouldBe(3);
                throw new IOException("Injected lost journal commit acknowledgement.");
            })));

        await Runner(host).RunAsync(job.Id, "lost-journal-ack", default);

        interrupted.ShouldBeTrue();
        commits.ShouldBeGreaterThan(1);
        await using var verify = _database.CreateContext();
        var allIds = await verify.JobItems.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync();
        allIds.Length.ShouldBe(50);
        firstIds.ShouldAllBe(id => allIds.Contains(id));
        (await verify.JobItems.Select(item => item.FileName).Distinct().CountAsync()).ShouldBe(50);
        var saved = await verify.Jobs.SingleAsync();
        saved.Phase.ShouldBe("IngestingRoms");
        saved.NextAttemptAtUtc.ShouldNotBeNull();
        saved.DeferralCount.ShouldBe(0);
        Directory.Exists(Workspace(job)).ShouldBeTrue();
    }

    private sealed class JournalBoundaryProbe(IUnitOfWork inner, Func<Task>? afterFlush = null, Func<Task>? afterCommit = null) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(await inner.BeginTransactionAsync(cancellationToken), inner, afterFlush, afterCommit);
        private sealed class Transaction(ITransaction inner, IUnitOfWork work, Func<Task>? afterFlush, Func<Task>? afterCommit) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await work.FlushAsync(cancellationToken);
                if (afterFlush is not null)
                    await afterFlush();
                await inner.CommitAsync(cancellationToken);
                if (afterCommit is not null)
                    await afterCommit();
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
