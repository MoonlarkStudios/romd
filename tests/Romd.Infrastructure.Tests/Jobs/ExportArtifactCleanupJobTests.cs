using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class ExportArtifactCleanupJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_ExpiredArtifact_DeletesBeforeTransactionAndCommitsWithCapturedCutoff()
    {
        using var directory = new TemporaryDirectory();
        string path = await directory.CreateArtifactAsync();
        var artifact = new ExpiredExportArtifact(Guid.NewGuid(), path);
        var store = Substitute.For<IExportArtifactCleanupStore>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        var clock = new ManualTimeProvider(Now);
        var cutoff = Now.AddHours(-24);
        var clearedAt = Now.AddMinutes(5);
        bool transactionOpened = false;
        bool pointerStaged = false;
        store.GetExpiredArtifactsAsync(cutoff, CancellationToken.None).Returns(new[] { artifact });
        unitOfWork.BeginTransactionAsync(CancellationToken.None).Returns(_ =>
        {
            File.Exists(path).ShouldBeFalse();
            transactionOpened = true;
            clock.SetUtcNow(clearedAt);
            return Task.FromResult(transaction);
        });
        store.ClearExpiredArtifactStagedAsync(artifact.JobId, path, cutoff, clearedAt, CancellationToken.None)
            .Returns(_ =>
            {
                transactionOpened.ShouldBeTrue();
                File.Exists(path).ShouldBeFalse();
                pointerStaged = true;
                return Task.FromResult(true);
            });
        transaction.CommitAsync(CancellationToken.None).Returns(_ =>
        {
            pointerStaged.ShouldBeTrue();
            return Task.CompletedTask;
        });
        var job = NewJob(store, unitOfWork, clock);

        await job.ExecuteAsync(CancellationToken.None);

        File.Exists(path).ShouldBeFalse();
        await store.Received(1).ClearExpiredArtifactStagedAsync(
            artifact.JobId, path, cutoff, clearedAt, CancellationToken.None);
        await transaction.Received(1).CommitAsync(CancellationToken.None);
        await transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_MissingArtifactAndChangedPointer_CompletesConditionalCleanupWithoutError()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.DirectoryPath, "missing.zip");
        var artifact = new ExpiredExportArtifact(Guid.NewGuid(), path);
        var store = Substitute.For<IExportArtifactCleanupStore>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        var cutoff = Now.AddHours(-24);
        store.GetExpiredArtifactsAsync(cutoff, CancellationToken.None).Returns(new[] { artifact });
        unitOfWork.BeginTransactionAsync(CancellationToken.None).Returns(transaction);
        store.ClearExpiredArtifactStagedAsync(artifact.JobId, path, cutoff, Now, CancellationToken.None)
            .Returns(false);
        var job = NewJob(store, unitOfWork, new ManualTimeProvider(Now));

        await Should.NotThrowAsync(() => job.ExecuteAsync(CancellationToken.None));

        await store.Received(1).ClearExpiredArtifactStagedAsync(
            artifact.JobId, path, cutoff, Now, CancellationToken.None);
        await transaction.Received(1).CommitAsync(CancellationToken.None);
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_CancelledAfterDiscovery_PreservesFileWithoutOpeningTransaction()
    {
        using var directory = new TemporaryDirectory();
        string path = await directory.CreateArtifactAsync();
        var artifact = new ExpiredExportArtifact(Guid.NewGuid(), path);
        var store = Substitute.For<IExportArtifactCleanupStore>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        using var cancellation = new CancellationTokenSource();
        store.GetExpiredArtifactsAsync(Now.AddHours(-24), cancellation.Token).Returns(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult<IReadOnlyList<ExpiredExportArtifact>>([artifact]);
        });
        var job = NewJob(store, unitOfWork, new ManualTimeProvider(Now));

        var error = await Should.ThrowAsync<OperationCanceledException>(() => job.ExecuteAsync(cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        (await File.ReadAllTextAsync(path)).ShouldBe("export");
        await unitOfWork.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
        await store.DidNotReceiveWithAnyArgs().ClearExpiredArtifactStagedAsync(default, default!, default, default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DatabaseBoundaryFailsAfterDeletion_RetryWithMissingFileCommits(bool failCommit)
    {
        using var directory = new TemporaryDirectory();
        string path = await directory.CreateArtifactAsync();
        var artifact = new ExpiredExportArtifact(Guid.NewGuid(), path);
        var store = Substitute.For<IExportArtifactCleanupStore>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var firstTransaction = Substitute.For<ITransaction>();
        var retryTransaction = Substitute.For<ITransaction>();
        var failure = new IOException("Export cleanup persistence unavailable.");
        var cutoff = Now.AddHours(-24);
        int transactions = 0;
        int pointerAttempts = 0;
        store.GetExpiredArtifactsAsync(cutoff, CancellationToken.None).Returns(new[] { artifact });
        unitOfWork.BeginTransactionAsync(CancellationToken.None).Returns(_ =>
        {
            File.Exists(path).ShouldBeFalse();
            return Task.FromResult(++transactions == 1 ? firstTransaction : retryTransaction);
        });
        store.ClearExpiredArtifactStagedAsync(artifact.JobId, path, cutoff, Now, CancellationToken.None)
            .Returns(_ => ++pointerAttempts == 1 && !failCommit
                ? Task.FromException<bool>(failure)
                : Task.FromResult(true));
        if (failCommit)
            firstTransaction.CommitAsync(CancellationToken.None).Returns(Task.FromException(failure));
        var job = NewJob(store, unitOfWork, new ManualTimeProvider(Now));

        (await Should.ThrowAsync<IOException>(() => job.ExecuteAsync(CancellationToken.None)))
            .ShouldBeSameAs(failure);

        File.Exists(path).ShouldBeFalse();
        await firstTransaction.Received(1).DisposeAsync();
        await firstTransaction.Received(failCommit ? 1 : 0).CommitAsync(CancellationToken.None);

        await job.ExecuteAsync(CancellationToken.None);

        await store.Received(2).ClearExpiredArtifactStagedAsync(
            artifact.JobId, path, cutoff, Now, CancellationToken.None);
        await retryTransaction.Received(1).CommitAsync(CancellationToken.None);
        await retryTransaction.Received(1).DisposeAsync();
        transactions.ShouldBe(2);
        File.Exists(path).ShouldBeFalse();
    }

    private static ExportArtifactCleanupJob NewJob(
        IExportArtifactCleanupStore store,
        IUnitOfWork unitOfWork,
        TimeProvider clock) => new(store, unitOfWork, clock, NullLogger<ExportArtifactCleanupJob>.Instance);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"romd-export-cleanup-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(DirectoryPath);

        public async Task<string> CreateArtifactAsync()
        {
            string path = Path.Combine(DirectoryPath, "export.zip");
            await File.WriteAllTextAsync(path, "export");
            return path;
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
