using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Romd.Domain.Hashing;
using Romd.Storage.FileSystem;
using Shouldly;
using Xunit;

namespace Romd.Storage.Tests.FileSystem;

public sealed class SynchronousRawContentTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "romd-sync-raw", Guid.NewGuid().ToString("N"));
    private FileSystemContentStore _store = null!;

    public Task InitializeAsync()
    {
        _store = NewStore(ContentWritePolicy.Raw);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private FileSystemContentStore NewStore(ContentWritePolicy policy) => new(Options.Create(
        new ContentStoreOptions { RootPath = _root, WritePolicy = policy }));

    [Fact]
    public async Task ReadAt_RangesAndEof_UseIndependentPositions()
    {
        byte[] bytes = new byte[100_003];
        new Random(321).NextBytes(bytes);
        var stored = await _store.StoreAsync(new MemoryStream(bytes));
        using var first = _store.OpenSynchronousRaw(stored.Key)!;
        using var second = _store.OpenSynchronousRaw(stored.Key)!;
        first.Length.ShouldBe(bytes.LongLength);
        foreach (long offset in new long[] { 0, 1, 65_535, 100_000, 100_003, long.MaxValue })
        {
            byte[] output = new byte[31];
            int count = first.ReadAt(offset, output);
            int expected = offset >= bytes.LongLength ? 0 : (int)Math.Min(output.Length, bytes.LongLength - offset);
            count.ShouldBe(expected);
            output[..count].ShouldBe(bytes.AsSpan((int)Math.Min(offset, bytes.LongLength), count).ToArray());
            byte[] start = new byte[17];
            second.ReadAt(0, start).ShouldBe(start.Length);
            start.ShouldBe(bytes[..start.Length]);
        }
        first.ReadAt(1, Span<byte>.Empty).ShouldBe(0);
        Should.Throw<ArgumentOutOfRangeException>(() => first.ReadAt(-1, new byte[1]));
    }

    [Fact]
    public async Task ReadAt_ConcurrentReaders_DoNotShareACursor()
    {
        byte[] bytes = new byte[128 * 1024];
        new Random(322).NextBytes(bytes);
        var stored = await _store.StoreAsync(new MemoryStream(bytes));
        using var reader = _store.OpenSynchronousRaw(stored.Key)!;
        using var barrier = new Barrier(8);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Factory.StartNew(() =>
        {
            byte[] output = new byte[2_048];
            for (int iteration = 0; iteration < 64; iteration++)
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
                int offset = (index * 101 + iteration * 2_049) % (bytes.Length - output.Length);
                reader.ReadAt(offset, output).ShouldBe(output.Length);
                output.ShouldBe(bytes.AsSpan(offset, output.Length).ToArray());
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)));
    }

    [Fact]
    public async Task ReadAt_WarmedRawReads_DoNotAllocateOnTheCallingThread()
    {
        byte[] bytes = new byte[4_096];
        new Random(323).NextBytes(bytes);
        var stored = await _store.StoreAsync(new MemoryStream(bytes));
        using var reader = _store.OpenSynchronousRaw(stored.Key)!;
        byte[] buffer = new byte[128];
        for (int index = 0; index < 1_000; index++)
            reader.ReadAt(index % 3_000, buffer);
        // One-time runtime transitions (such as tiering under load) may allocate once; a steady-state
        // allocation recurs in every window, so at least one window must allocate nothing.
        bool steady = false;
        for (int window = 0; window < 5 && !steady; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            int count = 0;
            for (int index = 0; index < 10_000; index++)
                count += reader.ReadAt(index % 3_000, buffer);
            steady = GC.GetAllocatedBytesForCurrentThread() == before;
            count.ShouldBe(10_000 * buffer.Length);
        }
        steady.ShouldBeTrue("Raw reads allocated in every steady-state window.");
    }

    [Fact]
    public async Task OpenSynchronousRaw_DeleteAndStoreDisposal_PreserveTheOwnedSnapshot()
    {
        byte[] bytes = "owned raw snapshot"u8.ToArray();
        var stored = await _store.StoreAsync(new MemoryStream(bytes));
        using var reader = _store.OpenSynchronousRaw(stored.Key)!;
        (await _store.DeleteAsync(stored.Key)).ShouldBeTrue();
        await _store.DisposeAsync();
        byte[] output = new byte[bytes.Length];
        reader.ReadAt(0, output).ShouldBe(output.Length);
        output.ShouldBe(bytes);
        reader.Dispose();
        reader.Dispose();
        Should.Throw<ObjectDisposedException>(() => reader.ReadAt(0, output));
        Should.Throw<ObjectDisposedException>(() => _ = reader.Length);
    }

    [Fact]
    public async Task OpenSynchronousRaw_CompressedObject_RejectsWithoutConversion()
    {
        await using var compressed = NewStore(ContentWritePolicy.SeekableZstandard);
        var stored = await compressed.StoreAsync(new MemoryStream(new byte[200_000]));
        stored.IsCompressed.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => _store.OpenSynchronousRaw(stored.Key));
        (await _store.GetInfoAsync(stored.Key))!.Value.Format.ShouldBe(ContentStorageFormat.SeekableZstandard);
        (await _store.VerifyAsync(stored.Key)).ShouldBeTrue();
    }

    [Fact]
    public async Task OpenSynchronousRaw_MissingEmptyAndDisposedStore_RespectOwnership()
    {
        var missing = StorageKey.FromHash(Sha256.FromSpan(SHA256.HashData("missing raw"u8)));
        _store.OpenSynchronousRaw(missing).ShouldBeNull();
        var stored = await _store.StoreAsync(new MemoryStream([]));
        using var reader = _store.OpenSynchronousRaw(stored.Key)!;
        reader.Length.ShouldBe(0);
        reader.ReadAt(0, new byte[1]).ShouldBe(0);
        await _store.DisposeAsync();
        Should.Throw<ObjectDisposedException>(() => _store.OpenSynchronousRaw(stored.Key));
    }

    [Fact]
    public void ReadAt_TruncatedSnapshot_ThrowsBeforeReturningPartialSuccess()
    {
        string path = Path.Combine(_root, "truncated-snapshot.raw");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(path, new byte[128]);
        // Deliberately permit this fixture's adversarial truncation; CAS readers do not permit write sharing.
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        using var reader = new SynchronousRawContent(handle);
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            writer.SetLength(7);
        reader.Length.ShouldBe(128);
        Should.Throw<EndOfStreamException>(() => reader.ReadAt(0, new byte[128]));
    }

    [Fact]
    public void ReadAt_SparseRawObjectBeyondFourGiB_PreservesLongOffsets()
    {
        // An isolated sparse fixture exercises offsets without materializing or hashing a DVD-sized image.
        var key = StorageKey.FromHash(Sha256.FromSpan(SHA256.HashData("sparse raw fixture"u8)));
        string path = Path.Combine(_root, ShardedPathLayout.GetRelativePath(key) + ".raw");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long offset = (1L << 32) + 17;
        byte[] marker = "above four GiB"u8.ToArray();
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            file.SetLength(offset + marker.Length);
            file.Position = offset;
            file.Write(marker);
        }
        using var reader = _store.OpenSynchronousRaw(key)!;
        reader.Length.ShouldBe(offset + marker.Length);
        byte[] output = new byte[marker.Length + 1];
        reader.ReadAt(offset, output).ShouldBe(marker.Length);
        output[..marker.Length].ShouldBe(marker);
        reader.ReadAt(offset + marker.Length, output).ShouldBe(0);
    }
}
