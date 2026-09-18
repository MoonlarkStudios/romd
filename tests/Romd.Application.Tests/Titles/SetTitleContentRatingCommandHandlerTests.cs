using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.SetTitleContentRating;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class SetTitleContentRatingCommandHandlerTests
{
    private static Title CreateTitle() =>
        Title.Rehydrate(
            id: 1,
            platformId: 10,
            name: "Super Mario World",
            normalizedName: "super mario world",
            description: null,
            publisher: null,
            developer: null,
            genre: null,
            releaseDate: null,
            players: null,
            rating: null,
            enrichmentStatus: EnrichmentStatus.Completed,
            lastEnrichedAt: DateTimeOffset.UtcNow,
            createdAt: DateTimeOffset.UtcNow,
            metadataLayers: []);

    private static (SetTitleContentRatingCommandHandler Handler,
        ITitleRepository TitleRepository,
        ILibraryRepository LibraryRepository) CreateHandler(Title title)
    {
        var titleRepository = Substitute.For<ITitleRepository>();
        var metadataRematerializer = Substitute.For<IMetadataRematerializer>();
        var libraryRepository = Substitute.For<ILibraryRepository>();

        titleRepository.GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>()).Returns(title);
        metadataRematerializer
            .RematerializeAsync(title, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                title.Rematerialize(["igdb"]);
                return Task.CompletedTask;
            });
        titleRepository.GetTitleDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => new TitleDetailData
            {
                Id = 1,
                PlatformId = 10,
                Name = "Super Mario World",
                EnrichmentStatus = "Completed",
                IsTracked = false,
                CreatedAt = DateTimeOffset.UtcNow,
                ContentRatings = title.ContentRatings
                    .Select(r => new TitleContentRatingData
                    {
                        Board = (int)r.Board,
                        Code = r.Code,
                        Designation = (int)r.Designation,
                        MinimumAge = r.MinimumAge,
                        SourceId = r.SourceId
                    })
                    .ToList()
            });

        var handler = new SetTitleContentRatingCommandHandler(TestSystemCatalog.Create(),
            titleRepository,
            metadataRematerializer,
            libraryRepository,
            CreateUnitOfWork(),
            NullLogger<SetTitleContentRatingCommandHandler>.Instance);

        return (handler, titleRepository, libraryRepository);
    }

    [Fact]
    public async Task HandleAsync_ValidCode_StoresUserRatingAndFlagsRematerialization()
    {
        var title = CreateTitle();
        var (handler, titleRepository, libraryRepository) = CreateHandler(title);

        var result = await handler.HandleAsync(new SetTitleContentRatingCommand(
            TitleId: 1, Board: RatingBoard.Esrb, Code: "E10+", Descriptors: null, Synopsis: null));

        result.IsError.ShouldBeFalse();
        result.Value.ContentRatings.ShouldContain(r => r.Board == (int)RatingBoard.Esrb && r.Code == "E10+");
        title.ContentRatings.Single(r => r.Board == RatingBoard.Esrb).SourceId.ShouldBe("user");
        await titleRepository.Received(1).UpdateUserMetadataStagedAsync(title, Arg.Any<CancellationToken>());
        await libraryRepository.Received(1)
            .FlagForRematerializationByPlatformAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UnrecognizedCode_ReturnsValidationErrorAndDoesNotSave()
    {
        var title = CreateTitle();
        var (handler, titleRepository, _) = CreateHandler(title);

        var result = await handler.HandleAsync(new SetTitleContentRatingCommand(
            TitleId: 1, Board: RatingBoard.Pegi, Code: "NOT A RATING", Descriptors: null, Synopsis: null));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.InvalidContentRating");
        await titleRepository.DidNotReceive().UpdateUserMetadataStagedAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_BlankCode_ClearsUserRating()
    {
        var title = CreateTitle();
        title.SetUserContentRating(new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "M" });
        var (handler, titleRepository, _) = CreateHandler(title);

        var result = await handler.HandleAsync(new SetTitleContentRatingCommand(
            TitleId: 1, Board: RatingBoard.Esrb, Code: null, Descriptors: null, Synopsis: null));

        result.IsError.ShouldBeFalse();
        title.ContentRatings.ShouldBeEmpty();
        await titleRepository.Received(1).UpdateUserMetadataStagedAsync(title, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_TitleNotFound_ReturnsNotFound()
    {
        var titleRepository = Substitute.For<ITitleRepository>();
        titleRepository.GetWithMetadataLayersAsync(99, Arg.Any<CancellationToken>())
            .Returns((Title?)null);
        var handler = new SetTitleContentRatingCommandHandler(TestSystemCatalog.Create(),
            titleRepository,
            Substitute.For<IMetadataRematerializer>(),
            Substitute.For<ILibraryRepository>(),
            CreateUnitOfWork(),
            NullLogger<SetTitleContentRatingCommandHandler>.Instance);

        var result = await handler.HandleAsync(new SetTitleContentRatingCommand(
            TitleId: 99, Board: RatingBoard.Esrb, Code: "E", Descriptors: null, Synopsis: null));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.TitleNotFound");
    }
    [Fact]
    public async Task HandleAsync_ConcurrentWrite_ReturnsConflict()
    {
        var title = CreateTitle();
        var (handler, titleRepository, _) = CreateHandler(title);
        titleRepository.UpdateUserMetadataStagedAsync(title, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new PersistenceConflictException(
                new InvalidOperationException("private provider detail"))));

        var result = await handler.HandleAsync(new SetTitleContentRatingCommand(
            1, RatingBoard.Esrb, "M", null, null));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Catalog.TitleConflict");
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Conflict);
    }

    private static IUnitOfWork CreateUnitOfWork()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>())
            .Returns(Substitute.For<ITransaction>());
        return unitOfWork;
    }

}
