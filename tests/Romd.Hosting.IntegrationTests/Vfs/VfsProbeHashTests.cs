using System.Security.Cryptography;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class VfsProbeHashTests
{
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(10001)]
    public async Task ReadAsync_HashesEveryByteThroughEofWithBoundedLargeReads(int maximumRead)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        byte[] bytes = new byte[(3 << 20) + 17];
        new Random(741).NextBytes(bytes);
        await using var source = new CountingStream(bytes, maximumRead);
        long progress = 0;
        var digest = await VfsProbeHash.ReadAsync(source, count => progress = count, deadline.Token);
        digest.ShouldBe(SHA256.HashData(bytes));
        progress.ShouldBe(bytes.Length);
        source.Position.ShouldBe(bytes.Length);
        source.SawEof.ShouldBeTrue();
        source.MaximumRequested.ShouldBeInRange(64 << 10, 2 << 20);
    }

    [Fact]
    public async Task ReadAsync_CancellationJoinsTheOwnedRead()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var source = new BlockedStream();
        var hashing = VfsProbeHash.ReadAsync(source, _ => { }, canceled.Token);
        await source.Entered.Task.WaitAsync(deadline.Token);
        await canceled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await hashing);
        source.Joined.ShouldBeTrue();
    }

    private sealed class CountingStream(byte[] bytes, int maximumRead) : MemoryStream(bytes, writable: false)
    {
        public int MaximumRequested { get; private set; }
        public bool SawEof { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            MaximumRequested = Math.Max(MaximumRequested, buffer.Length);
            int count = await base.ReadAsync(buffer[..Math.Min(maximumRead, buffer.Length)], ct);
            SawEof |= count == 0;
            return count;
        }
    }

    private sealed class BlockedStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Joined { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            try
            { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { Joined = true; }
            return 0;
        }
    }
}
