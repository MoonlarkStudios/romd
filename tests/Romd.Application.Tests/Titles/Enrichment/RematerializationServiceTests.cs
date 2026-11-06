using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Source.Platform;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles.Enrichment;

public class RematerializationServiceTests
{
    private readonly EnrichmentOptions _options = new() { GlobalSourcePriority = ["igdb"] };

    private readonly IPlatformFieldDefaultRepository _platformFieldDefaultRepo =
        Substitute.For<IPlatformFieldDefaultRepository>();

    private readonly ILibraryMaterializationService _materializationService =
        Substitute.For<ILibraryMaterializationService>();

    private readonly ITitleMetadataStore _titleRepo = Substitute.For<ITitleMetadataStore>();
    private readonly ITitleAggregateReader _titleRepoAggregates = Substitute.For<ITitleAggregateReader>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();

    private RematerializationService CreateService()
    {
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
        return new RematerializationService(
            _titleRepoAggregates,
            _titleRepo,
            _platformFieldDefaultRepo,
            _materializationService,
            _unitOfWork,
            Options.Create(_options),
            NullLogger<RematerializationService>.Instance);
    }

    private static Title CreateTitle(
        int id,
        int platformId = 10,
        string? genre = null,
        IReadOnlyList<ContentRating>? contentRatings = null)
    {
        return Title.Rehydrate(
            id, platformId, $"Title {id}",
            $"title {id}",
            null, null, null, genre,
            null, null, null,
            EnrichmentStatus.Completed, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            contentRatings: contentRatings,
            conservativeMinimumAge: contentRatings?
                .Where(r => r.Designation == RatingDesignation.Rated)
                .Max(r => r.MinimumAge));
    }

    private static ContentRating Rating(RatingBoard board, string code, int minimumAge) =>
        new()
        {
            Board = board,
            Code = code,
            Designation = RatingDesignation.Rated,
            MinimumAge = minimumAge,
            SourceId = "igdb"
        };

    private static ContentRatingClaim Claim(RatingBoard board, string rawCode) =>
        new() { Board = board, RawCode = rawCode };

