using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Romd.Infrastructure.Enrichment;
using Romd.Infrastructure.Jobs.Executors;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

/// <summary>
///     The HTTP media stager hands callers owned, staged media; the title mutation later publishes it with
///     the title. Orchestration-level ownership is covered in the application tests.
/// </summary>
public sealed class EnrichmentMediaStagingTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
    private readonly List<TrackedDownload> _downloads = [];
    private readonly List<IStagedFile> _staged = [];

    public EnrichmentMediaStagingTests()
    {
        _storage.StageAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var staged = Substitute.For<IStagedFile>();
            _staged.Add(staged);
            return staged;
        });
    }

    [Fact]
    public async Task StageAsync_SuccessfulDownload_ReturnsCallerOwnedStagedMedia()
    {
        var media = await CreateStager(HttpStatusCode.OK).StageAsync(1, "providerA", MediaType.Screenshot,
            "https://images.example/shot.png", CancellationToken.None);

        media.ShouldNotBeNull();
        media.Type.ShouldBe(MediaType.Screenshot);
        media.SourceId.ShouldBe("providerA");
        media.File.ShouldBeSameAs(_staged.Single());
        media.ToTitleMedia(1, 7, createdAt: DateTimeOffset.UnixEpoch).ContentType.ShouldBe("image/png");
        _downloads.Single().Disposed.ShouldBeFalse();

        await media.DisposeAsync();

        _ = _staged.Single().Received(1).DisposeAsync();
        _downloads.Single().Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task StageAsync_ResponseLargerThanHttpBuffer_StreamsIntoTemporaryStorage()
    {
        using var client = new HttpClient(new PngHandler(HttpStatusCode.OK)) { MaxResponseContentBufferSize = 1 };

        var media = await CreateStager(HttpStatusCode.OK, client).StageAsync(1, "providerA", MediaType.Screenshot,
            "https://images.example/shot.png", CancellationToken.None);

        media.ShouldNotBeNull("provider media must stream without buffering the complete HTTP response in memory");
        await media.DisposeAsync();
    }

    [Fact]
    public async Task StageAsync_BodyStallsAfterHeaders_StillHonorsHttpTimeout()
    {
        using var client = new HttpClient(new PngHandler(HttpStatusCode.OK)) { Timeout = TimeSpan.FromMilliseconds(100) };
        using var cancellation = new CancellationTokenSource();
        var tempFiles = Substitute.For<ITempFileFactory>();
        tempFiles.CreateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(call =>
            WaitForBodyAsync(call.Arg<CancellationToken>()));
        var stager = new HttpEnrichmentMediaStager(_storage, tempFiles, client,
            NullLogger<HttpEnrichmentMediaStager>.Instance);

        try
        {
            var media = await stager.StageAsync(1, "providerA", MediaType.Screenshot,
                "https://images.example/shot.png", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));

            media.ShouldBeNull();
            _staged.ShouldBeEmpty();
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        static async Task<ITempFile> WaitForBodyAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The stalled body must be cancelled.");
        }
    }

    [Fact]
    public async Task StageAsync_ProviderRejectsRequest_ReturnsNullWithoutStaging()
    {
        var media = await CreateStager(HttpStatusCode.NotFound).StageAsync(1, "providerA", MediaType.Screenshot,
            "https://images.example/missing.png", CancellationToken.None);

        media.ShouldBeNull();
        _downloads.ShouldBeEmpty();
        _staged.ShouldBeEmpty();
    }

    [Fact]
    public async Task StageAsync_StagingFails_ReleasesTheDownloadAndReturnsNull()
    {
        _storage.StageAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk full"));

        var media = await CreateStager(HttpStatusCode.OK).StageAsync(1, "providerA", MediaType.Screenshot,
            "https://images.example/shot.png", CancellationToken.None);

        media.ShouldBeNull();
        _downloads.Single().Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task StageAsync_CancelledWhileStaging_ReleasesTheDownloadAndPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        _storage.StageAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>()).Returns<IStagedFile>(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Should.ThrowAsync<OperationCanceledException>(() => CreateStager(HttpStatusCode.OK).StageAsync(1,
            "providerA", MediaType.Screenshot, "https://images.example/shot.png", cancellation.Token));

        _downloads.Single().Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task AttachPublishedMedia_MapsFilesInOrderWithSuppliedPreciseTime()
    {
        var title = CreateTitle(1);
        var screenshot = Substitute.For<IStagedFile>();
        var logo = Substitute.For<IStagedFile>();
        var createdAt = new DateTimeOffset(2026, 11, 4, 10, 9, 8, TimeSpan.FromHours(3)).AddTicks(1234567);
        await using var first = new StagedTitleMedia(MediaType.Screenshot, "providerA", "image/png", "https://a", screenshot, new TrackedDownload());
        await using var second = new StagedTitleMedia(MediaType.Logo, "providerA", "image/png", "https://b", logo, new TrackedDownload());
        title.AttachPublishedMedia([first, second], [Published(7), Published(8)], createdAt);

        title.Media.Select(media => (media.Type, media.FileId)).ShouldBe([(MediaType.Screenshot, 7), (MediaType.Logo, 8)]);
        title.Media.ShouldAllBe(media => media.CreatedAt == createdAt && media.CreatedAt.Offset == createdAt.Offset);
    }

    [Fact]
    public async Task AttachPublishedMedia_IncompleteBatchRejectsBeforeChangingTitle()
    {
        var title = CreateTitle(1);
        await using var first = new StagedTitleMedia(MediaType.Screenshot, "providerA", "image/png", "https://a", Substitute.For<IStagedFile>(), new TrackedDownload());
        await using var second = new StagedTitleMedia(MediaType.Logo, "providerA", "image/png", "https://b", Substitute.For<IStagedFile>(), new TrackedDownload());

        Should.Throw<ArgumentException>(() => title.AttachPublishedMedia([first, second], [Published(7)], DateTimeOffset.UnixEpoch));

        title.Media.ShouldBeEmpty();
    }

    private HttpEnrichmentMediaStager CreateStager(HttpStatusCode status, HttpClient? client = null)
    {
        var tempFiles = Substitute.For<ITempFileFactory>();
        tempFiles.CreateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>(), Arg.Any<Func<ReadOnlyMemory<byte>, bool>?>(), Arg.Any<Func<ReadOnlyMemory<byte>, CancellationToken, Task>?>()).Returns(async call =>
        {
            await call.Arg<Stream>().CopyToAsync(Stream.Null, call.Arg<CancellationToken>());
            var download = new TrackedDownload();
            _downloads.Add(download);
            return (ITempFile)download;
        });
        return new HttpEnrichmentMediaStager(_storage, tempFiles, client ?? new HttpClient(new PngHandler(status)),
            NullLogger<HttpEnrichmentMediaStager>.Instance);
    }

    private static FileStoreResult Published(int fileId) =>
        new(FileEntity.Rehydrate(fileId, Sha256.FromSpan(new byte[32]), 1, 1, false, DateTimeOffset.UtcNow), true, false);

    private static Title CreateTitle(int id) => Title.Rehydrate(
        id, 10, "Super Mario World", "super mario world", null, null, null, null, null, null, null,
        EnrichmentStatus.None, null, DateTimeOffset.UtcNow);

    private sealed class PngHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(Png) });
    }

    private sealed class TrackedDownload : ITempFile
    {
        public bool TryBeginChildUse() => true;
        public bool TryEndChildUse() => true;
        public bool Quarantine() => true;
        public bool Disposed { get; private set; }
        public string Path => "download.tmp";
        public Sha256 FileSha256 { get; } = Sha256.FromSpan(new byte[32]);
        public long FileSize => Png.Length;
        public Stream OpenRead() => new MemoryStream(Png, writable: false);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
