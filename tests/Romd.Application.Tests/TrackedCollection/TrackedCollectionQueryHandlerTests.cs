using NSubstitute;
using Romd.Admin.Application.TrackedCollection;
using Romd.Admin.Application.TrackedCollection.Queries.GetTrackedCollectionStats;
using Romd.Admin.Application.TrackedCollection.Queries.ListSatisfiedTrackedTitles;
using Romd.Admin.Application.TrackedCollection.ReadModels;
using Romd.Application.Common.Ids;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.TrackedCollection;

public sealed class TrackedCollectionQueryHandlerTests
{
    [Fact]
    public async Task ListSatisfied_HandleAsync_MapsRepositoryReadModelAndUsesSatisfiedView()
    {
        var satisfiedAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var repository = Substitute.For<ITrackedCollectionReadRepository>();
        repository.ListAsync(TrackedCollectionView.Satisfied, Arg.Any<CancellationToken>())
            .Returns([
                new TrackedCollectionTitleData
                {
                    TitleId = 10,
                    PlatformId = 2,
                    PlatformName = "Super Nintendo",
                    TitleName = "Chrono Trigger",
                    IsSatisfied = true,
                    HasUpgrade = true,
                    IsPinned = false,
                    SatisfiedAt = satisfiedAt,
                    DesiredRelease = new TrackedCollectionReleaseData(12, "Good dump", "USA", "Rev 2"),
                    OwnedRelease = new TrackedCollectionReleaseData(11, "Bad dump", "USA", "Rev 1")
                }
            ]);
        var handler = new ListSatisfiedTrackedTitlesQueryHandler(TestSystemCatalog.Create(), repository);

        var result = await handler.HandleAsync(new ListSatisfiedTrackedTitlesQuery());

        result.IsError.ShouldBeFalse();
        var title = result.Value.ShouldHaveSingleItem();
        title.TitleId.ShouldBe(IdCoder.Encode(10));
        title.SatisfiedAt.ShouldBe(satisfiedAt);
        title.DesiredRelease!.CatalogReleaseId.ShouldBe(IdCoder.Encode(12));
        title.OwnedRelease!.CatalogReleaseId.ShouldBe(IdCoder.Encode(11));
        await repository.Received(1)
            .ListAsync(TrackedCollectionView.Satisfied, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStats_HandleAsync_MapsPlatformIdentifiers()
    {
        var repository = Substitute.For<ITrackedCollectionReadRepository>();
        repository.GetStatsAsync(Arg.Any<CancellationToken>()).Returns(new TrackedCollectionStatsData
        {
            TrackedTitleCount = 2,
            SatisfiedTitleCount = 1,
            MissingTitleCount = 1,
            UpgradeTitleCount = 0,
            CompletionPercent = 50m,
            Platforms =
            [
                new TrackedCollectionPlatformStatsData
                {
                    PlatformId = 2,
                    PlatformName = "Super Nintendo",
                    TrackedTitleCount = 2,
                    SatisfiedTitleCount = 1,
                    MissingTitleCount = 1,
                    UpgradeTitleCount = 0,
                    CompletionPercent = 50m
                }
            ]
        });
        var handler = new GetTrackedCollectionStatsQueryHandler(TestSystemCatalog.Create(), repository);

        var result = await handler.HandleAsync(new GetTrackedCollectionStatsQuery());

        result.IsError.ShouldBeFalse();
        result.Value.Platforms.ShouldHaveSingleItem().SystemKey.ShouldBe(TestSystemCatalog.Keys.Required(2));
    }
}
