using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Queries.GetTitleSourceReferences;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class GetTitleSourceReferencesQueryHandlerTests
{
    private const int TitleId = 10;

    private readonly ITitleSourceReferenceReader _referenceReader = Substitute.For<ITitleSourceReferenceReader>();
    private readonly ITitleReader _titleReader = Substitute.For<ITitleReader>();

    private GetTitleSourceReferencesQueryHandler CreateHandler() => new(_titleReader, _referenceReader);

    private static TitleSummary CreateTitle() =>
        new(TitleId, 1, "Super Mario World", EnrichmentStatus.None, null, null, null, null, null, null, null,
            DateTimeOffset.UtcNow, null, HasLocalPayload: false);

    [Fact]
    public async Task HandleAsync_UnknownTitle_ReturnsTitleNotFoundWithoutReadingReferences()
    {
        _titleReader.GetSummaryAsync(TitleId, Arg.Any<CancellationToken>()).Returns((TitleSummary?)null);

        var result = await CreateHandler().HandleAsync(new GetTitleSourceReferencesQuery(TitleId));

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.NotFound);
        result.FirstError.Code.ShouldBe("Catalog.TitleNotFound");
        await _referenceReader.DidNotReceive().GetReferencesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_BackedTitle_ReturnsReaderReferences()
    {
        _titleReader.GetSummaryAsync(TitleId, Arg.Any<CancellationToken>()).Returns(CreateTitle());
        var references = new List<TitleSourceReference>
        {
            new(1, CatalogSourceKind.Dat, "No-Intro N64", CatalogSourceStatus.Active, EntryCount: 2),
            new(2, CatalogSourceKind.Import, "Bulk Import", CatalogSourceStatus.Disabled, EntryCount: 1)
        };
        _referenceReader.GetReferencesAsync(TitleId, Arg.Any<CancellationToken>()).Returns(references);

        var result = await CreateHandler().HandleAsync(new GetTitleSourceReferencesQuery(TitleId));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(references);
    }
}
