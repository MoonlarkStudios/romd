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
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Enrichment;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public class RematerializationServiceTests
{
    private readonly EnrichmentOptions _options = new() { GlobalSourcePriority = ["igdb"] };

    private readonly IPlatformFieldDefaultRepository _platformFieldDefaultRepo =
        Substitute.For<IPlatformFieldDefaultRepository>();

    private readonly ILibraryMaterializationService _materializationService =
        Substitute.For<ILibraryMaterializationService>();

    private readonly IPlatformRepository _platformRepo = Substitute.For<IPlatformRepository>();
    private readonly ITitleRepository _titleRepo = Substitute.For<ITitleRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();

    private RematerializationService CreateService()
    {
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
        return new RematerializationService(
            _titleRepo,
            _platformRepo,
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

    private static Platform CreatePlatform(int id = 10) =>
        Platform.Rehydrate(id, "Super Nintendo", "snes", "Nintendo", DateTimeOffset.UtcNow);

    [Fact]
    public async Task RematerializeTitleAsync_LoadsAndUpdatesTitle()
    {
        var title = CreateTitle(1);
        _titleRepo.GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>()).Returns(title);
        _platformFieldDefaultRepo.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        var service = CreateService();
        await service.RematerializeTitleAsync(1);

        await _titleRepo.Received(1).GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>());
        await _titleRepo.Received(1).UpdateMaterializedMetadataStagedAsync(title, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RematerializeTitleAsync_ContentRatingClaimChanged_FlagsAffectedPlatformLibraries()
    {
        var title = CreateTitle(1, contentRatings: [Rating(RatingBoard.Esrb, "E", 0)]);
        title.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Esrb, "M")]
        });

        _titleRepo.GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>()).Returns(title);
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
        });

        _titleRepo.GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>()).Returns(title);
        _platformFieldDefaultRepo.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        var service = CreateService();
        await service.RematerializeTitleAsync(1);

        title.Description.ShouldBe("Updated description");
        await _materializationService.DidNotReceive()
            .FlagAffectedLibrariesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RematerializePlatformAsync_ProcessesAllTitles()
    {
        _titleRepo.GetIdsByPlatformAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 1, 2, 3 });
        _platformFieldDefaultRepo.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        _titleRepo.GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>()).Returns(CreateTitle(1));
        _titleRepo.GetWithMetadataLayersAsync(2, Arg.Any<CancellationToken>()).Returns(CreateTitle(2));
        _titleRepo.GetWithMetadataLayersAsync(3, Arg.Any<CancellationToken>()).Returns(CreateTitle(3));

        var service = CreateService();
        await service.RematerializePlatformAsync(10);

        await _titleRepo.Received(1).UpdateMaterializedMetadataStagedAsync(Arg.Is<Title>(t => t.Id == 1), Arg.Any<CancellationToken>());
        await _titleRepo.Received(1).UpdateMaterializedMetadataStagedAsync(Arg.Is<Title>(t => t.Id == 2), Arg.Any<CancellationToken>());
        await _titleRepo.Received(1).UpdateMaterializedMetadataStagedAsync(Arg.Is<Title>(t => t.Id == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RematerializePlatformAsync_EligibilityChanges_FlagsWithinEachTitleTransaction()
    {
        var firstTitle = CreateTitle(1, contentRatings: [Rating(RatingBoard.Esrb, "E", 0)]);
        var secondTitle = CreateTitle(2, contentRatings: [Rating(RatingBoard.Esrb, "E", 0)]);
        firstTitle.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Esrb, "M")]
        });
        secondTitle.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Esrb, "M")]
        });

        _titleRepo.GetIdsByPlatformAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 1, 2 });
        _platformFieldDefaultRepo.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());
        _titleRepo.GetWithMetadataLayersAsync(1, Arg.Any<CancellationToken>()).Returns(firstTitle);
        _titleRepo.GetWithMetadataLayersAsync(2, Arg.Any<CancellationToken>()).Returns(secondTitle);

        var service = CreateService();
        await service.RematerializePlatformAsync(10);

        await _materializationService.Received(2)
            .FlagAffectedLibrariesAsync(10, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(2).BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _transaction.Received(2).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RematerializePlatformAsync_LoadsPlatformDefaultsOnce()
    {
        _titleRepo.GetIdsByPlatformAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 1, 2, 3 });
        _platformFieldDefaultRepo.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        _titleRepo.GetWithMetadataLayersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => CreateTitle((int)callInfo[0]));

        var service = CreateService();
        await service.RematerializePlatformAsync(10);

        // N+1 fix validated: platform defaults loaded exactly once, not per-title
        await _platformFieldDefaultRepo.Received(1)
            .GetByPlatformIdAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RematerializeTitleAsync_MissingTitle_NoUpdate()
    {
        _titleRepo.GetWithMetadataLayersAsync(999, Arg.Any<CancellationToken>())
            .Returns((Title?)null);

        var service = CreateService();
        await service.RematerializeTitleAsync(999);

        await _titleRepo.DidNotReceive().UpdateMaterializedMetadataStagedAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RematerializePlatformAsync_CancellationRespected()
    {
        _titleRepo.GetIdsByPlatformAsync(10, Arg.Any<CancellationToken>())
            .Returns(new List<int>
            {
                1,
                2,
                3,
                4,
                5
            });
        _platformFieldDefaultRepo.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        using var cts = new CancellationTokenSource();

        _titleRepo.GetWithMetadataLayersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => CreateTitle((int)callInfo[0]));

        // Cancellation stops the loop before starting the first title transaction.
        cts.Cancel();

        var service = CreateService();
        await Should.ThrowAsync<OperationCanceledException>(() => service.RematerializePlatformAsync(10, cts.Token));

        // Not all titles should have been processed
        await _titleRepo.DidNotReceive().UpdateMaterializedMetadataStagedAsync(
            Arg.Is<Title>(t => t.Id == 5), Arg.Any<CancellationToken>());
    }
}
