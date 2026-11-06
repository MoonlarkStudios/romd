using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class WorkspacePreparationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-prepared-input-").FullName;
    private readonly IRomdOptions _options = Substitute.For<IRomdOptions>();

    public WorkspacePreparationTests() => _options.DataDirectory.Returns(_root);

    [Fact]
    public async Task AcquireAsync_UsesWorkerLockAndCancelledWaiterCannotReleaseItsOwner()
    {
        var id = Guid.NewGuid();
        using var worker = JobWorkspace.TryAcquireExecutionLock(_root, id).ShouldNotBeNull();
        using var cancellation = new CancellationTokenSource();
        var pending = new JobWorkspaceExclusion(_options).AcquireAsync(id, cancellation.Token);
        pending.IsCompleted.ShouldBeFalse();

        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        JobWorkspace.TryAcquireExecutionLock(_root, id).ShouldBeNull();
        worker.Dispose();
        await using var accepted = await new JobWorkspaceExclusion(_options).AcquireAsync(id);
        JobWorkspace.TryAcquireExecutionLock(_root, id).ShouldBeNull();
        File.Exists(Path.Combine(_root, "temp", "job-locks", $"{id:N}.lock")).ShouldBeTrue();
    }

    [Fact]
    public async Task PrepareUploadAsync_ReleasingLeaseRetainsCompletedInputAndCallerStream()
    {
        var id = Guid.NewGuid();
        var lease = await new JobWorkspaceExclusion(_options).AcquireAsync(id);
        using var source = new MemoryStream([1, 2, 3]);

        var result = await new UploadWorkspacePreparation(_options).PrepareAsync(new(id, "pack.zip"), source, lease);

        result.Value.JobId.ShouldBe(id);
        source.CanRead.ShouldBeTrue();
        (await File.ReadAllBytesAsync(InputPath(id, "pack.zip"))).ShouldBe(new byte[] { 1, 2, 3 });
        await lease.DisposeAsync();
        File.Exists(InputPath(id, "pack.zip")).ShouldBeTrue();
        using var successor = JobWorkspace.TryAcquireExecutionLock(_root, id).ShouldNotBeNull();
    }

    [Fact]
    public async Task DiscardAsync_RequiresStillHeldLeaseAndNeverDeletesAnotherWorkspace()
    {
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(InputPath(other, "other.zip"))!);
        await File.WriteAllTextAsync(InputPath(other, "other.zip"), "other owner");
        await using var lease = await new JobWorkspaceExclusion(_options).AcquireAsync(id);
        using var source = new MemoryStream([4, 5]);
        var result = await new UploadWorkspacePreparation(_options).PrepareAsync(new(id, "pack.zip"), source, lease);

        await result.Value.DiscardAsync();
        await result.Value.DiscardAsync();

        Directory.Exists(Path.GetDirectoryName(InputPath(id, "pack.zip"))).ShouldBeFalse();
        (await File.ReadAllTextAsync(InputPath(other, "other.zip"))).ShouldBe("other owner");
    }

    [Fact]
    public async Task DiscardAsync_ReleasedLeaseCannotDeleteInputsNowOwnedBySuccessor()
    {
        var id = Guid.NewGuid();
        var lease = await new JobWorkspaceExclusion(_options).AcquireAsync(id);
        using var source = new MemoryStream([7]);
        var result = await new UploadWorkspacePreparation(_options).PrepareAsync(new(id, "pack.zip"), source, lease);
        await lease.DisposeAsync();
        using var successor = JobWorkspace.TryAcquireExecutionLock(_root, id).ShouldNotBeNull();

        await Should.ThrowAsync<ObjectDisposedException>(() => result.Value.DiscardAsync());

        (await File.ReadAllBytesAsync(InputPath(id, "pack.zip"))).ShouldBe(new byte[] { 7 });
    }

    [Fact]
    public async Task PrepareUploadAsync_LeaseForAnotherJobRejectsWithoutReadingOrWriting()
    {
        var id = Guid.NewGuid();
        await using var lease = await new JobWorkspaceExclusion(_options).AcquireAsync(Guid.NewGuid());
        using var source = new FailingStream();

        await Should.ThrowAsync<ArgumentException>(() =>
            new UploadWorkspacePreparation(_options).PrepareAsync(new(id, "pack.zip"), source, lease));

        Directory.Exists(Path.GetDirectoryName(InputPath(id, "pack.zip"))).ShouldBeFalse();
    }

    [Fact]
    public async Task PrepareUploadAsync_SameLeaseCannotOverwriteCompletedPreparation()
    {
        var id = Guid.NewGuid();
        await using var lease = await new JobWorkspaceExclusion(_options).AcquireAsync(id);
        using var first = new MemoryStream([8]);
        var preparation = new UploadWorkspacePreparation(_options);
        (await preparation.PrepareAsync(new(id, "pack.zip"), first, lease)).IsError.ShouldBeFalse();
        using var second = new FailingStream();

        await Should.ThrowAsync<InvalidOperationException>(() => preparation.PrepareAsync(new(id, "pack.zip"), second, lease));

        (await File.ReadAllBytesAsync(InputPath(id, "pack.zip"))).ShouldBe(new byte[] { 8 });
    }

    [Fact]
    public async Task PrepareUploadAsync_CopyFaultRemovesPartialWorkspaceAndPreservesCause()
    {
        var id = Guid.NewGuid();
        await using var lease = await new JobWorkspaceExclusion(_options).AcquireAsync(id);
        using var source = new FailingStream();

        var error = await Should.ThrowAsync<IOException>(() =>
            new UploadWorkspacePreparation(_options).PrepareAsync(new(id, "pack.zip"), source, lease));

        error.Message.ShouldBe("source fault");
        Directory.Exists(Path.GetDirectoryName(InputPath(id, "pack.zip"))).ShouldBeFalse();
    }

    [Fact]
    public async Task PrepareUploadAsync_SeekableOverReserveReturnsExistingConflictBeforeCopy()
    {
        var id = Guid.NewGuid();
        await using var lease = await new JobWorkspaceExclusion(_options).AcquireAsync(id);
        using var source = new OversizedStream();

        var result = await new UploadWorkspacePreparation(_options).PrepareAsync(new(id, "pack.zip"), source, lease);

        result.FirstError.ShouldBe(UploadErrors.InsufficientStorage());
        result.FirstError.Type.ShouldBe(ErrorType.Conflict);
        Directory.Exists(Path.GetDirectoryName(InputPath(id, "pack.zip"))).ShouldBeFalse();
    }

    [Fact]
    public async Task PrepareUploadAsync_NonseekableStreamDoesNotReadUnsupportedLength()
    {
        var id = Guid.NewGuid();
        await using var lease = await new JobWorkspaceExclusion(_options).AcquireAsync(id);
        using var source = new NonseekableStream([9, 10]);

        var result = await new UploadWorkspacePreparation(_options).PrepareAsync(new(id, "pack.zip"), source, lease);

        result.IsError.ShouldBeFalse();
        (await File.ReadAllBytesAsync(InputPath(id, "pack.zip"))).ShouldBe(new byte[] { 9, 10 });
    }

    [Fact]
    public async Task PrepareReplacementAsync_CopyFaultRetainsPartialBytesWithoutInventingAcceptance()
    {
        var id = Guid.NewGuid();
        await using var lease = await new JobWorkspaceExclusion(_options).AcquireAsync(id);
        using var source = new FailingStream();

        await Should.ThrowAsync<IOException>(() =>
            new ReplaceDatWorkspacePreparation(_options).PrepareAsync(new(id, "catalog.dat"), source, lease));

        File.Exists(InputPath(id, "catalog.dat")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape.dat")]
    [InlineData("folder\\escape.dat")]
    public async Task PrepareReplacementAsync_UnsafeFilenameRejectsWithoutReading(string name)
    {
        var id = Guid.NewGuid();
        await using var lease = await new JobWorkspaceExclusion(_options).AcquireAsync(id);
        using var source = new FailingStream();

        await Should.ThrowAsync<ArgumentException>(() =>
            new ReplaceDatWorkspacePreparation(_options).PrepareAsync(new(id, name), source, lease));

        Directory.Exists(Path.GetDirectoryName(InputPath(id, "catalog.dat"))).ShouldBeFalse();
    }

    private string InputPath(Guid jobId, string file) => Path.Combine(_root, "temp", "jobs", $"{jobId:N}", file);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FailingStream : MemoryStream
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct) =>
            throw new IOException("source fault");
    }

    private sealed class OversizedStream : MemoryStream
    {
        public override long Length => long.MaxValue / 2;
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct) =>
            throw new InvalidOperationException("Rejected bytes must not be copied.");
    }

    private sealed class NonseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }
}
