using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RomDecoderPoolTests
{
    private static readonly Sha256 Hash = Sha256.Parse(new string('a', 64));

    [Fact]
    public async Task DecodeAsync_AdmissionFull_RejectsAndDisposesSource_IdentityStillWorks()
    {
        var adapter = new FakeAdapter();
        await using var pool = new RomRepresentationDecoder(new RomMediaAdapterRegistry(adapter, new FakeAdapter(RomRepresentationEncoding.Identity)), new() { MaxConcurrent = 1, MaxIdle = 1 });
        var original = new Bytes();
        await using var lease = await pool.DecodeAsync(RomRepresentationEncoding.Rvz, original, artifact: Hash);
        var rejected = new Bytes();
        await Should.ThrowAsync<ResourceCapacityException>(() => pool.DecodeAsync(RomRepresentationEncoding.Rvz, rejected, artifact: Hash, admissionTimeout: TimeSpan.Zero));
        rejected.Disposed.ShouldBeTrue();
        await using var plain = await pool.DecodeAsync(RomRepresentationEncoding.Identity, new Bytes());
        plain.Length.ShouldBe(4);
        adapter.Opened.ShouldBe(1);
        await lease.DisposeAsync();
        var extra = new Bytes();
        await using var reuse = await pool.DecodeAsync(RomRepresentationEncoding.Rvz, extra, artifact: Hash);
        extra.Disposed.ShouldBeTrue();
        original.Disposed.ShouldBeFalse();
        adapter.Opened.ShouldBe(1);
        byte[] buffer = new byte[1];
        (await reuse.ReadAtAsync(2, buffer)).ShouldBe(1);
        buffer[0].ShouldBe((byte)3);
        await reuse.DisposeAsync();
        await pool.DisposeAsync();
        original.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task DecodeAsync_OpenFails_ReleasesAdmissionForNextAttempt()
    {
        var adapter = new FakeAdapter { Fail = true };
        await using var pool = new RomRepresentationDecoder(new RomMediaAdapterRegistry(adapter), new() { MaxConcurrent = 1, MaxIdle = 0 });
        var failed = new Bytes();
        await Should.ThrowAsync<InvalidDataException>(() => pool.DecodeAsync(RomRepresentationEncoding.Rvz, failed));
        failed.Disposed.ShouldBeTrue();
        adapter.Fail = false;
        await using var next = await pool.DecodeAsync(RomRepresentationEncoding.Rvz, new Bytes());
        next.Length.ShouldBe(4);
    }

    [Fact]
    public async Task DecodeAsync_ParallelPressure_BoundsOpensAndIdleImages()
    {
        var adapter = new FakeAdapter();
        await using var pool = new RomRepresentationDecoder(new RomMediaAdapterRegistry(adapter), new() { MaxConcurrent = 4, MaxIdle = 2 });
        var sources = Enumerable.Range(0, 4).Select(_ => new Bytes()).ToArray();
        var leases = await Task.WhenAll(sources.Select(source => pool.DecodeAsync(RomRepresentationEncoding.Rvz, source, artifact: Hash)));
        var rejected = Enumerable.Range(0, 16).Select(_ => new Bytes()).ToArray();
        await Task.WhenAll(rejected.Select(async source =>
            await Should.ThrowAsync<ResourceCapacityException>(() => pool.DecodeAsync(RomRepresentationEncoding.Rvz, source, artifact: Hash, admissionTimeout: TimeSpan.Zero))));
        adapter.Opened.ShouldBe(4);
        rejected.ShouldAllBe(source => source.Disposed);
        await Task.WhenAll(leases.Select(lease => lease.DisposeAsync().AsTask()));
        sources.Count(source => !source.Disposed).ShouldBe(2);
        await pool.DisposeAsync();
        sources.ShouldAllBe(source => source.Disposed);
    }

    [Fact]
    public async Task DecodeAsync_IdleHitSnapshotDisposeThrows_DiscardsImageAndReturnsCapacity()
    {
        var adapter = new FakeAdapter();
        await using var pool = new RomRepresentationDecoder(new RomMediaAdapterRegistry(adapter), new() { MaxConcurrent = 1, MaxIdle = 1 });
        var original = new Bytes();
        await (await pool.DecodeAsync(RomRepresentationEncoding.Rvz, original, artifact: Hash)).DisposeAsync();
        await Should.ThrowAsync<IOException>(() =>
            pool.DecodeAsync(RomRepresentationEncoding.Rvz, new Bytes { FailDispose = true }, artifact: Hash));
        await using var next = await pool.DecodeAsync(RomRepresentationEncoding.Rvz, new Bytes(), artifact: Hash);
        original.Disposed.ShouldBeTrue();
        adapter.Opened.ShouldBe(2);
    }

    [Fact]
    public async Task ReadCancellation_DiscardsImageAndReturnsCapacityOnDispose()
    {
        var adapter = new FakeAdapter();
        await using var pool = new RomRepresentationDecoder(new RomMediaAdapterRegistry(adapter), new() { MaxConcurrent = 1, MaxIdle = 1 });
        var source = new Bytes();
        await using var lease = await pool.DecodeAsync(RomRepresentationEncoding.Rvz, source, artifact: Hash);
        // Cancellation originates in the underlying reader after the pool has admitted the read.
        source.CancelRead = true;
        await Should.ThrowAsync<OperationCanceledException>(() => lease.ReadAtAsync(0, new byte[1]).AsTask());
        await lease.DisposeAsync();
        source.Disposed.ShouldBeTrue();
        await using var next = await pool.DecodeAsync(RomRepresentationEncoding.Rvz, new Bytes(), artifact: Hash);
        adapter.Opened.ShouldBe(2);
    }

    [Fact]
    public async Task DecodeAsync_InjectedHostCache_PassesSameBudgetedCacheToAdapter()
    {
        var cache = new MediaBlockCache(1 << 20);
        var adapter = new FakeAdapter();
        await using var pool = new RomRepresentationDecoder(new RomMediaAdapterRegistry(adapter),
            new() { CacheBytes = cache.BudgetBytes, MaxIdle = 0 }, cache);
        await using var decoded = await pool.DecodeAsync(RomRepresentationEncoding.Rvz, new Bytes());

        adapter.Cache.ShouldBeSameAs(cache);
        adapter.Cache.ShouldNotBeNull().BudgetBytes.ShouldBe(1 << 20);
        decoded.Length.ShouldBe(4);
    }

    [Fact]
    public void Constructor_InjectedHostCacheWithDifferentBudget_Refuses()
    {
        var cache = new MediaBlockCache(1 << 20);

        Should.Throw<InvalidOperationException>(() => new RomRepresentationDecoder(new RomMediaAdapterRegistry(),
            new() { CacheBytes = 2 << 20 }, cache));
    }

    [Fact]
    public async Task CanDecode_AnswersFromTheDecodersOwnRegistry()
    {
        await using var defaults = new RomRepresentationDecoder();
        await using var chdOnly = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new FakeAdapter(RomRepresentationEncoding.Chd)), new());

        defaults.CanDecode(RomRepresentationEncoding.Rvz).ShouldBeTrue();
        defaults.CanDecode(RomRepresentationEncoding.Identity).ShouldBeTrue();
        defaults.CanDecode(RomRepresentationEncoding.Chd).ShouldBeFalse();
        chdOnly.CanDecode(RomRepresentationEncoding.Chd).ShouldBeTrue();
        chdOnly.CanDecode(RomRepresentationEncoding.Rvz).ShouldBeFalse();
    }

    private sealed class FakeAdapter(RomRepresentationEncoding encoding = RomRepresentationEncoding.Rvz) : IRomMediaAdapter
    {
        public int Opened;
        public bool Fail;
        public MediaBlockCache? Cache;
        public RomRepresentationEncoding Encoding => encoding;
        public bool ReusesIdleImages => true;
        public async ValueTask<IRandomAccessContent> OpenAsync(RomArtifactSource artifact, RomMediaOpenContext context,
            MediaBlockCache cache, CancellationToken ct)
        {
            var source = await artifact.OpenRandomAccessAsync(ct);
            Interlocked.Increment(ref Opened);
            Cache = cache;
            if (Fail)
            { await source.DisposeAsync(); throw new InvalidDataException("bad media"); }
            return source;
        }
    }

    [Fact]
    public async Task DecodeAsync_BoundedAdmissionWait_ResumesWhenLeaseReturns()
    {
        await using var pool = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new FakeAdapter()), new() { MaxConcurrent = 1, MaxIdle = 0 });
        await using var occupied = await pool.DecodeAsync(RomRepresentationEncoding.Rvz, new Bytes());
        var source = new Bytes();
        var waiting = pool.DecodeAsync(RomRepresentationEncoding.Rvz, source, admissionTimeout: TimeSpan.FromSeconds(5));
        waiting.IsCompleted.ShouldBeFalse();
        source.Disposed.ShouldBeFalse();
        await occupied.DisposeAsync();
        await using var resumed = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        resumed.Length.ShouldBe(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodeAsync_WaitExpiresOrCancels_DisposesSourceAndDoesNotConsumeSlot(bool cancel)
    {
        await using var pool = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new FakeAdapter()), new() { MaxConcurrent = 1, MaxIdle = 0 });
        await using var occupied = await pool.DecodeAsync(RomRepresentationEncoding.Rvz, new Bytes());
        using var cancellation = new CancellationTokenSource();
        var source = new Bytes();
        var waiting = pool.DecodeAsync(RomRepresentationEncoding.Rvz, source, cancellation.Token,
            admissionTimeout: cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(25));
        if (cancel)
        {
            await cancellation.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => waiting);
        }
        else
            await Should.ThrowAsync<ResourceCapacityException>(() => waiting);
        source.Disposed.ShouldBeTrue();
        await occupied.DisposeAsync();
        await using var next = await pool.DecodeAsync(RomRepresentationEncoding.Rvz, new Bytes());
        next.Length.ShouldBe(4);
    }
    private sealed class Bytes : IRandomAccessContent
    {
        public bool Disposed;
        public bool CancelRead;
        public bool FailDispose;
        public long Length => 4;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> bytes, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (CancelRead)
                throw new OperationCanceledException();
            ObjectDisposedException.ThrowIf(Disposed, this);
            int n = (int)Math.Min(bytes.Length, 4 - offset);
            for (int i = 0; i < n; i++)
                bytes.Span[i] = (byte)(offset + i + 1);
            return ValueTask.FromResult(n);
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return FailDispose ? throw new IOException("snapshot close failed") : ValueTask.CompletedTask;
        }
    }
}
