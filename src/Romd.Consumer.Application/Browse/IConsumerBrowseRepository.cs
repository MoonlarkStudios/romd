using Romd.Application.Common.Pagination;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;

namespace Romd.Consumer.Application.Browse;

public interface IConsumerBrowseRepository
{
    Task<ConsumerLibraryReadResult<PagedList<ConsumerPlatformSummaryData>>> ListPlatformsAsync(
        ConsumerLibraryScope scope,
        string? cursor,
        int limit,
        CancellationToken ct = default);

    Task<ConsumerLibraryReadResult<ConsumerPlatformDetailData>> GetPlatformAsync(
        ConsumerLibraryScope scope,
        int platformId,
        CancellationToken ct = default);

    Task<ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>> SearchCatalogAsync(
        ConsumerLibraryScope scope,
        ConsumerCatalogFilters filters,
        ConsumerReleasePreference releasePreference,
        string? cursor,
        int limit,
        CancellationToken ct = default);

    Task<ConsumerLibraryReadResult<ConsumerTitleDetailData>> GetTitleAsync(
        ConsumerLibraryScope scope,
        int titleId,
        ConsumerReleasePreference releasePreference,
        CancellationToken ct = default);
}
