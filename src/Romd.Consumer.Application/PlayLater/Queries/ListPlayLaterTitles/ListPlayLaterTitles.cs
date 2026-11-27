using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Browse;

namespace Romd.Consumer.Application.PlayLater.Queries.ListPlayLaterTitles;

public sealed record ListPlayLaterTitlesQuery(string? Cursor, int Limit) : IQuery<Page<ConsumerTitleCard>>
{
    public const int MaxLimit = 100;
}

public sealed class ListPlayLaterTitlesQueryHandler(
    ICurrentUser currentUser,
    IConsumerUserSettingsStore settingsStore,
    IPlayLaterRepository repository) : IQueryHandler<ListPlayLaterTitlesQuery, Page<ConsumerTitleCard>>
{
    public async Task<ErrorOr<Page<ConsumerTitleCard>>> HandleAsync(
        ListPlayLaterTitlesQuery query, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return ConsumerErrors.CurrentUserRequired();

        var settings = await settingsStore.GetSettingsAsync(userId, ct);
        var result = await repository.ListPlayLaterTitlesAsync(
            new(userId), settings.ReleasePreference, query.Cursor, query.Limit, ct);
        return result switch
        {
            ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>.Found found => found.Value.ToContract(),
            ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>.Rejected rejected => rejected.Error,
            ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>.LibraryUnavailable when query.Cursor is null =>
                new Page<ConsumerTitleCard> { Items = [], HasNextPage = false },
            ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>.LibraryUnavailable =>
                ConsumerErrors.InvalidCursor(),
            _ => throw new InvalidOperationException("Unknown Play Later title list result.")
        };
    }
}
