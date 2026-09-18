using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romd.Domain.Hashing;
using Romd.Storage.Exceptions;
using Romd.Storage.FileSystem;
using Shouldly;
using Xunit;

namespace Romd.Storage.Tests.FileSystem;

public class FileSystemContentStoreTests : IAsyncLifetime
{
    private readonly ILogger<FileSystemContentStore> _logger = NullLogger<FileSystemContentStore>.Instance;
    private readonly string _testRoot;
    private ContentStoreOptions _options = null!;
    private FileSystemContentStore _store = null!;

    public FileSystemContentStoreTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "romd-tests", Guid.NewGuid().ToString("N"));
    }

    public Task InitializeAsync()
    {
        _options = new ContentStoreOptions
        {
            RootPath = _testRoot,
            CompressionLevel = 3,
            MinCompressionRatio = 0.95,
            MaxContentSize = 10 * 1024 * 1024 // 10 MB for tests
        };

        _store = CreateStore(_options);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    private FileSystemContentStore CreateStore(ContentStoreOptions? options = null)
    {
        var opts = Options.Create(options ?? _options);
        return new FileSystemContentStore(opts, _logger);
    }

    #region Progress Reporting

    [Fact]
    public async Task StoreAsync_ReportsProgress()
    {
        byte[] content = CreateRandomContent(50_000);
        using var input = new MemoryStream(content);
        var progressReports = new ConcurrentBag<StoreProgress>();
        var progress = new Progress<StoreProgress>(p => progressReports.Add(p));

        await _store.StoreAsync(input, progress);

        progressReports.ShouldNotBeEmpty();
        progressReports.ShouldContain(p => p.Phase == StorePhase.Hashing);
        progressReports.ShouldContain(p => p.Phase == StorePhase.Complete);

        var completeReport = progressReports.First(p => p.Phase == StorePhase.Complete);
        completeReport.BytesProcessed.ShouldBe(content.Length);
    }

    #endregion

    #region Storage Path Structure

    [Fact]
    public async Task StoreAsync_CreatesShardedDirectoryStructure()
    {
        byte[] content = CreateRandomContent(1024);
        using var input = new MemoryStream(content);

        var result = await _store.StoreAsync(input);

        string hash = result.Key.Hash.ToString();
        string expectedDir = Path.Combine(_testRoot, hash[..2], hash[2..4]);

        Directory.Exists(expectedDir).ShouldBeTrue();

        string[] files = Directory.GetFiles(expectedDir);
        files.Length.ShouldBe(1);
        Path.GetFileNameWithoutExtension(files[0]).ShouldBe(hash);
    }

    #endregion

    #region Store and Retrieve

    [Fact]
    public async Task StoreAsync_ThenRetrieveAsync_RoundtripsContent()
    {
        byte[] content = CreateRandomContent(1024);
        using var input = new MemoryStream(content);

        var result = await _store.StoreAsync(input);
        await using var retrieved = await _store.RetrieveAsync(result.Key);

        retrieved.ShouldNotBeNull();
        byte[] retrievedBytes = await ReadAllBytesAsync(retrieved);
        retrievedBytes.ShouldBe(content);
    }

    [Theory]
    [MemberData(nameof(TestContents.All), MemberType = typeof(TestContents))]
    public async Task StoreAsync_SyntheticContent_ProducesCorrectKey(ContentTestData content)
    {
        await using var input = content.OpenStream();

        var result = await _store.StoreAsync(input);

        // StoreResult only contains SHA-256 (the CAS key)
        result.Key.Hash.ToString().ShouldBe(content.Sha256);
        result.Size.ShouldBe(content.Size);
    }

    [Fact]
    public async Task StoreAsync_EmptyStream_Succeeds()
    {
        using var input = new MemoryStream([]);

        var result = await _store.StoreAsync(input);

        result.Size.ShouldBe(0);
        result.Key.IsEmpty.ShouldBeFalse();

        await using var retrieved = await _store.RetrieveAsync(result.Key);
        retrieved.ShouldNotBeNull();
        (await ReadAllBytesAsync(retrieved)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_NonExistentKey_ReturnsNull()
    {
        var fakeHash = Sha256.FromSpan(new byte[32].AsSpan().With(0, 0xDE).With(1, 0xAD));
        var key = StorageKey.FromHash(fakeHash);

        var result = await _store.RetrieveAsync(key);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task StoreAsync_NonReadableStream_ThrowsArgumentException()
    {
        using var writeOnly = new NonReadableStream();

        await Should.ThrowAsync<ArgumentException>(() => _store.StoreAsync(writeOnly));
    }

    [Fact]
    public async Task StoreAsync_NullStream_ThrowsArgumentNullException() =>
        await Should.ThrowAsync<ArgumentNullException>(() => _store.StoreAsync(null!));

    #endregion

    #region Deduplication

    [Fact]
    public async Task StoreAsync_SameContentTwice_Deduplicates()
    {
        byte[] content = CreateRandomContent(2048);

        using var input1 = new MemoryStream(content);
        var result1 = await _store.StoreAsync(input1);

        using var input2 = new MemoryStream(content);
        var result2 = await _store.StoreAsync(input2);

        result1.Key.ShouldBe(result2.Key);
        result1.WasDeduplicated.ShouldBeFalse();
        result2.WasDeduplicated.ShouldBeTrue();
    }

    [Fact]
    public async Task StoreAsync_DifferentContent_StoresSeparately()
    {
        byte[] content1 = CreateRandomContent(1024, 1);
        byte[] content2 = CreateRandomContent(1024, 2);

        using var input1 = new MemoryStream(content1);
        using var input2 = new MemoryStream(content2);

        var result1 = await _store.StoreAsync(input1);
        var result2 = await _store.StoreAsync(input2);

        result1.Key.ShouldNotBe(result2.Key);
        result1.WasDeduplicated.ShouldBeFalse();
        result2.WasDeduplicated.ShouldBeFalse();
    }

    #endregion

    #region Concurrent Store Coordination

    [Fact]
    public async Task StoreAsync_ConcurrentIdenticalContent_ProducesOnlyOneFile()
    {
        byte[] content = CreateRandomContent(50_000);
        const int concurrentStores = 10;

        var tasks = Enumerable.Range(0, concurrentStores)
            .Select(_ => Task.Run(async () =>
            {
                using var input = new MemoryStream(content);
                return await _store.StoreAsync(input);
            }))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // All should produce the same key
        var keys = results.Select(r => r.Key).Distinct().ToList();
        keys.Count.ShouldBe(1);

        // Content should be correctly stored and retrievable
        await using var retrieved = await _store.RetrieveAsync(keys[0]);
        retrieved.ShouldNotBeNull();
        (await ReadAllBytesAsync(retrieved)).ShouldBe(content);

        // Most importantly: only ONE physical file should exist on disk
        string hash = results[0].Key.Hash.ToString();
        string dir = Path.Combine(_testRoot, hash[..2], hash[2..4]);
        string[] files = Directory.GetFiles(dir, $"{hash}.*");
        files.Length.ShouldBe(1);
    }

    [Fact]
    public async Task StoreAsync_ConcurrentDifferentContent_AllSucceed()
    {
        const int concurrentStores = 20;
        var contentItems = Enumerable.Range(0, concurrentStores)
            .Select(i => CreateRandomContent(1024, i))
            .ToList();

        var tasks = contentItems
            .Select(content => Task.Run(async () =>
            {
                using var input = new MemoryStream(content);
                return await _store.StoreAsync(input);
            }))
            .ToList();

        var results = await Task.WhenAll(tasks);

        var uniqueKeys = results.Select(r => r.Key).Distinct().ToList();
        uniqueKeys.Count.ShouldBe(concurrentStores);
        results.ShouldAllBe(r => !r.WasDeduplicated);
    }

    #endregion

    #region Compression Behavior

    [Fact]
    public async Task StoreAsync_CompressibleContent_Compresses()
    {
        // Highly repetitive content compresses well
        byte[] content = new byte[10_000];
        Array.Fill(content, (byte)'A');

        using var input = new MemoryStream(content);

        var result = await _store.StoreAsync(input);

        result.IsCompressed.ShouldBeTrue();
        result.CompressedSize.ShouldBeLessThan(result.Size);
        result.CompressionRatio.ShouldBeLessThan(0.5);
    }

    [Fact]
    public async Task StoreAsync_IncompressibleContent_StoresUncompressed()
    {
        // Random content doesn't compress well
        byte[] content = CreateRandomContent(10_000);

        // Use a strict compression ratio to force uncompressed storage
        var strictOptions = new ContentStoreOptions
        {
            RootPath = _testRoot, MinCompressionRatio = 0.5 // Require 50% compression
        };

        await using var strictStore = CreateStore(strictOptions);
        using var input = new MemoryStream(content);

        var result = await strictStore.StoreAsync(input);

        result.IsCompressed.ShouldBeFalse();
        result.CompressedSize.ShouldBe(result.Size);
    }

    [Fact]
    public async Task StoreAsync_CompressionRatioAtThreshold_RespectsThreshold()
    {
        var options = new ContentStoreOptions
        {
            RootPath = _testRoot, MinCompressionRatio = 1.0 // Accept any compression, even if larger
        };

        await using var alwaysCompressStore = CreateStore(options);

        // Use compressible content - random data often expands when compressed
        byte[] content = new byte[5000];
        Array.Fill(content, (byte)'X');

        using var input = new MemoryStream(content);

        var result = await alwaysCompressStore.StoreAsync(input);

        result.IsCompressed.ShouldBeTrue();
    }

    #endregion

    #region Non-Seekable Streams

    [Fact]
    public async Task StoreAsync_NonSeekableStream_Succeeds()
    {
        byte[] content = CreateRandomContent(4096);
        using var nonSeekable = new NonSeekableStream(new MemoryStream(content));

        var result = await _store.StoreAsync(nonSeekable);

        result.Size.ShouldBe(content.Length);
        result.Key.IsEmpty.ShouldBeFalse();

        await using var retrieved = await _store.RetrieveAsync(result.Key);
        retrieved.ShouldNotBeNull();
        (await ReadAllBytesAsync(retrieved)).ShouldBe(content);
    }

    [Fact]
    public async Task StoreAsync_NonSeekableStream_ProducesSameKeyAsSeekable()
    {
        byte[] content = CreateRandomContent(8192);

        using var seekable = new MemoryStream(content);
        var seekableResult = await _store.StoreAsync(seekable);

        // Store again with non-seekable (will deduplicate but key should match)
        using var nonSeekable = new NonSeekableStream(new MemoryStream(content));
        var nonSeekableResult = await _store.StoreAsync(nonSeekable);

        nonSeekableResult.Key.ShouldBe(seekableResult.Key);
    }

    #endregion

    #region Size Limits

    [Fact]
    public async Task StoreAsync_SeekableExceedsMaxSize_ThrowsImmediately()
    {
        var smallLimitOptions = new ContentStoreOptions { RootPath = _testRoot, MaxContentSize = 1000 };

        await using var limitedStore = CreateStore(smallLimitOptions);
        byte[] content = new byte[2000];
        using var input = new MemoryStream(content);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => limitedStore.StoreAsync(input));

        ex.Message.ShouldContain("exceeds maximum");
    }

    [Fact]
    public async Task StoreAsync_NonSeekableExceedsMaxSize_ThrowsDuringRead()
    {
        var smallLimitOptions = new ContentStoreOptions { RootPath = _testRoot, MaxContentSize = 1000 };

        await using var limitedStore = CreateStore(smallLimitOptions);
        byte[] content = new byte[2000];
        using var input = new NonSeekableStream(new MemoryStream(content));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => limitedStore.StoreAsync(input));

        ex.Message.ShouldContain("exceeds maximum");
    }

    [Fact]
    public async Task RetrieveAsync_DecompressionBombProtection_EnforcesLimit()
    {
        // Store compressible content normally
        var normalOptions = new ContentStoreOptions { RootPath = _testRoot, MaxContentSize = 100_000 };

        await using var normalStore = CreateStore(normalOptions);
        byte[] content = new byte[50_000];
        Array.Fill(content, (byte)'X');

        using var input = new MemoryStream(content);
        var result = await normalStore.StoreAsync(input);

        // Now create a store with a smaller read limit
        var restrictedOptions = new ContentStoreOptions
        {
            RootPath = _testRoot, MaxContentSize = 10_000 // Less than actual content
        };

        await using var restrictedStore = CreateStore(restrictedOptions);
        await using var retrieved = await restrictedStore.RetrieveAsync(result.Key);

        retrieved.ShouldNotBeNull();

        // Reading should fail when limit is exceeded
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => ReadAllBytesAsync(retrieved));

        ex.Message.ShouldContain("exceeds maximum");
    }

    #endregion

    #region Hash Verification on Retrieve

    [Fact]
    public async Task RetrieveAsync_FullRead_VerifiesIntegrity()
    {
        byte[] content = CreateRandomContent(2048);
        using var input = new MemoryStream(content);
        var result = await _store.StoreAsync(input);

        await using var retrieved = await _store.RetrieveAsync(result.Key);
        retrieved.ShouldNotBeNull();

        // Full read should succeed and verify
        byte[] bytes = await ReadAllBytesAsync(retrieved);
        bytes.ShouldBe(content);

        // Stream should be verified
        if (retrieved is HashVerifyingStream hvs)
        {
            hvs.IsVerified.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task RetrieveAsync_CorruptedContent_ThrowsContentCorruptedException()
    {
        byte[] content = CreateRandomContent(4096);
        using var input = new MemoryStream(content);
        var result = await _store.StoreAsync(input);

        // Corrupt the stored file
        CorruptStoredFile(result.Key);

        await using var retrieved = await _store.RetrieveAsync(result.Key);
        retrieved.ShouldNotBeNull();

        await Should.ThrowAsync<ContentCorruptedException>(() => ReadAllBytesAsync(retrieved));
    }

    [Fact]
    public async Task RetrieveAsync_ForceVerify_ValidatesWithoutFullRead()
    {
        byte[] content = CreateRandomContent(2048);
        using var input = new MemoryStream(content);
        var result = await _store.StoreAsync(input);

        await using var retrieved = await _store.RetrieveAsync(result.Key);
        retrieved.ShouldNotBeNull();

        // Read partial content
        byte[] partial = new byte[100];
        await retrieved.ReadExactlyAsync(partial);

        // Force verification
        if (retrieved is HashVerifyingStream hvs)
        {
            await hvs.ForceVerifyAsync();
            hvs.IsVerified.ShouldBeTrue();
        }
    }

    #endregion

    #region Exists and GetInfo

    [Fact]
    public async Task ExistsAsync_StoredContent_ReturnsTrue()
    {
        byte[] content = CreateRandomContent(1024);
        using var input = new MemoryStream(content);
        var result = await _store.StoreAsync(input);

        bool exists = await _store.ExistsAsync(result.Key);

        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task ExistsAsync_NonExistentKey_ReturnsFalse()
    {
        var fakeKey = CreateNonExistentKey();

        bool exists = await _store.ExistsAsync(fakeKey);

        exists.ShouldBeFalse();
    }

    [Fact]
    public async Task GetInfoAsync_StoredContent_ReturnsMetadata()
    {
        byte[] content = CreateRandomContent(2048);
        using var input = new MemoryStream(content);
        var storeResult = await _store.StoreAsync(input);

        var info = await _store.GetInfoAsync(storeResult.Key);

        info.ShouldNotBeNull();
        info.Value.Key.ShouldBe(storeResult.Key);
        info.Value.CompressedSize.ShouldBe(storeResult.CompressedSize);
        info.Value.IsCompressed.ShouldBe(storeResult.IsCompressed);
        info.Value.CreatedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task GetInfoAsync_NonExistentKey_ReturnsNull()
    {
        var fakeKey = CreateNonExistentKey();

        var info = await _store.GetInfoAsync(fakeKey);

        info.ShouldBeNull();
    }

    #endregion

    #region Delete

    [Fact]
    public async Task DeleteAsync_ExistingContent_ReturnsTrue()
    {
        byte[] content = CreateRandomContent(1024);
        using var input = new MemoryStream(content);
        var result = await _store.StoreAsync(input);

        bool deleted = await _store.DeleteAsync(result.Key);

        deleted.ShouldBeTrue();
        (await _store.ExistsAsync(result.Key)).ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteAsync_NonExistentKey_ReturnsFalse()
    {
        var fakeKey = CreateNonExistentKey();

        bool deleted = await _store.DeleteAsync(fakeKey);

        deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ThenRetrieve_ReturnsNull()
    {
        byte[] content = CreateRandomContent(1024);
        using var input = new MemoryStream(content);
        var result = await _store.StoreAsync(input);

        await _store.DeleteAsync(result.Key);
        var retrieved = await _store.RetrieveAsync(result.Key);

        retrieved.ShouldBeNull();
    }

    #endregion

    #region Disposal

    [Fact]
    public async Task DisposeAsync_PreventsNewOperations()
    {
        await _store.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(() => _store.StoreAsync(new MemoryStream([1, 2, 3])));
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        await _store.DisposeAsync();
        await _store.DisposeAsync(); // Should not throw
    }

    [Fact]
    public async Task StoreAsync_Cancellation_ThrowsAndCleansUp()
    {
        using var cts = new CancellationTokenSource();
        byte[] content = CreateRandomContent(100_000);

        // Use a slow stream that allows cancellation mid-operation
        using var slowStream = new SlowStream(new MemoryStream(content), 10);

        var storeTask = _store.StoreAsync(slowStream, ct: cts.Token);

        // Cancel after a short delay
        await Task.Delay(50);
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => storeTask);

        // Verify no partial files left (temp files should be cleaned up)
        string[] files = Directory.GetFiles(_testRoot, ".tmp-*", SearchOption.AllDirectories);
        files.ShouldBeEmpty();
    }

    #endregion

    #region Helpers

    private static StorageKey CreateNonExistentKey()
    {
        // Create a valid but non-existent key (not all zeros)
        byte[] fakeHashBytes = new byte[32];
        fakeHashBytes[0] = 0xDE;
        fakeHashBytes[1] = 0xAD;
        fakeHashBytes[2] = 0xBE;
        fakeHashBytes[3] = 0xEF;
        return StorageKey.FromHash(Sha256.FromSpan(fakeHashBytes));
    }

    private static byte[] CreateRandomContent(int size, int seed = 42)
    {
        var random = new Random(seed);
        byte[] buffer = new byte[size];
        random.NextBytes(buffer);
        return buffer;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private void CorruptStoredFile(StorageKey key)
    {
        string hash = key.Hash.ToString();
        string dir = Path.Combine(_testRoot, hash[..2], hash[2..4]);
        string[] files = Directory.GetFiles(dir, $"{hash}.*");

        foreach (string file in files)
        {
            byte[] bytes = File.ReadAllBytes(file);
            if (bytes.Length > 10)
            {
                // Flip some bits in the middle
                bytes[bytes.Length / 2] ^= 0xFF;
                File.WriteAllBytes(file, bytes);
            }
        }
    }

    #endregion

    #region Test Helpers Classes

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => inner.ReadAsync(buffer, offset, count, ct);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => inner.ReadAsync(buffer, ct);

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class NonReadableStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    private sealed class SlowStream(Stream inner, int delayMs) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(delayMs);
            return inner.Read(buffer, offset, count);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await Task.Delay(delayMs, ct);
            return await inner.ReadAsync(buffer, offset, count, ct);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(delayMs, ct);
            return await inner.ReadAsync(buffer, ct);
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    #endregion
}

internal static class SpanExtensions
{
    public static Span<byte> With(this Span<byte> span, int index, byte value)
    {
        span[index] = value;
        return span;
    }
}
