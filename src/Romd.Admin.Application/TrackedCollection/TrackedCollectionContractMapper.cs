using Romd.Application.Common.Systems;
using Romd.Admin.Application.TrackedCollection.ReadModels;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.TrackedCollection;

namespace Romd.Admin.Application.TrackedCollection;

internal static class TrackedCollectionContractMapper
{
    public static TrackedCollectionTitle ToContract(TrackedCollectionTitleData data, SystemKeys systemKeys) => new()
    {
        TitleId = IdCoder.Encode(data.TitleId),
        SystemKey = systemKeys.Required(data.PlatformId),
        PlatformName = data.PlatformName,
        TitleName = data.TitleName,
        IsSatisfied = data.IsSatisfied,
        HasUpgrade = data.HasUpgrade,
        IsPinned = data.IsPinned,
        SatisfiedAt = data.SatisfiedAt,
        DesiredRelease = ToContract(data.DesiredRelease),
        OwnedRelease = ToContract(data.OwnedRelease)
    };

    public static TrackedCollectionStats ToContract(TrackedCollectionStatsData data, SystemKeys systemKeys) => new()
    {
        TrackedTitleCount = data.TrackedTitleCount,
        SatisfiedTitleCount = data.SatisfiedTitleCount,
        MissingTitleCount = data.MissingTitleCount,
        UpgradeTitleCount = data.UpgradeTitleCount,
        CompletionPercent = data.CompletionPercent,
        Platforms = data.Platforms.Select(platform => new TrackedCollectionPlatformStats
        {
            SystemKey = systemKeys.Required(platform.PlatformId),
            PlatformName = platform.PlatformName,
            TrackedTitleCount = platform.TrackedTitleCount,
            SatisfiedTitleCount = platform.SatisfiedTitleCount,
            MissingTitleCount = platform.MissingTitleCount,
            UpgradeTitleCount = platform.UpgradeTitleCount,
            CompletionPercent = platform.CompletionPercent
        }).ToList()
    };

    private static TrackedCollectionRelease? ToContract(TrackedCollectionReleaseData? release) =>
        release is null
            ? null
            : new TrackedCollectionRelease
            {
                CatalogReleaseId = IdCoder.Encode(release.CatalogReleaseId),
                Name = release.Name,
                Region = release.Region,
                Revision = release.Revision
            };
}
