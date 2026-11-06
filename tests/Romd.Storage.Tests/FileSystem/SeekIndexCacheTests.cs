using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Romd.Domain.Hashing;
using Romd.Storage.FileSystem;
using Shouldly;
using Xunit;

namespace Romd.Storage.Tests.FileSystem;

public sealed class SeekIndexCacheTests : IAsyncLifetime
{
    private static readonly FileGeneration First = new(100, new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "romd-index-cache", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public void TryGet_SameGeneration_ReturnsTheCachedIndex()
    {
        var cache = new SeekIndexCache(1024);
        var index = Index(frames: 4);
        cache.Add(Key(1), First, index);

        cache.TryGet(Key(1), First, out var cached).ShouldBeTrue();

        cached.ShouldBeSameAs(index);
    }

    [Fact]
    public void TryGet_DifferentGeneration_MissesSoAReplacedFileIsNeverServedAStaleTable()
    {
        var cache = new SeekIndexCache(1024);
        cache.Add(Key(1), First, Index(frames: 4));

        cache.TryGet(Key(1), First with { LastWriteTimeUtc = First.LastWriteTimeUtc.AddTicks(1) }, out _).ShouldBeFalse();
        cache.TryGet(Key(1), First with { Length = First.Length + 1 }, out _).ShouldBeFalse();
    }

    [Fact]
    public void Add_OverBudget_EvictsLeastRecentlyUsedFirst()
    {
        // Each 4-frame index retains 5 offsets * 8 + 4 checksums * 4 = 56 bytes.
        var cache = new SeekIndexCache(3 * 56);
        cache.Add(Key(1), First, Index(frames: 4));
        cache.Add(Key(2), First, Index(frames: 4));
        cache.Add(Key(3), First, Index(frames: 4));
        cache.TryGet(Key(1), First, out _).ShouldBeTrue();

        cache.Add(Key(4), First, Index(frames: 4));

        cache.TryGet(Key(2), First, out _).ShouldBeFalse();
        cache.TryGet(Key(1), First, out _).ShouldBeTrue();
        cache.TryGet(Key(3), First, out _).ShouldBeTrue();
        cache.TryGet(Key(4), First, out _).ShouldBeTrue();
        cache.RetainedBytes.ShouldBe(3 * 56);
    }

    [Fact]
    public void Add_IndexLargerThanTheBudget_IsNotCached()
    {
        var cache = new SeekIndexCache(55);

        cache.Add(Key(1), First, Index(frames: 4));

        cache.TryGet(Key(1), First, out _).ShouldBeFalse();
        cache.RetainedBytes.ShouldBe(0);
    }

    [Fact]
    public void Add_SameKey_ReplacesTheEntryAndItsAccounting()
    {
        var cache = new SeekIndexCache(1024);
        cache.Add(Key(1), First, Index(frames: 4));
        var replacement = Index(frames: 8);
        var second = First with { Length = 200 };

        cache.Add(Key(1), second, replacement);

        cache.TryGet(Key(1), second, out var cached).ShouldBeTrue();
        cached.ShouldBeSameAs(replacement);
        cache.RetainedBytes.ShouldBe(9 * 8 + 8 * 4);
    }

    [Fact]
    public void Remove_EvictsTheEntry()
    {
        var cache = new SeekIndexCache(1024);
        cache.Add(Key(1), First, Index(frames: 4));

        cache.Remove(Key(1));

        cache.TryGet(Key(1), First, out _).ShouldBeFalse();
        cache.RetainedBytes.ShouldBe(0);
    }

    [Fact]
    public async Task OpenRandomAccessAsync_ObjectReplacedByAnotherWriter_ReadsTheNewLayout()
    {
        byte[] bytes = Compressible(1024 * 1024);
        await using var reader64 = NewStore(64 * 1024);
        await using var writer256 = NewStore(256 * 1024);
        var key = (await reader64.StoreAsync(new MemoryStream(bytes))).Key;
        await (await reader64.OpenRandomAccessAsync(key))!.DisposeAsync();
        reader64.CachedSeekTables.ShouldBe(1);

        // Another process deletes and republishes the same content with a different frame size.
        (await writer256.DeleteAsync(key)).ShouldBeTrue();
        (await writer256.StoreAsync(new MemoryStream(bytes))).WasDeduplicated.ShouldBeFalse();

        await using var reader = (await reader64.OpenRandomAccessAsync(key))!;
        byte[] tail = new byte[4096];
        await reader.ReadExactlyAtAsync(bytes.Length - tail.Length, tail);
        tail.ShouldBe(bytes[^4096..]);
    }

    [Fact]
    public async Task DeleteAsync_EvictsTheCachedSeekTable()
    {
        await using var store = NewStore(64 * 1024);
        var key = (await store.StoreAsync(new MemoryStream(Compressible(300_000)))).Key;
        await (await store.OpenRandomAccessAsync(key))!.DisposeAsync();
        store.CachedSeekTables.ShouldBe(1);

        await store.DeleteAsync(key);

        store.CachedSeekTables.ShouldBe(0);
    }

    [Fact]
    public async Task OpenRandomAccessAsync_CacheDisabled_ReadsWithoutCaching()
    {
        await using var store = NewStore(64 * 1024, cacheBytes: 0);
        byte[] bytes = Compressible(300_000);
        var key = (await store.StoreAsync(new MemoryStream(bytes))).Key;

        await using var reader = (await store.OpenRandomAccessAsync(key))!;
        byte[] head = new byte[4096];
        await reader.ReadExactlyAtAsync(0, head);

        head.ShouldBe(bytes[..4096]);
        store.CachedSeekTables.ShouldBe(0);
    }

    private FileSystemContentStore NewStore(int frameSize, long cacheBytes = 64L * 1024 * 1024) =>
        new(Options.Create(new ContentStoreOptions
        {
            RootPath = _root,
            SeekableFrameSize = frameSize,
            SeekIndexCacheBytes = cacheBytes
        }));

    private static StorageKey Key(byte value) => StorageKey.FromHash(Sha256.FromSpan(SHA256.HashData([value])));

    private static SeekableZstandard.Index Index(int frames) =>
        new(64 * 1024, frames * 64L * 1024, new long[frames + 1], new uint[frames]);

    private static byte[] Compressible(int size)
    {
        byte[] bytes = new byte[size];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i % 251);
        return bytes;
    }
}
