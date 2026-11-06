using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Romd.Domain.Hashing;
using Romd.Storage.Exceptions;
using Romd.Storage.FileSystem;
using Shouldly;
using Xunit;

namespace Romd.Storage.Tests.FileSystem;

public sealed class ContentContractTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "romd-contracts", Guid.NewGuid().ToString("N"));
    private FileSystemContentStore _store = null!;
    public Task InitializeAsync()
    {
        _store = NewStore();
        return Task.CompletedTask;
    }
    private FileSystemContentStore NewStore() => new(Options.Create(new ContentStoreOptions { RootPath = _root, WritePolicy = ContentWritePolicy.SeekableZstandard }));
    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_root, true);
    }
    private static StorageKey Key(byte[] bytes) => StorageKey.FromHash(Sha256.FromSpan(SHA256.HashData(bytes)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreAsync_DefaultPolicy_StoresCompressibleContentAsSeekableZstandard(bool nonSeekable)
    {
        byte[] bytes = new byte[200_000];
        await using var store = new FileSystemContentStore(Options.Create(new ContentStoreOptions { RootPath = _root }));
        using MemoryStream source = nonSeekable ? new NonSeekableSource(bytes) : new MemoryStream(bytes);
        var stored = await store.StoreAsync(source);
        stored.Key.ShouldBe(Key(bytes));
        stored.IsCompressed.ShouldBeTrue();
        stored.StoredSize.ShouldBeLessThan(bytes.Length);
        (await store.GetInfoAsync(stored.Key))!.Value.Format.ShouldBe(ContentStorageFormat.SeekableZstandard);
        (await store.VerifyAsync(stored.Key)).ShouldBeTrue();
        await using var reader = (await store.OpenRandomAccessAsync(stored.Key))!;
        reader.Integrity.ShouldBe(ContentReadIntegrity.FrameChecksum);
        byte[] tail = new byte[100];
        await reader.ReadExactlyAtAsync(bytes.Length - tail.Length, tail);
        tail.ShouldBe(bytes[^100..]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreAsync_RawPolicy_StoresCompressibleContentAsRaw(bool nonSeekable)
    {
        byte[] bytes = new byte[200_000];
        await using var store = new FileSystemContentStore(Options.Create(new ContentStoreOptions
        {
            RootPath = _root,
            WritePolicy = ContentWritePolicy.Raw
        }));
        using MemoryStream source = nonSeekable ? new NonSeekableSource(bytes) : new MemoryStream(bytes);
        var stored = await store.StoreAsync(source);
        stored.Key.ShouldBe(Key(bytes));
        stored.IsCompressed.ShouldBeFalse();
        stored.StoredSize.ShouldBe(bytes.Length);
        (await store.GetInfoAsync(stored.Key))!.Value.Format.ShouldBe(ContentStorageFormat.Raw);
        (await store.VerifyAsync(stored.Key)).ShouldBeTrue();
        await using var reader = (await store.OpenRandomAccessAsync(stored.Key))!;
        reader.Integrity.ShouldBe(ContentReadIntegrity.None);
        byte[] tail = new byte[100];
        await reader.ReadExactlyAtAsync(bytes.Length - tail.Length, tail);
        tail.ShouldBe(bytes[^100..]);
    }

    [Fact]
    public async Task StoreAsync_RawPolicy_DeduplicatesExistingCompressedObjectWithoutConverting()
    {
        byte[] bytes = new byte[200_000];
        var stored = await _store.StoreAsync(new MemoryStream(bytes));
        stored.IsCompressed.ShouldBeTrue();
        await using var raw = new FileSystemContentStore(Options.Create(new ContentStoreOptions
        {
            RootPath = _root,
            WritePolicy = ContentWritePolicy.Raw
        }));
        var duplicate = await raw.StoreAsync(new MemoryStream(bytes));
        duplicate.WasDeduplicated.ShouldBeTrue();
        duplicate.IsCompressed.ShouldBeTrue();
        (await raw.GetInfoAsync(stored.Key))!.Value.Format.ShouldBe(ContentStorageFormat.SeekableZstandard);
        (await raw.VerifyAsync(stored.Key)).ShouldBeTrue();
    }

    private sealed class NonSeekableSource(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task Options_AreSnapshottedAndCannotChangeAnExistingStore()
    {
        var options = new ContentStoreOptions { RootPath = _root, WritePolicy = ContentWritePolicy.Raw };
        await using var store = new FileSystemContentStore(Options.Create(options));
        options.WritePolicy = ContentWritePolicy.SeekableZstandard;
        options.MaxContentSize = 1;
        var stored = await store.StoreAsync(new MemoryStream(new byte[100_000]));
        stored.IsCompressed.ShouldBeFalse();
        (await store.VerifyAsync(stored.Key)).ShouldBeTrue();
    }

    [Fact]
    public async Task StoreAsync_RawPolicy_SkipsCompressionAndPreservesIdentity()
    {
        byte[] bytes = new byte[200_000];
        await using var raw = new FileSystemContentStore(Options.Create(new ContentStoreOptions
        {
            RootPath = _root,
            WritePolicy = ContentWritePolicy.Raw,
            SeekableFrameSize = 64 * 1024
        }));
        var stored = await raw.StoreAsync(new MemoryStream(bytes));
        stored.Key.ShouldBe(Key(bytes));
        stored.IsCompressed.ShouldBeFalse();
        stored.StoredSize.ShouldBe(bytes.Length);
        (await raw.VerifyAsync(stored.Key)).ShouldBeTrue();
        // Switching policy does not rewrite an existing object during deduplication.
        (await _store.StoreAsync(new MemoryStream(bytes))).IsCompressed.ShouldBeFalse();
        Directory.GetFiles(_root, "*.szst", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task StoreAsync_SeekableSourceUnderreportsLength_FirstHashPassIsBounded()
    {
        await using var limited = new FileSystemContentStore(Options.Create(new ContentStoreOptions
        {
            RootPath = _root,
            MaxContentSize = 100
        }));
        using var source = new UnderreportedSource(new byte[1000]);
        await Should.ThrowAsync<InvalidOperationException>(() => limited.StoreAsync(source));
        source.Position.ShouldBe(101);
        Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    private sealed class UnderreportedSource(byte[] bytes) : MemoryStream(bytes)
    {
        public override long Length => 1;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verification_EmptyReadsDoNotSignalEof_AndFullReadVerifies(bool asynchronous)
    {
        byte[] bytes = [1, 2, 3];
        using var stream = new HashVerifyingStream(new MemoryStream(bytes), Key(bytes));
        if (asynchronous)
            (await stream.ReadAsync(Memory<byte>.Empty)).ShouldBe(0);
        else
            stream.Read(Span<byte>.Empty).ShouldBe(0);
        stream.IsVerified.ShouldBeFalse();
        await stream.ForceVerifyAsync();
        stream.IsVerified.ShouldBeTrue();
        stream.TotalBytesRead.ShouldBe(bytes.Length);
    }

    [Fact]
    public async Task Verification_FailureRemainsObservableThroughBothForceMethods()
    {
        using var stream = new HashVerifyingStream(new MemoryStream([1]), Key([2]));
        await Should.ThrowAsync<ContentCorruptedException>(() => stream.ForceVerifyAsync());
        Should.Throw<InvalidOperationException>(() => stream.ForceVerify());
        await Should.ThrowAsync<InvalidOperationException>(() => stream.ForceVerifyAsync());
        stream.IsVerified.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyAsync_DistinguishesMissingValidAndCorrupt()
    {
        (await _store.VerifyAsync(Key([99]))).ShouldBeFalse();
        var stored = await _store.StoreAsync(new MemoryStream([1, 2, 3]));
        (await _store.VerifyAsync(stored.Key)).ShouldBeTrue();
        string blob = Directory.GetFiles(_root, "*.raw", SearchOption.AllDirectories).Single();
        await File.WriteAllBytesAsync(blob, [4, 5, 6]);
        await Should.ThrowAsync<ContentCorruptedException>(() => _store.VerifyAsync(stored.Key));
    }

    [Fact]
    public async Task VerifyAsync_RejectsCorruptSeekTableEvenWhenContentHashStillMatches()
    {
        var stored = await _store.StoreAsync(new MemoryStream(new byte[100_000]));
        string blob = Directory.GetFiles(_root, "*.szst", SearchOption.AllDirectories).Single();
        byte[] encoded = await File.ReadAllBytesAsync(blob);
        encoded[^1] ^= 1;
        await File.WriteAllBytesAsync(blob, encoded);
        await Should.ThrowAsync<InvalidDataException>(() => _store.VerifyAsync(stored.Key));
    }

    [Fact]
    public async Task StreamAdapters_HaveIndependentPositionsAndExplicitOwnership()
    {
        var stored = await _store.StoreAsync(new MemoryStream([10, 20, 30, 40]));
        var reader = (await _store.OpenRandomAccessAsync(stored.Key))!;
        using (var first = reader.AsStream(leaveOpen: true))
        using (var second = reader.AsStream(leaveOpen: true))
        {
            first.Seek(-2, SeekOrigin.End).ShouldBe(2);
            first.ReadByte().ShouldBe(30);
            second.ReadByte().ShouldBe(10);
            first.Seek(100, SeekOrigin.Begin).ShouldBe(100);
            first.ReadByte().ShouldBe(-1);
            Should.Throw<IOException>(() => second.Seek(-2, SeekOrigin.Current));
            second.Position.ShouldBe(1);
            Should.Throw<IOException>(() => first.Seek(long.MaxValue, SeekOrigin.Current));
        }
        byte[] output = new byte[4];
        await reader.ReadExactlyAtAsync(0, output);
        output.ShouldBe(new byte[] { 10, 20, 30, 40 });
        await using (var owner = reader.AsStream())
        {
            owner.Position = 1;
            (await owner.ReadAsync(output)).ShouldBe(3);
        }
        await Should.ThrowAsync<ObjectDisposedException>(async () => await reader.ReadAtAsync(0, output));
    }

    [Fact]
    public async Task ReadExactlyAtAsync_HandlesShortReadsAndEof()
    {
        await using var reader = new ShortReader();
        byte[] output = new byte[3];
        await reader.ReadExactlyAtAsync(0, output);
        output.ShouldBe(new byte[] { 10, 11, 12 });
        await Should.ThrowAsync<EndOfStreamException>(async () => await reader.ReadExactlyAtAsync(1, output));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await reader.ReadExactlyAtAsync(long.MaxValue, output));
    }

    [Fact]
    public async Task MutationLease_BlocksOtherInstancesAndCleanup_ButNotReads()
    {
        var stored = await _store.StoreAsync(new MemoryStream([1, 2, 3]));
        await using var other = NewStore();
        await using (var lease = await MutationLease.AcquireAsync(_root, default))
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Should.ThrowAsync<OperationCanceledException>(() => other.DeleteAsync(stored.Key, ct.Token));
            (await other.VerifyAsync(stored.Key)).ShouldBeTrue();
            await Should.ThrowAsync<OperationCanceledException>(() => other.CleanupTemporaryFilesAsync(ct.Token));
        }
        (await other.DeleteAsync(stored.Key)).ShouldBeTrue();
    }

    [Fact]
    public async Task DisposeAsync_CancelsQueuedMutationsAndWaitsForTheirCleanup()
    {
        await using var lease = await MutationLease.AcquireAsync(_root, default);
        var pending = _store.StoreAsync(new MemoryStream([1]));
        pending.IsCompleted.ShouldBeFalse();
        await Task.WhenAll(_store.DisposeAsync().AsTask(), _store.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        // Staging no longer waits for the lease; cancellation must still discard anything staged.
        Directory.GetFiles(_root, ".tmp-*", SearchOption.AllDirectories).ShouldBeEmpty();
        await Should.ThrowAsync<ObjectDisposedException>(() => _store.CleanupTemporaryFilesAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task StoreAsync_WhileReadingInput_DoesNotHoldMutationLease(bool seekable, bool afterRewind)
    {
        var existing = await _store.StoreAsync(new MemoryStream([1, 2, 3]));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new GatedSource(new byte[300_000], () =>
        {
            reached.SetResult();
            return release.Task;
        }, afterRewind, seekable);
        var slow = _store.StoreAsync(source);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var other = NewStore();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Hashing, spooling, and compression all run without the lease; other mutations proceed.
        await (await MutationLease.AcquireAsync(_root, timeout.Token)).DisposeAsync();
        (await other.DeleteAsync(existing.Key, timeout.Token)).ShouldBeTrue();
        (await other.StoreAsync(new MemoryStream([4, 5, 6]), ct: timeout.Token)).WasDeduplicated.ShouldBeFalse();
        slow.IsCompleted.ShouldBeFalse();
        release.SetResult();
        var stored = await slow.WaitAsync(TimeSpan.FromSeconds(10));
        stored.IsCompressed.ShouldBeTrue();
        (await _store.VerifyAsync(stored.Key)).ShouldBeTrue();
    }

    [Fact]
    public async Task CleanupTemporaryFilesAsync_DuringStaging_SkipsLiveFileAndStorePublishes()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] bytes = new byte[300_000];
        // The spool staging file is held open while the gated, non-seekable input is read.
        using var source = new GatedSource(bytes, () =>
        {
            reached.SetResult();
            return release.Task;
        }, afterRewind: false, seekable: false);
        var slow = _store.StoreAsync(source);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string staging = Directory.GetFiles(_root, ".tmp-*", SearchOption.AllDirectories).Single();
        await using var other = NewStore();
        (await other.CleanupTemporaryFilesAsync()).ShouldBe(0);
        File.Exists(staging).ShouldBeTrue();
        release.SetResult();
        var stored = await slow.WaitAsync(TimeSpan.FromSeconds(10));
        stored.Key.ShouldBe(Key(bytes));
        stored.WasDeduplicated.ShouldBeFalse();
        (await _store.VerifyAsync(stored.Key)).ShouldBeTrue();
        Directory.GetFiles(_root, ".tmp-*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task CleanupTemporaryFilesAsync_OrphanedStagingFiles_AreRemoved()
    {
        string staging = Path.Combine(_root, StagingFile.DirectoryName);
        Directory.CreateDirectory(staging);
        string[] orphans = [Path.Combine(staging, $".tmp-{Guid.NewGuid():N}"), Path.Combine(staging, $".tmp-{Guid.NewGuid():N}")];
        foreach (string orphan in orphans)
            await File.WriteAllBytesAsync(orphan, [1]);
        string foreign = Path.Combine(staging, ".tmp-not-a-cas-file");
        await File.WriteAllTextAsync(foreign, "keep");
        // Cleanup scans only the staging directory, never the object tree.
        string shard = Path.Combine(_root, "ab", "cd");
        Directory.CreateDirectory(shard);
        string outside = Path.Combine(shard, $".tmp-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(outside, [1]);
        (await _store.CleanupTemporaryFilesAsync()).ShouldBe(2);
        orphans.ShouldAllBe(orphan => !File.Exists(orphan));
        File.Exists(foreign).ShouldBeTrue();
        File.Exists(outside).ShouldBeTrue();
    }

    [Fact]
    public async Task StoreAsync_ConcurrentInstancesStagingIdenticalContent_PublishOneObject()
    {
        byte[] bytes = new byte[300_000];
        int arrived = 0;
        var bothStaging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Gate()
        {
            if (Interlocked.Increment(ref arrived) == 2)
                bothStaging.SetResult();
            return bothStaging.Task;
        }
        await using var second = NewStore();
        using var firstSource = new GatedSource(bytes, Gate);
        using var secondSource = new GatedSource(bytes, Gate);
        // Both writers pass the lock-free existence probe and stage before either publishes.
        var results = await Task.WhenAll(_store.StoreAsync(firstSource), second.StoreAsync(secondSource))
            .WaitAsync(TimeSpan.FromSeconds(20));
        results.Count(result => !result.WasDeduplicated).ShouldBe(1);
        results.Select(result => (result.Key, result.Size, result.CompressedSize, result.IsCompressed))
            .Distinct().Count().ShouldBe(1);
        results[0].Key.ShouldBe(Key(bytes));
        results[0].IsCompressed.ShouldBeTrue();
        string[] files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != MutationLease.FileName).ToArray();
        files.Length.ShouldBe(1);
        files[0].ShouldEndWith(".szst");
        (await second.VerifyAsync(results[0].Key)).ShouldBeTrue();
    }

    /// <summary>Awaits a gate on the first read of the hashing pass, or of the pass after the store rewinds.</summary>
    private sealed class GatedSource(byte[] bytes, Func<Task> gate, bool afterRewind = true, bool seekable = true)
        : MemoryStream(bytes)
    {
        private bool _rewound;
        private bool _gated;

        public override bool CanSeek => seekable;

        public override long Position
        {
            get => base.Position;
            set
            {
                _rewound = true;
                base.Position = value;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_gated && _rewound == afterRewind)
            {
                _gated = true;
                await gate().WaitAsync(ct);
            }
            return await base.ReadAsync(buffer, ct);
        }
    }

    [Fact]
    public async Task Cleanup_LiveThenKilledChildWriter_SkipsHeldStagingFileThenRemovesOrphan()
    {
        var stored = await _store.StoreAsync(new MemoryStream([1, 2, 3]));
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(TestHost.Program).Assembly.Location);
        start.ArgumentList.Add(_root);
        using var child = Process.Start(start)!;
        try
        {
            string? line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            line.ShouldBe("staged");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // The child spools outside the lease but holds its staging file open with an exclusive handle.
            await (await MutationLease.AcquireAsync(_root, timeout.Token)).DisposeAsync();
            (await _store.CleanupTemporaryFilesAsync(timeout.Token)).ShouldBe(0);
            string staging = Path.Combine(_root, StagingFile.DirectoryName);
            Directory.GetFiles(staging, ".tmp-*").Length.ShouldBe(1);
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await File.WriteAllTextAsync(Path.Combine(staging, ".tmp-not-a-cas-file"), "keep");
            (await _store.CleanupTemporaryFilesAsync()).ShouldBe(1);
            (await _store.CleanupTemporaryFilesAsync()).ShouldBe(0);
            File.Exists(Path.Combine(staging, ".tmp-not-a-cas-file")).ShouldBeTrue();
            (await _store.VerifyAsync(stored.Key)).ShouldBeTrue();
            (await _store.DeleteAsync(stored.Key)).ShouldBeTrue();
        }
        finally
        {
            if (!child.HasExited)
            { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }

    private sealed class ShortReader : IRandomAccessContent
    {
        public long Length => 3;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            if (offset >= Length || destination.IsEmpty)
                return ValueTask.FromResult(0);
            destination.Span[0] = (byte)(10 + offset);
            return ValueTask.FromResult(1);
        }
    }
}
