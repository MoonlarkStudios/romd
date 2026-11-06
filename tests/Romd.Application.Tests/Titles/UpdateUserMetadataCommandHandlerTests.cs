using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.UpdateUserMetadata;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Concurrency;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
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
        var titleRepository = Substitute.For<ITitleMetadataStore>();
        var titleRepositoryAggregates = Substitute.For<ITitleAggregateReader>();
        var titleRepositoryDetails = Substitute.For<ITitleDetailReader>();
        var metadataRematerializer = Substitute.For<IMetadataRematerializer>();
        var libraryRepository = Substitute.For<ILibraryRepository>();
        titleRepositoryAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>())
            .Returns(title);
        metadataRematerializer
            .RematerializeAsync(title, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                title.Rematerialize(["igdb"]);
                return Task.CompletedTask;
            });
        titleRepositoryDetails.GetDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new TitleDetailData
            {
                Id = 1,
                Revision = Guid.Empty,
                PlatformId = 10,
                Name = "Mario Is Missing!",
                EnrichmentStatus = "Completed",
                IsTracked = false,
                Rating = 76,
                CreatedAt = DateTimeOffset.UtcNow,
                FieldProvenance = new Dictionary<string, string> { ["Rating"] = "user" },
            });

        var handler = new UpdateUserMetadataCommandHandler(TestSystemCatalog.Create(),
            titleRepositoryAggregates,
            titleRepository, titleRepositoryDetails,
            metadataRematerializer,
            libraryRepository,
            CreateUnitOfWork(),
            NullLogger<UpdateUserMetadataCommandHandler>.Instance, clock: TimeProvider.System);
        var result = await handler.HandleAsync(new UpdateUserMetadataCommand(
            TitleId: 1,
            Values: new TitleMetadataPayload { Name = "Mario Is Missing!", Rating = 76 },
            Fields: TitleMetadataFields.Name | TitleMetadataFields.Rating,
            IfMatch: RevisionPrecondition.For(title.Revision.ToString())));

        result.IsError.ShouldBeFalse();
        result.Value.Rating.ShouldBe(76);
        result.Value.FieldProvenance.ShouldNotBeNull();
        result.Value.FieldProvenance["Rating"].ShouldBe("user");
        Received.InOrder(() =>
        {
            metadataRematerializer.RematerializeAsync(title, Arg.Any<CancellationToken>());
            titleRepository.UpdateUserMetadataStagedAsync(title, Arg.Any<CancellationToken>());
            titleRepositoryDetails.GetDetailAsync(1, Arg.Any<CancellationToken>());
        });
        await libraryRepository.DidNotReceive()
            .FlagForRematerializationByPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task HandleAsync_ConcurrentWrite_ReturnsPreconditionFailed()
    {
        var titles = Substitute.For<ITitleMetadataStore>();
        var titlesAggregates = Substitute.For<ITitleAggregateReader>();
        var titlesDetails = Substitute.For<ITitleDetailReader>();
        titlesAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Title?>(new PersistenceConflictException(
                new InvalidOperationException("private provider detail"))));
        var handler = new UpdateUserMetadataCommandHandler(TestSystemCatalog.Create(),
            titlesAggregates,
            titles, titlesDetails, Substitute.For<IMetadataRematerializer>(), Substitute.For<ILibraryRepository>(),
            CreateUnitOfWork(), NullLogger<UpdateUserMetadataCommandHandler>.Instance, clock: TimeProvider.System);

        var result = await handler.HandleAsync(new UpdateUserMetadataCommand(
            1, new TitleMetadataPayload(), TitleMetadataFields.None, RevisionPrecondition.For(Guid.Empty.ToString())));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Titles.PreconditionFailed");
        ((int)result.FirstError.Type).ShouldBe(412);
    }

    [Fact]
    public async Task HandleAsync_StaleIfMatch_ReturnsPreconditionFailedWithoutWriting()
    {
        var title = Title.Rehydrate(1, 10, "Mario Is Missing!", "mario is missing", null, null, null, null, null, null, null,
            EnrichmentStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, revision: Guid.NewGuid());
        var titles = Substitute.For<ITitleMetadataStore>();
        var titlesAggregates = Substitute.For<ITitleAggregateReader>();
        var titlesDetails = Substitute.For<ITitleDetailReader>();
        var rematerializer = Substitute.For<IMetadataRematerializer>();
        titlesAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(title);
        var unitOfWork = CreateUnitOfWork();
        var handler = new UpdateUserMetadataCommandHandler(TestSystemCatalog.Create(),
            titlesAggregates,
            titles, titlesDetails, rematerializer, Substitute.For<ILibraryRepository>(),
            unitOfWork, NullLogger<UpdateUserMetadataCommandHandler>.Instance, clock: TimeProvider.System);

        var result = await handler.HandleAsync(new UpdateUserMetadataCommand(
            1, new TitleMetadataPayload { Genre = "Adventure" }, TitleMetadataFields.Genre,
            RevisionPrecondition.For(Guid.NewGuid().ToString())));

        result.FirstError.Code.ShouldBe("Titles.PreconditionFailed");
        ((int)result.FirstError.Type).ShouldBe(412);
        title.GetMetadataLayer("user").ShouldBeNull();
        await rematerializer.DidNotReceive().RematerializeAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>());
        await titles.DidNotReceive().UpdateUserMetadataStagedAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>());
        await unitOfWork.DidNotReceive().FlushAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CurrentIfMatch_Saves()
    {
        var revision = Guid.NewGuid();
        var title = Title.Rehydrate(1, 10, "Mario Is Missing!", "mario is missing", null, null, null, null, null, null, null,
            EnrichmentStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, revision: revision);
        var titles = Substitute.For<ITitleMetadataStore>();
        var titlesAggregates = Substitute.For<ITitleAggregateReader>();
        var titlesDetails = Substitute.For<ITitleDetailReader>();
        titlesAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(title);
        titlesDetails.GetDetailAsync(1, Arg.Any<CancellationToken>()).Returns(new TitleDetailData
        {
            Id = 1,
            Revision = Guid.Empty,
            PlatformId = 10,
            Name = "Mario Is Missing!",
            EnrichmentStatus = "Completed",
            IsTracked = false,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var handler = new UpdateUserMetadataCommandHandler(TestSystemCatalog.Create(),
            titlesAggregates,
            titles, titlesDetails, Substitute.For<IMetadataRematerializer>(), Substitute.For<ILibraryRepository>(),
            CreateUnitOfWork(), NullLogger<UpdateUserMetadataCommandHandler>.Instance, clock: TimeProvider.System);

        var result = await handler.HandleAsync(new UpdateUserMetadataCommand(
            1, new TitleMetadataPayload { Genre = "Adventure" }, TitleMetadataFields.Genre,
            RevisionPrecondition.For(revision.ToString())));

        result.IsError.ShouldBeFalse();
        await titles.Received(1).UpdateUserMetadataStagedAsync(title, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_PartialUpdate_ChangesOnlyTheSuppliedFieldsOfTheUserLayer()
    {
        var revision = Guid.NewGuid();
        var title = Title.Rehydrate(1, 10, "Provider Name", "provider name", null, null, null, null, null, null, null,
            EnrichmentStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, revision: revision);
        title.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Name = "Provider Name",
            Description = "Provider description",
            Publisher = "Provider Publisher",
            Genre = "Platformer"
        }, updatedAt: DateTimeOffset.UnixEpoch);
        title.StoreUserLayer(new TitleMetadataPayload
        {
            Name = "User Name",
            Description = "User description",
            Publisher = "User Publisher",
            Genre = "Puzzle",
            ContentRatings = [new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "T" }]
        }, updatedAt: DateTimeOffset.UnixEpoch);
        var titles = Substitute.For<ITitleMetadataStore>();
        var aggregates = Substitute.For<ITitleAggregateReader>();
        var details = Substitute.For<ITitleDetailReader>();
        var rematerializer = Substitute.For<IMetadataRematerializer>();
        aggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(title);
        rematerializer.RematerializeAsync(title, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            title.Rematerialize(["igdb"]);
            return Task.CompletedTask;
        });
        details.GetDetailAsync(1, Arg.Any<CancellationToken>()).Returns(new TitleDetailData
        {
            Id = 1,
            Revision = Guid.Empty,
            PlatformId = 10,
            Name = "User Name",
            EnrichmentStatus = "Completed",
            IsTracked = false,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var handler = new UpdateUserMetadataCommandHandler(TestSystemCatalog.Create(), aggregates, titles, details,
            rematerializer, Substitute.For<ILibraryRepository>(), CreateUnitOfWork(),
            NullLogger<UpdateUserMetadataCommandHandler>.Instance, clock: TimeProvider.System);

        // The genre is set, the publisher override is cleared, and nothing else is supplied.
        var result = await handler.HandleAsync(new UpdateUserMetadataCommand(1,
            new TitleMetadataPayload { Genre = "Adventure" },
            TitleMetadataFields.Genre | TitleMetadataFields.Publisher,
            RevisionPrecondition.For(revision.ToString())));

        result.IsError.ShouldBeFalse();
        var user = title.GetMetadataLayer("user").ShouldNotBeNull().GetPayload().ShouldNotBeNull();
        user.Genre.ShouldBe("Adventure");
        user.Publisher.ShouldBeNull();
        user.Name.ShouldBe("User Name");
        user.Description.ShouldBe("User description");
        var kept = user.ContentRatings.ShouldNotBeNull().ShouldHaveSingleItem();
        (kept.Board, kept.RawCode).ShouldBe((RatingBoard.Esrb, "T"));
        title.Description.ShouldBe("User description");
        title.Publisher.ShouldBe("Provider Publisher");
        title.Genre.ShouldBe("Adventure");
    }

    private static IUnitOfWork CreateUnitOfWork()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>())
            .Returns(Substitute.For<ITransaction>());
        return unitOfWork;
    }

}
