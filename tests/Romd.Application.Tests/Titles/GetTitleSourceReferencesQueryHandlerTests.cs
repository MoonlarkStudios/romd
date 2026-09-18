using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Queries.GetTitleSourceReferences;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class GetTitleSourceReferencesQueryHandlerTests
{
    private const int TitleId = 10;

    private readonly ITitleSourceReferenceReader _referenceReader = Substitute.For<ITitleSourceReferenceReader>();
    private readonly ITitleRepository _titleRepository = Substitute.For<ITitleRepository>();

    private GetTitleSourceReferencesQueryHandler CreateHandler() => new(_titleRepository, _referenceReader);

    private static Title CreateTitle() =>
        Title.Rehydrate(
            id: TitleId,
            platformId: 1,
            name: "Super Mario World",
            normalizedName: "super mario world",
            description: null,
            publisher: null,
            developer: null,
            genre: null,
            releaseDate: null,
            players: null,
            rating: null,
            enrichmentStatus: EnrichmentStatus.None,
            lastEnrichedAt: null,
            createdAt: DateTimeOffset.UtcNow,
            metadataLayers: []);

    [Fact]
    public async Task HandleAsync_UnknownTitle_ReturnsTitleNotFoundWithoutReadingReferences()
    {
        _titleRepository.GetByIdAsync(TitleId, Arg.Any<CancellationToken>()).Returns((Title?)null);

        var result = await CreateHandler().HandleAsync(new GetTitleSourceReferencesQuery(TitleId));

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.NotFound);
        result.FirstError.Code.ShouldBe("Catalog.TitleNotFound");
        await _referenceReader.DidNotReceive().GetReferencesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_BackedTitle_ReturnsReaderReferences()
    {
        _titleRepository.GetByIdAsync(TitleId, Arg.Any<CancellationToken>()).Returns(CreateTitle());
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
