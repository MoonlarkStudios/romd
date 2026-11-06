using System.Security.Cryptography;
using NSubstitute;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Romd.Infrastructure.Storage;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ReferenceBlobPublicationStagingTests
{
    [Fact]
    public async Task PublishStaged_OwnedBatchCopiesAndOrderedDuplicateIdentities_RequireOwnerFlushAndDisposal()
    {
        var fixture = new Fixture();
        List<byte[]> contents = [[1, 2], [3, 4], [1, 2]];
        fixture.AfterFirstStage = () => { Array.Fill<byte>(contents[1], 0); contents.Clear(); };

        var publication = await fixture.Store.PublishStagedAsync(contents, default);

        fixture.Handles.Select(handle => handle.Bytes).ShouldBe(new byte[][] { [1, 2], [3, 4], [1, 2] });
        fixture.PublishedHandles.ShouldBe(fixture.Handles);
        publication.RequiresFlush.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => publication.ReadAfterFlush());
        fixture.Handles.ShouldAllBe(handle => handle.DisposeCount == 0);
        fixture.Batch.Flushed = true;
        publication.RequiresFlush.ShouldBeFalse();
        publication.ReadAfterFlush().ShouldBe([21, 22, 21]);
        await publication.DisposeAsync();
        fixture.Handles.ShouldAllBe(handle => handle.DisposeCount == 1);
        fixture.Streams.ShouldAllBe(stream => !stream.CanRead);
        Should.Throw<ObjectDisposedException>(() => publication.RequiresFlush);
        Should.Throw<ObjectDisposedException>(() => publication.ReadAfterFlush());
        await publication.DisposeAsync();
        fixture.Handles.ShouldAllBe(handle => handle.DisposeCount == 1);
        await fixture.Files.Received(1).PublishStagedAsync(Arg.Any<IReadOnlyList<IStagedFile>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishStaged_PartialPreparationFails_PreservesPrimaryAndClosesEveryOwnedResource(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var primary = cancel ? (Exception)new OperationCanceledException("Caller cancelled.", cancellation.Token) : new IOException("Preparation failed.");
        var cleanup = new IOException("Staging disposal failed.");
        var fixture = new Fixture { StageFailureIndex = 2, Primary = primary, FirstCleanupFailure = cleanup, CancelOnFailure = cancel ? cancellation : null };

        var actual = await Record.ExceptionAsync(() => fixture.Store.PublishStagedAsync([[1], [2], [3]], cancellation.Token));

        actual.ShouldBeSameAs(primary);
        if (cancel)
            actual.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        primary.Data["Romd.ReferencePublication.CleanupFailure"].ShouldBeSameAs(cleanup);
        fixture.Handles.Count.ShouldBe(2);
        fixture.Handles.ShouldAllBe(handle => handle.DisposeCount == 1);
        fixture.Streams.Count.ShouldBe(3);
        fixture.Streams.ShouldAllBe(stream => !stream.CanRead);
        await fixture.Files.DidNotReceiveWithAnyArgs().PublishStagedAsync(Arg.Any<IReadOnlyList<IStagedFile>>(), default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishStaged_PublicationFails_PreservesPrimaryAndRetainsSecondaryCleanup(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var primary = cancel ? (Exception)new OperationCanceledException("Caller cancelled.", cancellation.Token) : new IOException("Publication failed.");
        var cleanup = new IOException("Staging disposal failed.");
        var fixture = new Fixture { PublishFailure = primary, FirstCleanupFailure = cleanup, CancelOnFailure = cancel ? cancellation : null };

        var actual = await Record.ExceptionAsync(() => fixture.Store.PublishStagedAsync([[1], [2]], cancellation.Token));

        actual.ShouldBeSameAs(primary);
        if (cancel)
            actual.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        primary.Data["Romd.ReferencePublication.CleanupFailure"].ShouldBeSameAs(cleanup);
        fixture.Handles.ShouldAllBe(handle => handle.DisposeCount == 1);
        fixture.Streams.ShouldAllBe(stream => !stream.CanRead);
    }

    [Fact]
    public async Task Dispose_FirstCleanupFails_StillClosesRemainingHandlesAndIsNotRepeated()
    {
        var primary = new IOException("First disposal failed.");
        var fixture = new Fixture { FirstCleanupFailure = primary };
        var publication = await fixture.Store.PublishStagedAsync([[1], [2]], default);

        (await Record.ExceptionAsync(() => publication.DisposeAsync().AsTask())).ShouldBeSameAs(primary);

        fixture.Handles.ShouldAllBe(handle => handle.DisposeCount == 1);
        fixture.Streams.ShouldAllBe(stream => !stream.CanRead);
        await publication.DisposeAsync();
        fixture.Handles.ShouldAllBe(handle => handle.DisposeCount == 1);
    }

    [Fact]
    public async Task Dispose_MultipleFailures_RetainsEveryCauseAfterAllResourcesAreAttempted()
    {
        var first = new IOException("First cleanup.");
        var second = new IOException("Second cleanup.");
        var fixture = new Fixture { FirstCleanupFailure = first, SecondCleanupFailure = second };
        var publication = await fixture.Store.PublishStagedAsync([[1], [2]], default);

        var error = await Should.ThrowAsync<AggregateException>(() => publication.DisposeAsync().AsTask());

        error.InnerExceptions.ShouldBe([first, second]);
        fixture.Handles.ShouldAllBe(handle => handle.DisposeCount == 1);
        fixture.Streams.ShouldAllBe(stream => !stream.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishStaged_ExpiredFileReceipt_RejectsBothReadSurfacesButStillDisposes(bool empty)
    {
        var fixture = new Fixture();
        var publication = await fixture.Store.PublishStagedAsync(empty ? [] : [[1]], default);
        fixture.Batch.Expired = true;

        Should.Throw<InvalidOperationException>(() => publication.RequiresFlush);
        Should.Throw<InvalidOperationException>(() => publication.ReadAfterFlush());
        await publication.DisposeAsync();
        fixture.Handles.ShouldAllBe(handle => handle.DisposeCount == 1);
    }

    [Fact]
    public async Task ReadOnlyBridge_RejectsPublicationBeforeTouchingContentStore()
    {
        var cas = Substitute.For<IContentAddressableStore>();
        var store = new ReadOnlyReferenceBlobStore(cas);

        await Should.ThrowAsync<InvalidOperationException>(() => store.PublishStagedAsync([[1]], default));

        cas.ReceivedCalls().ShouldBeEmpty();
    }

    private sealed class Fixture
    {
        public IFileStorageService Files { get; } = Substitute.For<IFileStorageService>();
        public List<Handle> Handles { get; } = [];
        public List<Stream> Streams { get; } = [];
        public IReadOnlyList<IStagedFile> PublishedHandles { get; private set; } = [];
        public Batch Batch { get; } = new();
        public int? StageFailureIndex { get; init; }
        public Exception? Primary { get; init; }
        public Exception? PublishFailure { get; init; }
        public CancellationTokenSource? CancelOnFailure { get; init; }
        public Exception? FirstCleanupFailure { get; init; }
        public Exception? SecondCleanupFailure { get; init; }
        public Action? AfterFirstStage { get; set; }
        public ReferenceBlobStore Store { get; }
        public Fixture()
        {
            Files.StageAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                var stream = call.Arg<Stream>();
                int index = Streams.Count;
                Streams.Add(stream);
                if (index == StageFailureIndex)
                { CancelOnFailure?.Cancel(); throw Primary!; }
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy, call.Arg<CancellationToken>());
                var handle = new Handle(copy.ToArray(), index == 0 ? FirstCleanupFailure : index == 1 ? SecondCleanupFailure : null);
                Handles.Add(handle);
                if (index == 0)
                    AfterFirstStage?.Invoke();
                return (IStagedFile)handle;
            });
            Files.PublishStagedAsync(Arg.Any<IReadOnlyList<IStagedFile>>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                if (PublishFailure is not null)
                { CancelOnFailure?.Cancel(); throw PublishFailure; }
                PublishedHandles = call.Arg<IReadOnlyList<IStagedFile>>().ToArray();
                var ids = new Dictionary<Sha256, int>();
                Batch.Results = PublishedHandles.Select(handle =>
                {
                    bool added = !ids.TryGetValue(handle.Sha256, out int id);
                    if (added)
                        ids.Add(handle.Sha256, id = ids.Count + 21);
                    return new FileStoreResult(FileEntity.Rehydrate(id, handle.Sha256, handle.Size, handle.Size, false,
                        DateTimeOffset.UnixEpoch), added, !added);
                }).ToArray();
                return Task.FromResult<IStagedFilePublicationBatch>(Batch);
            });
            Store = new ReferenceBlobStore(Substitute.For<IContentAddressableStore>(), Files);
        }
    }

    private sealed class Handle(byte[] bytes, Exception? cleanup) : IStagedFile
    {
        public byte[] Bytes => bytes;
        public int DisposeCount { get; private set; }
        public Sha256 Sha256 { get; } = Sha256.FromSpan(SHA256.HashData(bytes));
        public long Size => bytes.Length;
        public Md5? ArtifactMd5 => null;
        public Sha1? ArtifactSha1 => null;
        public ValueTask DisposeAsync() { DisposeCount++; if (cleanup is not null) throw cleanup; return ValueTask.CompletedTask; }
    }

    private sealed class Batch : IStagedFilePublicationBatch
    {
        public FileStoreResult[] Results { get; set; } = [];
        public bool Flushed { get; set; }
        public bool Expired { get; set; }
        public bool RequiresFlush => Expired ? throw new InvalidOperationException("Expired receipt.") : !Flushed && Results.Any(row => row.WasNew);
        public IReadOnlyList<FileStoreResult> ReadAfterFlush() => RequiresFlush
            ? throw new InvalidOperationException("Owner must flush.") : Results;
    }
}
