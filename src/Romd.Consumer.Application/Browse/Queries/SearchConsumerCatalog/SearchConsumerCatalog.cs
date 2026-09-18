using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Browse;
using CommonModels = Romd.Contracts.Common.Models;

namespace Romd.Consumer.Application.Browse.Queries.SearchConsumerCatalog;

public sealed record SearchConsumerCatalogQuery(
    string? Query,
    int? PlatformId,
    string? Genre,
    string? Completeness,
    string? SortBy,
    string? Cursor,
    int Limit) : IQuery<CommonModels.Page<ConsumerTitleCardDto>>;

public sealed class SearchConsumerCatalogQueryHandler(
    ICurrentUser currentUser,
    IConsumerUserSettingsStore settingsStore,
    IConsumerBrowseRepository browseRepository)
    : IQueryHandler<SearchConsumerCatalogQuery, CommonModels.Page<ConsumerTitleCardDto>>
{
    public async Task<ErrorOr<CommonModels.Page<ConsumerTitleCardDto>>> HandleAsync(
        SearchConsumerCatalogQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var filters = new ConsumerCatalogFilters(
            query.Query,
            query.PlatformId,
            query.Genre,
            ParseCompleteness(query.Completeness),
            ParseSortField(query.SortBy));

        var settings = await settingsStore.GetSettingsAsync(userId, ct);
        var result = await browseRepository.SearchCatalogAsync(
            new ConsumerLibraryScope(userId),
            filters,
            settings.ReleasePreference,
            query.Cursor,
            query.Limit,
            ct);

        return result switch
        {
            ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>.Found found =>
                found.Value.ToContract(),
            ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>.ItemNotFound =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>.LibraryUnavailable =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>.ProjectionInconsistent =>
                ConsumerErrors.LibraryNotFound(),
            _ => throw new InvalidOperationException("Unknown Consumer Library catalog-search result.")
        };
    }

    private static ConsumerCompletenessFilter ParseCompleteness(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "complete" => ConsumerCompletenessFilter.Complete,
            "partial" => ConsumerCompletenessFilter.Partial,
            _ => ConsumerCompletenessFilter.All
        };

    private static ConsumerTitleSortField ParseSortField(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "rating" => ConsumerTitleSortField.Rating,
            _ => ConsumerTitleSortField.Name
        };
}