    [Fact]
    public async Task RematerializeTitleAsync_LoadsAndUpdatesTitle()
    {
        var title = CreateTitle(1);
        _titleRepoAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(title);
        _platformFieldDefaultRepo.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        var service = CreateService();
        await service.RematerializeTitleAsync(1);

        await _titleRepoAggregates.Received(1).GetAggregateAsync(1, Arg.Any<CancellationToken>());
        await _titleRepo.Received(1).UpdateMaterializedMetadataStagedAsync(title, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RematerializeTitleAsync_ContentRatingClaimChanged_FlagsAffectedPlatformLibraries()
    {
        var title = CreateTitle(1, contentRatings: [Rating(RatingBoard.Esrb, "E", 0)]);
        title.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Esrb, "M")]
        }, updatedAt: DateTimeOffset.UnixEpoch);

        _titleRepoAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(title);
        _platformFieldDefaultRepo.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        var service = CreateService();
        await service.RematerializeTitleAsync(1);

        title.ContentRatings.Single(r => r.Board == RatingBoard.Esrb).Code.ShouldBe("M");
        await _materializationService.Received(1)
            .FlagAffectedLibrariesAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RematerializeTitleAsync_CosmeticOnlyChange_DoesNotFlagLibraries()
    {
        var title = CreateTitle(1, genre: "Action", contentRatings: [Rating(RatingBoard.Esrb, "E", 0)]);
        title.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "Updated description",
            Genre = "Action",
            ContentRatings = [Claim(RatingBoard.Esrb, "E")]
        }, updatedAt: DateTimeOffset.UnixEpoch);

        _titleRepoAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(title);
        _platformFieldDefaultRepo.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        var service = CreateService();
        await service.RematerializeTitleAsync(1);

        title.Description.ShouldBe("Updated description");
        await _materializationService.DidNotReceive()
            .FlagAffectedLibrariesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RematerializeTitleAsync_IndependentCallsPersistEachTitle()
    {
        _platformFieldDefaultRepo.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        _titleRepoAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(CreateTitle(1));
        _titleRepoAggregates.GetAggregateAsync(2, Arg.Any<CancellationToken>()).Returns(CreateTitle(2));
        _titleRepoAggregates.GetAggregateAsync(3, Arg.Any<CancellationToken>()).Returns(CreateTitle(3));

        var service = CreateService();
        await service.RematerializeTitleAsync(1);
        await service.RematerializeTitleAsync(2);
        await service.RematerializeTitleAsync(3);

        await _titleRepo.Received(1).UpdateMaterializedMetadataStagedAsync(Arg.Is<Title>(t => t.Id == 1), Arg.Any<CancellationToken>());
        await _titleRepo.Received(1).UpdateMaterializedMetadataStagedAsync(Arg.Is<Title>(t => t.Id == 2), Arg.Any<CancellationToken>());
        await _titleRepo.Received(1).UpdateMaterializedMetadataStagedAsync(Arg.Is<Title>(t => t.Id == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RematerializeTitleAsync_EligibilityChanges_FlagsWithinEachTitleTransaction()
    {
        var firstTitle = CreateTitle(1, contentRatings: [Rating(RatingBoard.Esrb, "E", 0)]);
        var secondTitle = CreateTitle(2, contentRatings: [Rating(RatingBoard.Esrb, "E", 0)]);
        firstTitle.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Esrb, "M")]
        }, updatedAt: DateTimeOffset.UnixEpoch);
        secondTitle.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Esrb, "M")]
        }, updatedAt: DateTimeOffset.UnixEpoch);

        _platformFieldDefaultRepo.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());
        _titleRepoAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(firstTitle);
        _titleRepoAggregates.GetAggregateAsync(2, Arg.Any<CancellationToken>()).Returns(secondTitle);

        var service = CreateService();
        await service.RematerializeTitleAsync(1);
        await service.RematerializeTitleAsync(2);

        await _materializationService.Received(2)
            .FlagAffectedLibrariesAsync(10, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(2).BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _transaction.Received(2).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RematerializeTitleAsync_IndependentCallsReadCurrentDefaultsInsteadOfReusingAPolicySnapshot()
    {
        var first = CreateTitle(1);
        var second = CreateTitle(2);
        foreach (var title in new[] { first, second })
        {
            title.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload { Description = "Provider description" }, updatedAt: DateTimeOffset.UnixEpoch);
            title.StoreProviderLayer("dat", MetadataSourceType.Parsing, new TitleMetadataPayload { Description = "DAT description" }, updatedAt: DateTimeOffset.UnixEpoch);
        }
        _titleRepoAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(first);
        _titleRepoAggregates.GetAggregateAsync(2, Arg.Any<CancellationToken>()).Returns(second);
        _platformFieldDefaultRepo.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>()).Returns(
            new Dictionary<string, string> { ["Description"] = "igdb" },
            new Dictionary<string, string> { ["Description"] = "dat" });
        var service = CreateService();
        await service.RematerializeTitleAsync(1);
        await service.RematerializeTitleAsync(2);
        first.Description.ShouldBe("Provider description");
        second.Description.ShouldBe("DAT description");
        await _platformFieldDefaultRepo.Received(2).GetByPlatformIdAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RematerializeTitleAsync_MissingTitle_NoUpdate()
    {
        _titleRepoAggregates.GetAggregateAsync(999, Arg.Any<CancellationToken>())
            .Returns((Title?)null);

        var service = CreateService();
        await service.RematerializeTitleAsync(999);

        await _titleRepo.DidNotReceive().UpdateMaterializedMetadataStagedAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RematerializeTitleAsync_CancelledBeforeWork_StartsNoTransactionOrWrite()
    {
        var service = CreateService();
        await Should.ThrowAsync<OperationCanceledException>(() => service.RematerializeTitleAsync(1, new CancellationToken(true)));
        await _unitOfWork.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _titleRepo.DidNotReceive().UpdateMaterializedMetadataStagedAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>());
    }
}
