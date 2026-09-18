using Romd.Infrastructure.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class JobWorkspaceExecutionLockTests
{
    [Fact]
    public async Task AcquireExecutionLockAsync_ExistingOwner_BlocksUntilReleaseAndHonorsShutdown()
    {
        string root = Directory.CreateTempSubdirectory("romd-execution-lock-").FullName;
        try
        {
            var jobId = Guid.NewGuid();
            await using var first = await JobWorkspace.AcquireExecutionLockAsync(root, jobId, default);
            using var shutdown = new CancellationTokenSource();
            var blocked = JobWorkspace.AcquireExecutionLockAsync(root, jobId, shutdown.Token);
            if (blocked.IsFaulted) await blocked;
            blocked.IsCompleted.ShouldBeFalse();
            shutdown.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => blocked);
            await first.DisposeAsync();
            await using var successor = await JobWorkspace.AcquireExecutionLockAsync(root, jobId, default);
            successor.CanWrite.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
