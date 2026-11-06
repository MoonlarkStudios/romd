using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Romd.Domain.Hashing;
using Romd.Storage.Exceptions;
using Romd.Storage.FileSystem;
using Shouldly;
using Xunit;

namespace Romd.Storage.Tests.FileSystem;

public sealed class StagedContentTests : IAsyncLifetime
{
    private const int SampledFrameSize = 64 * 1024;
    // Content larger than four frames is sampled before the store writes a full seekable candidate.
    private const int CompressionSample = 4 * SampledFrameSize;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "romd-staged", Guid.NewGuid().ToString("N"));
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StageAsync_BeforePublish_ContentIsNotReadable(bool seekable)
    {
        byte[] bytes = Compressible(300_000);
        await using var staged = await _store.StageAsync(new CountingStream(bytes, seekable));

        staged.Key.ShouldBe(Key(bytes));
        staged.Size.ShouldBe(bytes.Length);
        (await _store.ExistsAsync(staged.Key)).ShouldBeFalse();

        var published = await staged.PublishAsync();

        published.WasDeduplicated.ShouldBeFalse();
        published.IsCompressed.ShouldBeTrue();
        (await _store.VerifyAsync(staged.Key)).ShouldBeTrue();
        StagingFiles().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StageAsync_DisposedWithoutPublishing_LeavesNoObjectOrStagingFile(bool seekable)
    {
        byte[] bytes = Compressible(300_000);

        await (await _store.StageAsync(new CountingStream(bytes, seekable))).DisposeAsync();

        (await _store.ExistsAsync(Key(bytes))).ShouldBeFalse();
        StagingFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task StageAsync_ExpectedKey_SkipsTheHashingPass()
    {
        byte[] bytes = Compressible(300_000);
        var hashed = new CountingStream(bytes);
        var trusted = new CountingStream(bytes);

        await using (await _store.StageAsync(hashed))
        { }
        await using (await _store.StageAsync(trusted, Key(bytes)))
        { }

        hashed.BytesRead.ShouldBe(2L * bytes.Length);
        trusted.BytesRead.ShouldBe(bytes.Length);
    }

    [Fact]
    public async Task StageAsync_ExpectedKeyOfPublishedObject_ReadsNothingAndDeduplicates()
    {
        byte[] bytes = Compressible(300_000);
        var stored = await _store.StoreAsync(new MemoryStream(bytes));
        var source = new CountingStream(bytes);

        await using var staged = await _store.StageAsync(source, stored.Key);
        var published = await staged.PublishAsync();

        source.BytesRead.ShouldBe(0);
        published.WasDeduplicated.ShouldBeTrue();
        published.Size.ShouldBe(bytes.Length);
        published.CompressedSize.ShouldBe(stored.CompressedSize);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StageAsync_ExpectedKeyMismatch_ThrowsAndLeavesNoStagingFile(bool seekable)
    {
        byte[] bytes = Compressible(300_000);
        var wrong = Key([1, 2, 3]);

        var thrown = await Should.ThrowAsync<ContentCorruptedException>(
            () => _store.StageAsync(new CountingStream(bytes, seekable), wrong));

        thrown.Key.ShouldBe(wrong);
        StagingFiles().ShouldBeEmpty();
        (await _store.ExistsAsync(Key(bytes))).ShouldBeFalse();
    }

    [Fact]
    public async Task StageAsync_IncompressibleContentLargerThanSample_StagesRawWithoutFullCompressedCandidate()
    {
        await using var store = CreateSampledStore();
        byte[] bytes = RandomNumberGenerator.GetBytes(16 * CompressionSample);
        var source = new CountingStream(bytes);

        await using var staged = await store.StageAsync(source, Key(bytes));
        var published = await staged.PublishAsync();

        source.BytesRead.ShouldBe(bytes.Length + CompressionSample);
        published.IsCompressed.ShouldBeFalse();
        await AssertRawAsync(published.Key, bytes);
        StagingFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task StageAsync_CompressibleContentLargerThanSample_StoresSeekableZstandard()
    {
        await using var store = CreateSampledStore();
        byte[] bytes = Compressible(16 * CompressionSample);
        var source = new CountingStream(bytes);
        using var synced = new SyncedBytes();

        await using var staged = await store.StageAsync(source, Key(bytes));
        var published = await staged.PublishAsync();

        source.BytesRead.ShouldBe(bytes.Length + CompressionSample);
        synced.Total.ShouldBe(published.StoredSize);
        published.IsCompressed.ShouldBeTrue();
        (await store.GetInfoAsync(published.Key))!.Value.Format.ShouldBe(ContentStorageFormat.SeekableZstandard);
        (await store.VerifyAsync(published.Key)).ShouldBeTrue();
    }

    [Fact]
    public async Task StageAsync_CompressiblePrefixIncompressibleRemainder_WholeContentRatioStillStagesRaw()
    {
        await using var store = CreateSampledStore(minCompressionRatio: 0.5);
        byte[] bytes = [.. Compressible(CompressionSample), .. RandomNumberGenerator.GetBytes(4 * CompressionSample)];
        var source = new CountingStream(bytes);
        using var synced = new SyncedBytes();

        await using var staged = await store.StageAsync(source, Key(bytes));
        var published = await staged.PublishAsync();

        // The sample passes, so the whole-content candidate decides and is then discarded, never synced, for raw bytes.
        source.BytesRead.ShouldBe(CompressionSample + 2L * bytes.Length);
        synced.Total.ShouldBe(bytes.Length);
        published.IsCompressed.ShouldBeFalse();
        await AssertRawAsync(published.Key, bytes);
        StagingFiles().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StageAsync_ContentNoLargerThanSample_DecidesInMemoryAndWritesOnlyTheKeptRepresentation(bool compressible)
    {
        await using var store = CreateSampledStore();
        byte[] bytes = compressible ? Compressible(CompressionSample) : RandomNumberGenerator.GetBytes(CompressionSample);
        var source = new CountingStream(bytes);
        using var synced = new SyncedBytes();

        await using var staged = await store.StageAsync(source, Key(bytes));
        var published = await staged.PublishAsync();

        source.BytesRead.ShouldBe(bytes.Length);
        synced.Total.ShouldBe(published.StoredSize);
        published.IsCompressed.ShouldBe(compressible);
        (await store.VerifyAsync(published.Key)).ShouldBeTrue();
    }

    [Fact]
    public async Task StageAsync_SourceLongerThanItsReportedLength_StagesTheWholeSourceThroughTheStreamingWriter()
    {
        await using var store = CreateSampledStore();
        byte[] bytes = Compressible(4 * CompressionSample);
        var source = new UnderReportingStream(bytes, reportedLength: CompressionSample);

        await using var staged = await store.StageAsync(source);
        var published = await staged.PublishAsync();

        published.Key.ShouldBe(Key(bytes));
        published.Size.ShouldBe(bytes.Length);
        published.IsCompressed.ShouldBeTrue();
        (await store.VerifyAsync(published.Key)).ShouldBeTrue();
    }

    [Fact]
    public async Task StageAsync_IncompressibleContentLargerThanSampleWithWrongKey_ThrowsAndLeavesNoStagingFile()
    {
        await using var store = CreateSampledStore();
        byte[] bytes = RandomNumberGenerator.GetBytes(4 * CompressionSample);
        var wrong = Key([1, 2, 3]);
        var source = new CountingStream(bytes);

        var thrown = await Should.ThrowAsync<ContentCorruptedException>(() => store.StageAsync(source, wrong));

        thrown.Key.ShouldBe(wrong);
        source.BytesRead.ShouldBe(bytes.Length + CompressionSample, "the early-raw writer, not a compressed candidate, caught the mismatch");
        StagingFiles().ShouldBeEmpty();
        (await store.ExistsAsync(Key(bytes))).ShouldBeFalse();
    }

    [Fact]
    public async Task PublishAsync_ObjectDeletedAfterStagingFoundIt_EncodesAgainFromSource()
    {
        byte[] bytes = Compressible(300_000);
        var stored = await _store.StoreAsync(new MemoryStream(bytes));
        await using var staged = await _store.StageAsync(new CountingStream(bytes), stored.Key);
        (await _store.DeleteAsync(stored.Key)).ShouldBeTrue();

        var published = await staged.PublishAsync();

        published.WasDeduplicated.ShouldBeFalse();
        (await _store.VerifyAsync(stored.Key)).ShouldBeTrue();
    }

    [Fact]
    public async Task PublishAsync_AnotherWriterPublishedFirst_DeduplicatesAndDiscardsStagedCopyImmediately()
    {
        byte[] bytes = Compressible(300_000);
        await using var staged = await _store.StageAsync(new MemoryStream(bytes));
        StagingFiles().Length.ShouldBe(1);
        await _store.StoreAsync(new MemoryStream(bytes));

        var published = await staged.PublishAsync();

        published.WasDeduplicated.ShouldBeTrue();
        StagingFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task StageAsync_Completed_HoldsNoMutationLeaseUntilPublish()
    {
        await using var staged = await _store.StageAsync(new MemoryStream(Compressible(300_000)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using (await MutationLease.AcquireAsync(_root, timeout.Token))
        {
            using var blocked = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Should.ThrowAsync<OperationCanceledException>(() => staged.PublishAsync(blocked.Token));
        }

        // A failed attempt leaves the handle publishable.
        (await staged.PublishAsync(timeout.Token)).WasDeduplicated.ShouldBeFalse();
    }

    [Fact]
    public async Task PublishAsync_Twice_Throws()
    {
        await using var staged = await _store.StageAsync(new MemoryStream(Compressible(300_000)));
        await staged.PublishAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => staged.PublishAsync());
    }

    [Fact]
    public async Task PublishAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var staged = await _store.StageAsync(new MemoryStream(Compressible(300_000)));
        await staged.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(() => staged.PublishAsync());
    }

    [Fact]
    public async Task PublishAsync_AfterStoreDispose_ThrowsObjectDisposedAndDiscardsOnDispose()
    {
        var staged = await _store.StageAsync(new MemoryStream(Compressible(300_000)));
        await _store.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(() => staged.PublishAsync());
        await staged.DisposeAsync();
        StagingFiles().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task StageRawAsync_CompressedDefault_PublishesRawWithoutChangingOrdinaryPolicy(bool seekable, bool expectedKey)
    {
        byte[] bytes = Compressible(300_000);
        using var source = new CountingStream(bytes, seekable);
        await using var staged = await _store.StageRawAsync(source, expectedKey ? Key(bytes) : null);

        (await _store.ExistsAsync(staged.Key)).ShouldBeFalse();
        var published = await staged.PublishAsync();

        published.WasDeduplicated.ShouldBeFalse();
        published.IsCompressed.ShouldBeFalse();
        published.CompressedSize.ShouldBe(bytes.Length);
        await AssertRawAsync(staged.Key, bytes);
        StagingFiles().ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
        source.ToArray().ShouldBe(bytes);

        byte[] ordinary = Compressible(300_001);
        var ordinaryResult = await _store.StoreAsync(new MemoryStream(ordinary));
        ordinaryResult.IsCompressed.ShouldBeTrue();
        (await _store.GetInfoAsync(ordinaryResult.Key))!.Value.Format.ShouldBe(ContentStorageFormat.SeekableZstandard);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StageRawAsync_ExistingCompressedObject_RefusesWithoutReplacingIt(bool seekable)
    {
        byte[] bytes = Compressible(300_000);
        var stored = await _store.StoreAsync(new MemoryStream(bytes));
        stored.IsCompressed.ShouldBeTrue();
        using var source = new CountingStream(bytes, seekable);

        var error = await Should.ThrowAsync<NotSupportedException>(() => _store.StageRawAsync(source, stored.Key));

        error.Message.ShouldContain(stored.Key.ToString());
        source.BytesRead.ShouldBe(seekable ? 0 : bytes.Length);
        source.CanRead.ShouldBeTrue();
        StagingFiles().ShouldBeEmpty();
        RawFiles().ShouldBeEmpty();
        (await _store.GetInfoAsync(stored.Key))!.Value.Format.ShouldBe(ContentStorageFormat.SeekableZstandard);
        (await _store.VerifyAsync(stored.Key)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishAsync_RawStaged_CompressedWriterWins_RefusesAndCleansOnDispose(bool seekable)
    {
        byte[] bytes = Compressible(300_000);
        using var source = new CountingStream(bytes, seekable);
        await using var staged = await _store.StageRawAsync(source, Key(bytes));
        StagingFiles().Length.ShouldBe(seekable ? 1 : 2);
        // A cooperating publisher wins after raw staging, before its authoritative publication check.
        await using var otherWriter = new FileSystemContentStore(Options.Create(new ContentStoreOptions { RootPath = _root }));
        var winner = await otherWriter.StoreAsync(new MemoryStream(bytes));
        winner.IsCompressed.ShouldBeTrue();

        await Should.ThrowAsync<NotSupportedException>(() => staged.PublishAsync());

        RawFiles().ShouldBeEmpty();
        (await _store.GetInfoAsync(staged.Key))!.Value.Format.ShouldBe(ContentStorageFormat.SeekableZstandard);
        (await _store.VerifyAsync(staged.Key)).ShouldBeTrue();
        await staged.DisposeAsync();
        StagingFiles().ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
        source.ToArray().ShouldBe(bytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishAsync_RawDedup_DeletedAndRecreatedCompressed_RefusesWithoutRereadingSource(bool seekable)
    {
        byte[] bytes = Compressible(300_000);
        var initial = await StoreRawAsync(bytes);
        using var source = new CountingStream(bytes, seekable);
        await using var staged = await _store.StageRawAsync(source, initial.Key);
        long beforePublish = source.BytesRead;
        (await _store.DeleteAsync(initial.Key)).ShouldBeTrue();
        var replacement = await _store.StoreAsync(new MemoryStream(bytes));
        replacement.IsCompressed.ShouldBeTrue();

        await Should.ThrowAsync<NotSupportedException>(() => staged.PublishAsync());

        source.BytesRead.ShouldBe(beforePublish);
        RawFiles().ShouldBeEmpty();
        (await _store.GetInfoAsync(initial.Key))!.Value.Format.ShouldBe(ContentStorageFormat.SeekableZstandard);
        (await _store.VerifyAsync(initial.Key)).ShouldBeTrue();
        await staged.DisposeAsync();
        StagingFiles().ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishAsync_RawDedup_DeletedObject_ReencodesRawDespiteCompressedDefault(bool seekable)
    {
        byte[] bytes = Compressible(300_000);
        var initial = await StoreRawAsync(bytes);
        using var source = new CountingStream(bytes, seekable);
        await using var staged = await _store.StageRawAsync(source, initial.Key);
        (await _store.DeleteAsync(initial.Key)).ShouldBeTrue();

        var published = await staged.PublishAsync();

        published.WasDeduplicated.ShouldBeFalse();
        published.IsCompressed.ShouldBeFalse();
        source.BytesRead.ShouldBe(bytes.Length);
        await AssertRawAsync(initial.Key, bytes);
        StagingFiles().ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task PublishAsync_RawDedup_CompressedWriterWinsDuringRecovery_RefusesAndPreservesWinner()
    {
        byte[] bytes = Compressible(300_000);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var initialSource = new MemoryStream(bytes, writable: false);
        await using var initialStage = await _store.StageRawAsync(initialSource, Key(bytes), ct: deadline.Token);
        var initial = await initialStage.PublishAsync(deadline.Token);
        initial.IsCompressed.ShouldBeFalse();
        (await _store.GetInfoAsync(initial.Key, deadline.Token))!.Value.Format.ShouldBe(ContentStorageFormat.Raw);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new GatedRecoverySource(bytes, () =>
        {
            reached.TrySetResult();
            return release.Task;
        });
        await using var staged = await _store.StageRawAsync(source, initial.Key, ct: deadline.Token);
        source.BytesRead.ShouldBe(0);
        reached.Task.IsCompleted.ShouldBeFalse();
        StagingFiles().ShouldBeEmpty();
        (await _store.DeleteAsync(initial.Key, deadline.Token)).ShouldBeTrue();

        var publication = staged.PublishAsync(deadline.Token);
        try
        {
            await reached.Task.WaitAsync(deadline.Token);
            publication.IsCompleted.ShouldBeFalse();
            source.BytesRead.ShouldBe(0);
            await using var otherWriter = new FileSystemContentStore(Options.Create(new ContentStoreOptions { RootPath = _root }));
            // Recovery is paused inside its retained source read. Its first deduplication lease must be released.
            await using (await MutationLease.AcquireAsync(_root, deadline.Token))
            {
                release.Task.IsCompleted.ShouldBeFalse();
                publication.IsCompleted.ShouldBeFalse();
            }
            using var winnerSource = new MemoryStream(bytes, writable: false);
            var winner = await otherWriter.StoreAsync(winnerSource, ct: deadline.Token);
            winner.Key.ShouldBe(initial.Key);
            winner.WasDeduplicated.ShouldBeFalse();
            winner.IsCompressed.ShouldBeTrue();
            release.Task.IsCompleted.ShouldBeFalse();
            publication.IsCompleted.ShouldBeFalse();
            string winnerPath = Directory.GetFiles(_root, "*.szst", SearchOption.AllDirectories).Single();
            byte[] winnerBytes = await File.ReadAllBytesAsync(winnerPath, deadline.Token);
            var winnerInfo = await otherWriter.GetInfoAsync(initial.Key, deadline.Token)
                ?? throw new InvalidDataException("The competing compressed object was missing.");
            winnerInfo.Format.ShouldBe(ContentStorageFormat.SeekableZstandard);

            release.TrySetResult();
            var error = await Should.ThrowAsync<NotSupportedException>(() => publication);

            error.Message.ShouldContain(initial.Key.ToString());
            source.BytesRead.ShouldBe(bytes.Length);
            RawFiles().ShouldBeEmpty();
            (await _store.GetInfoAsync(initial.Key, deadline.Token)).ShouldBe(winnerInfo);
            (await File.ReadAllBytesAsync(winnerPath, deadline.Token)).ShouldBe(winnerBytes);
            (await _store.VerifyAsync(initial.Key, deadline.Token)).ShouldBeTrue();
            StagingFiles().Length.ShouldBe(1);
            await staged.DisposeAsync();
            StagingFiles().ShouldBeEmpty();
            source.CanRead.ShouldBeTrue();
            source.ToArray().ShouldBe(bytes);
        }
        finally
        {
            // Release and cancel even when a held-lease regression prevents the other writer from progressing.
            release.TrySetResult();
            await deadline.CancelAsync();
            try
            { await publication.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (NotSupportedException) { }
            catch (OperationCanceledException) { }
            await staged.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StageRawAsync_ExistingRawObject_DeduplicatesWithoutReencoding(bool seekable)
    {
        byte[] bytes = Compressible(300_000);
        var initial = await StoreRawAsync(bytes);
        using var source = new CountingStream(bytes, seekable);
        await using var staged = await _store.StageRawAsync(source, initial.Key);
        long stagedReads = source.BytesRead;

        var published = await staged.PublishAsync();

        stagedReads.ShouldBe(seekable ? 0 : bytes.Length);
        source.BytesRead.ShouldBe(stagedReads);
        published.WasDeduplicated.ShouldBeTrue();
        published.IsCompressed.ShouldBeFalse();
        await AssertRawAsync(initial.Key, bytes);
        StagingFiles().ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData(true, -1)]
    [InlineData(true, 1)]
    [InlineData(false, -1)]
    [InlineData(false, 1)]
    public async Task StageRawAsync_ExistingRawObjectWrongLength_RefusesAndSucceedsAfterRestoration(bool seekable, int sizeDelta)
    {
        byte[] bytes = Compressible(1024);
        var initial = await StoreRawAsync(bytes);
        string rawPath = RawFiles().Single();
        byte[] damaged = new byte[bytes.Length + sizeDelta];
        bytes.AsSpan(0, Math.Min(bytes.Length, damaged.Length)).CopyTo(damaged);
        await File.WriteAllBytesAsync(rawPath, damaged);
        (await _store.GetInfoAsync(initial.Key))!.Value.StoredSize.ShouldBe(damaged.Length);
        using var source = new CountingStream(bytes, seekable);
        IStagedContent? unexpected = null;
        InvalidDataException? error = null;

        try
        { unexpected = await _store.StageRawAsync(source, initial.Key); }
        catch (InvalidDataException exception) { error = exception; }
        finally
        {
            // A missing refusal must not leak the unexpectedly returned staged handle in the red control.
            if (unexpected is not null)
                await unexpected.DisposeAsync();
        }

        error.ShouldNotBeNull().Message.ShouldContain(initial.Key.ToString());
        (await File.ReadAllBytesAsync(rawPath)).ShouldBe(damaged);
        (await _store.GetInfoAsync(initial.Key))!.Value.Format.ShouldBe(ContentStorageFormat.Raw);
        source.BytesRead.ShouldBe(seekable ? 0 : bytes.Length);
        source.CanRead.ShouldBeTrue();
        source.ToArray().ShouldBe(bytes);
        StagingFiles().ShouldBeEmpty();

        await File.WriteAllBytesAsync(rawPath, bytes);
        (await File.ReadAllBytesAsync(rawPath)).ShouldBe(bytes);
        using var retrySource = new CountingStream(bytes, seekable);
        await using var retry = await _store.StageRawAsync(retrySource, initial.Key);
        long retryReads = retrySource.BytesRead;
        var published = await retry.PublishAsync();

        published.WasDeduplicated.ShouldBeTrue();
        published.IsCompressed.ShouldBeFalse();
        published.Size.ShouldBe(bytes.Length);
        published.CompressedSize.ShouldBe(bytes.Length);
        retryReads.ShouldBe(seekable ? 0 : bytes.Length);
        retrySource.BytesRead.ShouldBe(retryReads);
        await AssertRawAsync(initial.Key, bytes);
        StagingFiles().ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
        source.ToArray().ShouldBe(bytes);
        retrySource.CanRead.ShouldBeTrue();
        retrySource.ToArray().ShouldBe(bytes);
    }

    [Theory]
    [InlineData(true, -1)]
    [InlineData(true, 1)]
    [InlineData(false, -1)]
    [InlineData(false, 1)]
    public async Task PublishAsync_RawDedup_ObjectLengthChangedAfterStaging_RefusesAndSameHandleRetriesAfterRestoration(bool seekable, int sizeDelta)
    {
        byte[] bytes = Compressible(1024);
        var initial = await StoreRawAsync(bytes);
        using var source = new CountingStream(bytes, seekable);
        await using var staged = await _store.StageRawAsync(source, initial.Key);
        long stagedReads = source.BytesRead;
        stagedReads.ShouldBe(seekable ? 0 : bytes.Length);
        StagingFiles().Length.ShouldBe(seekable ? 0 : 1);
        string rawPath = RawFiles().Single();
        byte[] damaged = new byte[bytes.Length + sizeDelta];
        bytes.AsSpan(0, Math.Min(bytes.Length, damaged.Length)).CopyTo(damaged);
        await File.WriteAllBytesAsync(rawPath, damaged);
        (await _store.GetInfoAsync(initial.Key))!.Value.StoredSize.ShouldBe(damaged.Length);

        var error = await Should.ThrowAsync<InvalidDataException>(() => staged.PublishAsync());

        error.Message.ShouldContain(initial.Key.ToString());
        (await File.ReadAllBytesAsync(rawPath)).ShouldBe(damaged);
        (await _store.GetInfoAsync(initial.Key))!.Value.Format.ShouldBe(ContentStorageFormat.Raw);
        source.BytesRead.ShouldBe(stagedReads);
        source.CanRead.ShouldBeTrue();
        source.ToArray().ShouldBe(bytes);
        StagingFiles().Length.ShouldBe(seekable ? 0 : 1);

        await File.WriteAllBytesAsync(rawPath, bytes);
        (await File.ReadAllBytesAsync(rawPath)).ShouldBe(bytes);
        var published = await staged.PublishAsync();

        published.WasDeduplicated.ShouldBeTrue();
        published.IsCompressed.ShouldBeFalse();
        published.Size.ShouldBe(bytes.Length);
        published.CompressedSize.ShouldBe(bytes.Length);
        source.BytesRead.ShouldBe(stagedReads);
        await AssertRawAsync(initial.Key, bytes);
        StagingFiles().ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
        source.ToArray().ShouldBe(bytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StageRawAsync_ExpectedKeyMismatch_ThrowsAndLeavesNoObjectOrStagingFile(bool seekable)
    {
        byte[] bytes = Compressible(300_000);
        var wrong = Key([1, 2, 3]);
        using var source = new CountingStream(bytes, seekable);

        var error = await Should.ThrowAsync<ContentCorruptedException>(() => _store.StageRawAsync(source, wrong));

        error.Key.ShouldBe(wrong);
        (await _store.ExistsAsync(wrong)).ShouldBeFalse();
        (await _store.ExistsAsync(Key(bytes))).ShouldBeFalse();
        StagingFiles().ShouldBeEmpty();
        RawFiles().ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StageRawAsync_DisposedWithoutPublishing_ReleasesOnlyOwnedFiles(bool seekable)
    {
        byte[] bytes = Compressible(300_000);
        using var source = new CountingStream(bytes, seekable);
        var staged = await _store.StageRawAsync(source, Key(bytes));

        await staged.DisposeAsync();

        (await _store.ExistsAsync(Key(bytes))).ShouldBeFalse();
        StagingFiles().ShouldBeEmpty();
        RawFiles().ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
        source.ToArray().ShouldBe(bytes);
        await Should.ThrowAsync<ObjectDisposedException>(() => staged.PublishAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StageRawAsync_CanceledDuringRead_CleansOwnedFilesAndPreservesSource(bool seekable)
    {
        byte[] bytes = Compressible(300_000);
        using var cancellation = new CancellationTokenSource();
        using var source = new CountingStream(bytes, seekable, cancellation);

        await Should.ThrowAsync<OperationCanceledException>(() => _store.StageRawAsync(source, Key(bytes), ct: cancellation.Token));

        source.BytesRead.ShouldBeGreaterThan(0);
        (await _store.ExistsAsync(Key(bytes))).ShouldBeFalse();
        StagingFiles().ShouldBeEmpty();
        RawFiles().ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
        source.ToArray().ShouldBe(bytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishAsync_RawPending_CanceledWhileLeaseHeld_RemainsPublishable(bool seekable)
    {
        byte[] bytes = Compressible(300_000);
        using var source = new CountingStream(bytes, seekable);
        await using var staged = await _store.StageRawAsync(source, Key(bytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (await MutationLease.AcquireAsync(_root, timeout.Token))
        {
            using var blocked = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Should.ThrowAsync<OperationCanceledException>(() => staged.PublishAsync(blocked.Token));
            (await _store.ExistsAsync(staged.Key)).ShouldBeFalse();
            RawFiles().ShouldBeEmpty();
        }

        var published = await staged.PublishAsync(timeout.Token);

        published.WasDeduplicated.ShouldBeFalse();
        await AssertRawAsync(staged.Key, bytes);
        StagingFiles().ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishAsync_RawDedup_LeaseHeld_WaitsForAuthoritativeCheck(bool seekable)
    {
        byte[] bytes = Compressible(300_000);
        var initial = await StoreRawAsync(bytes);
        using var source = new CountingStream(bytes, seekable);
        await using var staged = await _store.StageRawAsync(source, initial.Key);
        long beforePublish = source.BytesRead;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (await MutationLease.AcquireAsync(_root, timeout.Token))
        {
            using var blocked = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Should.ThrowAsync<OperationCanceledException>(() => staged.PublishAsync(blocked.Token));
        }

        var published = await staged.PublishAsync(timeout.Token);

        published.WasDeduplicated.ShouldBeTrue();
        source.BytesRead.ShouldBe(beforePublish);
        await AssertRawAsync(initial.Key, bytes);
        StagingFiles().ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StageRawAsync_OverSizeLimit_RefusesWithoutPublishing(bool seekable)
    {
        byte[] bytes = Compressible(300_000);
        await using var limited = new FileSystemContentStore(Options.Create(new ContentStoreOptions
        { RootPath = _root, MaxContentSize = bytes.Length - 1 }));
        using var source = new CountingStream(bytes, seekable);

        await Should.ThrowAsync<InvalidOperationException>(() => limited.StageRawAsync(source, Key(bytes)));

        (await limited.ExistsAsync(Key(bytes))).ShouldBeFalse();
        StagingFiles().ShouldBeEmpty();
        RawFiles().ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task PublishAsync_RawDedup_DeletedObjectAndChangedSource_RefusesCorruptRecovery()
    {
        byte[] bytes = Compressible(300_000);
        var initial = await StoreRawAsync(bytes);
        using var source = new CountingStream(bytes);
        await using var staged = await _store.StageRawAsync(source, initial.Key);
        (await _store.DeleteAsync(initial.Key)).ShouldBeTrue();
        bytes[0] ^= 0xff;

        await Should.ThrowAsync<ContentCorruptedException>(() => staged.PublishAsync());

        (await _store.ExistsAsync(initial.Key)).ShouldBeFalse();
        RawFiles().ShouldBeEmpty();
        await staged.DisposeAsync();
        StagingFiles().ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
    }

    private async Task<StoreResult> StoreRawAsync(byte[] bytes)
    {
        using var source = new MemoryStream(bytes, writable: false);
        await using var staged = await _store.StageRawAsync(source, Key(bytes));
        return await staged.PublishAsync();
    }

    private async Task AssertRawAsync(StorageKey key, byte[] bytes)
    {
        var info = await _store.GetInfoAsync(key) ?? throw new InvalidDataException("Published raw object was missing.");
        info.Format.ShouldBe(ContentStorageFormat.Raw);
        info.StoredSize.ShouldBe(bytes.Length);
        (await _store.VerifyAsync(key)).ShouldBeTrue();
        using var reader = _store.OpenSynchronousRaw(key) ?? throw new InvalidDataException("Published raw object could not be opened.");
        byte[] actual = new byte[bytes.Length];
        int offset = 0;
        while (offset < actual.Length)
        {
            int read = reader.ReadAt(offset, actual.AsSpan(offset));
            read.ShouldBeGreaterThan(0);
            offset += read;
        }
        actual.ShouldBe(bytes);
    }

    private FileSystemContentStore CreateSampledStore(double minCompressionRatio = 0.95) =>
        new(Options.Create(new ContentStoreOptions
        {
            RootPath = _root,
            SeekableFrameSize = SampledFrameSize,
            MinCompressionRatio = minCompressionRatio
        }));

    private string[] StagingFiles() => Directory.GetFiles(_root, ".tmp-*", SearchOption.AllDirectories);
    private string[] RawFiles() => Directory.GetFiles(_root, "*.raw", SearchOption.AllDirectories);

    private static StorageKey Key(byte[] bytes) => StorageKey.FromHash(Sha256.FromSpan(SHA256.HashData(bytes)));

    private static byte[] Compressible(int size)
    {
        byte[] bytes = new byte[size];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i % 7);
        return bytes;
    }

    private sealed class GatedRecoverySource(byte[] bytes, Func<Task> gate) : MemoryStream(bytes, writable: false)
    {
        private bool _gated;
        public long BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_gated)
            {
                _gated = true;
                await gate().WaitAsync(ct);
            }
            int read = await base.ReadAsync(buffer, ct);
            BytesRead += read;
            return read;
        }
    }

    private sealed class CountingStream(byte[] bytes, bool seekable = true, CancellationTokenSource? cancelAfterRead = null)
        : MemoryStream(bytes, writable: false)
    {
        public long BytesRead { get; private set; }
        public override bool CanSeek => seekable;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int read = await base.ReadAsync(buffer, ct);
            BytesRead += read;
            if (read > 0)
                cancelAfterRead?.Cancel();
            return read;
        }
    }

    private sealed class UnderReportingStream(byte[] bytes, long reportedLength) : MemoryStream(bytes, writable: false)
    {
        public override long Length => reportedLength;
    }

    /// <summary>Sums staging syncs recorded on this test's own async flow, excluding concurrently running tests.</summary>
    private sealed class SyncedBytes : IDisposable
    {
        private static readonly AsyncLocal<SyncedBytes?> Scope = new();
        private readonly MeterListener _listener = new();
        private long _total;

        public SyncedBytes()
        {
            Scope.Value = this;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ContentStoreDiagnostics.MeterName && instrument.Name == "cas.write.synced")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
            {
                if (Scope.Value == this)
                    Interlocked.Add(ref _total, value);
            });
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose()
        {
            _listener.Dispose();
            Scope.Value = null;
        }
    }
}
