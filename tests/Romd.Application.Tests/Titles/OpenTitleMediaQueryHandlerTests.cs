using ErrorOr;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Queries.OpenTitleMedia;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class OpenTitleMediaQueryHandlerTests
{
    private const int MediaId = 41;
    private const int FileId = 7;

    private readonly IFileStorageService _files = Substitute.For<IFileStorageService>();
    private readonly ITitleMediaFileReader _media = Substitute.For<ITitleMediaFileReader>();

    private OpenTitleMediaQueryHandler CreateHandler() => new(_media, _files);

    [Fact]
    public async Task HandleAsync_StoredMedia_ReturnsCallerOwnedStreamAndDeclaredContentType()
    {
        await using var stream = new MemoryStream([1, 2, 3]);
        _media.FindAsync(MediaId, Arg.Any<CancellationToken>()).Returns(new TitleMediaFile(FileId, "image/png"));
        _files.RetrieveByIdAsync(FileId, Arg.Any<CancellationToken>()).Returns(stream);

        var result = await CreateHandler().HandleAsync(new OpenTitleMediaQuery(MediaId));

        result.IsError.ShouldBeFalse();
        result.Value.Content.ShouldBeSameAs(stream);
        result.Value.ContentType.ShouldBe("image/png");
        stream.CanRead.ShouldBeTrue("the handler hands ownership to the caller instead of disposing");
    }

    [Fact]
    public async Task HandleAsync_MissingRecord_ReturnsNotFoundWithoutOpeningStorage()
    {
        _media.FindAsync(MediaId, Arg.Any<CancellationToken>()).Returns((TitleMediaFile?)null);

        var result = await CreateHandler().HandleAsync(new OpenTitleMediaQuery(MediaId));

        result.FirstError.Type.ShouldBe(ErrorType.NotFound);
        result.FirstError.Code.ShouldBe("Catalog.MediaNotFound");
        await _files.DidNotReceiveWithAnyArgs().RetrieveByIdAsync(default, default);
    }

    [Fact]
    public async Task HandleAsync_MissingStoredBytes_ReturnsNotFound()
    {
        _media.FindAsync(MediaId, Arg.Any<CancellationToken>()).Returns(new TitleMediaFile(FileId, "image/jpeg"));
        _files.RetrieveByIdAsync(FileId, Arg.Any<CancellationToken>()).Returns((Stream?)null);

        var result = await CreateHandler().HandleAsync(new OpenTitleMediaQuery(MediaId));

        result.FirstError.Type.ShouldBe(ErrorType.NotFound);
        result.FirstError.Code.ShouldBe("Catalog.MediaContentNotFound");
    }

    [Fact]
    public async Task HandleAsync_Cancellation_FlowsToBothPortsAndPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _media.FindAsync(MediaId, token).Returns(new TitleMediaFile(FileId, "image/jpeg"));
        _files.RetrieveByIdAsync(FileId, token).ThrowsAsync(new OperationCanceledException(token));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            CreateHandler().HandleAsync(new OpenTitleMediaQuery(MediaId), token));

        await _media.Received(1).FindAsync(MediaId, token);
        await _files.Received(1).RetrieveByIdAsync(FileId, token);
    }
}
