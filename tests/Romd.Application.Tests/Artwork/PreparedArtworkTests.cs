using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Artwork;

public sealed class PreparedArtworkTests
{
    [Fact]
    public async Task StageAsync_OwnsStreamsAndInputOrder_AndRetainsImmutableMeasuredMetadata()
    {
        var storage = Substitute.For<IFileStorageService>();
        var staged = new List<StagedFile>();
        storage.StageAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var file = new StagedFile(call.Arg<Stream>());
            staged.Add(file);
            return Task.FromResult<IStagedFile>(file);
        });
        byte[] original = [1, 2, 3], variant = [4, 5, 6];
        var variants = new List<ProcessedArtworkVariant> { new("hero", "image/webp", 400, 200, variant) };
        var prepared = await PreparedArtwork.StageAsync(storage, original, new("image/png", 800, 400, variants), default);
        original[0] = 9;
        variant[0] = 9;
        variants.Clear();

        prepared.Files.ShouldBe(staged);
        Should.Throw<NotSupportedException>(() => ((IList<IStagedFile>)prepared.Files).Clear());
        staged.Select(file => file.Source.ReadByte()).ShouldBe([1, 4]);
        var results = staged.Select((file, index) => new FileStoreResult(
            FileEntity.CreateNew(file.Sha256, 3, 3, false, DateTimeOffset.UnixEpoch), index == 0, index != 0)).ToArray();
        var retained = prepared.Retain(results, "Artist", "https://example.test/art");
        (retained.Original.Name, retained.Original.Width, retained.Original.Height).ShouldBe(("original", 800, 400));
        (retained.Variants.Single().Name, retained.Variants.Single().ContentType).ShouldBe(("hero", "image/webp"));
        (retained.Attribution, retained.SourcePageUrl).ShouldBe(("Artist", "https://example.test/art"));
        Should.Throw<NotSupportedException>(() => ((IList<RetainedArtworkFile>)retained.Variants).Clear());
        Should.Throw<ArgumentException>(() => prepared.Retain([], null, null));

        await prepared.DisposeAsync();
        await prepared.DisposeAsync();

        staged.ShouldAllBe(file => file.DisposeCount == 1 && file.SourceWasOpenAtDisposal);
        staged.ShouldAllBe(file => !file.Source.CanRead);
        Should.Throw<ObjectDisposedException>(() => prepared.Retain(results, null, null));
    }

    [Fact]
    public async Task StageAsync_PartialFailureAndCleanupFailure_PreservesBothAndDisposesEveryAcquiredResource()
    {
        var storage = Substitute.For<IFileStorageService>();
        var primary = new IOException("variant staging failed");
        var cleanup = new IOException("original cleanup failed");
        var sources = new List<Stream>();
        StagedFile? first = null;
        storage.StageAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            sources.Add(call.Arg<Stream>());
            if (sources.Count == 2)
                return Task.FromException<IStagedFile>(primary);
            first = new StagedFile(sources[0], cleanup);
            return Task.FromResult<IStagedFile>(first);
        });

        var failure = await Should.ThrowAsync<AggregateException>(() => PreparedArtwork.StageAsync(storage, [1],
            new("image/png", 800, 400, [new("hero", "image/webp", 400, 200, [2])]), default));

        failure.Flatten().InnerExceptions.ShouldContain(primary);
        failure.Flatten().InnerExceptions.ShouldContain(cleanup);
        first!.DisposeCount.ShouldBe(1);
        sources.ShouldAllBe(source => !source.CanRead);
    }

    [Fact]
    public async Task DisposeAsync_OneStagedFileFails_StillDisposesEveryFileAndItsSource()
    {
        var storage = Substitute.For<IFileStorageService>();
        var staged = new List<StagedFile>();
        storage.StageAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var file = new StagedFile(call.Arg<Stream>(), staged.Count == 0 ? new IOException("cleanup") : null);
            staged.Add(file);
            return Task.FromResult<IStagedFile>(file);
        });
        var prepared = await PreparedArtwork.StageAsync(storage, [1],
            new("image/png", 800, 400, [new("hero", "image/webp", 400, 200, [2])]), default);

        await Should.ThrowAsync<AggregateException>(() => prepared.DisposeAsync().AsTask());

        staged.ShouldAllBe(file => file.DisposeCount == 1 && !file.Source.CanRead);
    }

    internal sealed class StagedFile(Stream source, Exception? cleanup = null) : IStagedFile
    {
        public Stream Source => source;
        public Sha256 Sha256 { get; } = Sha256.FromBytes(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 }));
        public long Size => 3;
        public Md5? ArtifactMd5 => null;
        public Sha1? ArtifactSha1 => null;
        public int DisposeCount { get; private set; }
        public bool SourceWasOpenAtDisposal { get; private set; }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            SourceWasOpenAtDisposal = source.CanRead;
            return cleanup is null ? ValueTask.CompletedTask : ValueTask.FromException(cleanup);
        }
    }
}
