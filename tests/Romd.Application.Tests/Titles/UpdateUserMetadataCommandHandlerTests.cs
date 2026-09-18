using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.UpdateUserMetadata;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class UpdateUserMetadataCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_UserMetadataSaved_RematerializesBeforeReturningDetail()
    {
        var title = Title.Rehydrate(
            id: 1,
            platformId: 10,
            name: "Mario Is Missing!",
            normalizedName: "mario is missing",
            description: null,
            publisher: null,
            developer: null,
            genre: null,
            releaseDate: null,
            players: null,
            rating: 53.9,
            enrichmentStatus: EnrichmentStatus.Completed,
            lastEnrichedAt: DateTimeOffset.UtcNow,
            createdAt: DateTimeOffset.UtcNow);
        var titleRepository = Substitute.For<ITitleRepository>();
        var metadataRematerializer = Substitute.For<IMetadataRematerializer>();
        var libraryRepository = Substitute.For<ILibraryRepository>();
        titleRepository.GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>())
            .Returns(title);
        metadataRematerializer
            .RematerializeAsync(title, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                title.Rematerialize(["igdb"]);
                return Task.CompletedTask;
            });
        titleRepository.GetTitleDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new TitleDetailData
            {
                Id = 1,
                PlatformId = 10,
                Name = "Mario Is Missing!",
                EnrichmentStatus = "Completed",
                IsTracked = false,
                Rating = 76,
                CreatedAt = DateTimeOffset.UtcNow,
                FieldProvenance = new Dictionary<string, string> { ["Rating"] = "user" },
            });

        var handler = new UpdateUserMetadataCommandHandler(TestSystemCatalog.Create(),
            titleRepository,
            metadataRematerializer,
            libraryRepository,
            CreateUnitOfWork(),
            NullLogger<UpdateUserMetadataCommandHandler>.Instance);
        var result = await handler.HandleAsync(new UpdateUserMetadataCommand(
            TitleId: 1,
            Name: "Mario Is Missing!",
            Description: null,
            Publisher: null,
            Developer: null,
            Genre: null,
            ReleaseDate: null,
            Players: null,
            Rating: 76));

        result.IsError.ShouldBeFalse();
        result.Value.Rating.ShouldBe(76);
        result.Value.FieldProvenance.ShouldNotBeNull();
        result.Value.FieldProvenance["Rating"].ShouldBe("user");
        Received.InOrder(() =>
        {
            metadataRematerializer.RematerializeAsync(title, Arg.Any<CancellationToken>());
            titleRepository.UpdateUserMetadataStagedAsync(title, Arg.Any<CancellationToken>());
            titleRepository.GetTitleDetailAsync(1, Arg.Any<CancellationToken>());
        });
        await libraryRepository.DidNotReceive()
            .FlagForRematerializationByPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task HandleAsync_ConcurrentWrite_ReturnsConflict()
    {
        var titles = Substitute.For<ITitleRepository>();
        titles.GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Title?>(new PersistenceConflictException(
                new InvalidOperationException("private provider detail"))));
        var handler = new UpdateUserMetadataCommandHandler(TestSystemCatalog.Create(),
            titles, Substitute.For<IMetadataRematerializer>(), Substitute.For<ILibraryRepository>(),
            CreateUnitOfWork(), NullLogger<UpdateUserMetadataCommandHandler>.Instance);

        var result = await handler.HandleAsync(new UpdateUserMetadataCommand(
            1, null, null, null, null, null, null, null, null));

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
