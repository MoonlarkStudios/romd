using Microsoft.Extensions.Options;
using Romd.Storage.FileSystem;
using Shouldly;
using Xunit;

namespace Romd.Storage.Tests.FileSystem;

public sealed class ContentInventoryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "romd-inventory", Guid.NewGuid().ToString("N"));
    private FileSystemContentStore _store = null!;

    public Task InitializeAsync()
    {
        _store = new FileSystemContentStore(Options.Create(new ContentStoreOptions { RootPath = _root }));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_root, true);
    }

    [Fact]
    public async Task EnumerateAsync_EmptyStore_ListsNothing() =>
        (await ListAsync()).ShouldBeEmpty();

    [Fact]
    public async Task EnumerateAsync_PublishedObjects_ListsEachOnceAndSkipsStagingAndForeignFiles()
    {
        var compressed = await _store.StoreAsync(new MemoryStream(new byte[300_000]));
        var raw = await _store.StoreAsync(new MemoryStream(Random(300_000)));
        await using var unpublished = await _store.StageAsync(new MemoryStream(Random(300_000, seed: 2)));
        string shard = Path.GetDirectoryName(Directory.GetFiles(_root, compressed.Key + ".*", SearchOption.AllDirectories).Single())!;
        await File.WriteAllTextAsync(Path.Combine(shard, "notes.txt"), "not an object");
        await File.WriteAllTextAsync(Path.Combine(shard, raw.Key + ".raw"), "a hash in the wrong shard");
        await File.WriteAllTextAsync(Path.Combine(shard, compressed.Key + ".zst"), "retired representation");

        var listed = await ListAsync();

        listed.ShouldBe([compressed.Key, raw.Key], ignoreOrder: true);
        compressed.IsCompressed.ShouldBeTrue();
        raw.IsCompressed.ShouldBeFalse();
    }

    [Fact]
    public async Task EnumerateAsync_ObjectWithBothRepresentations_ListsItOnce()
    {
        var stored = await _store.StoreAsync(new MemoryStream(new byte[300_000]));
        string seekable = Directory.GetFiles(_root, stored.Key + ".szst", SearchOption.AllDirectories).Single();
        File.Copy(seekable, Path.ChangeExtension(seekable, ".raw"));

        (await ListAsync()).ShouldBe([stored.Key]);
    }

    [Fact]
    public async Task StageAsync_StagesInTheStagingDirectoryOnly()
    {
        await using var staged = await _store.StageAsync(new MemoryStream(new byte[300_000]));

        string file = Directory.GetFiles(_root, StagingFile.Prefix + "*", SearchOption.AllDirectories).Single();
        Path.GetDirectoryName(file).ShouldBe(Path.Combine(_root, StagingFile.DirectoryName));
    }

    [Fact]
    public async Task EnumerateAsync_AfterDispose_Throws()
    {
        await _store.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(ListAsync);
    }

    private async Task<List<StorageKey>> ListAsync()
    {
        var keys = new List<StorageKey>();
        await foreach (var key in _store.EnumerateAsync())
            keys.Add(key);
        return keys;
    }

    private static byte[] Random(int size, int seed = 1)
    {
        byte[] bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
