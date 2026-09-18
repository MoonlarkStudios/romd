using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Collections;

namespace Romd.Consumer.Application.Collections.Queries.ListConsumerCollectionTitles;

public sealed record ListConsumerCollectionTitlesQuery(
    int CollectionId,
    string? Cursor = null,
    int Limit = 50) : IQuery<Page<ConsumerCollectionTitleDto>>;

public sealed class ListConsumerCollectionTitlesQueryHandler(
    ICurrentUser currentUser,
    IConsumerUserSettingsStore settingsStore,
    IConsumerCollectionReadRepository collectionRepository)
    : IQueryHandler<ListConsumerCollectionTitlesQuery, Page<ConsumerCollectionTitleDto>>
{
    private const int MaxLimit = 100;

    public async Task<ErrorOr<Page<ConsumerCollectionTitleDto>>> HandleAsync(
        ListConsumerCollectionTitlesQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        ConsumerCollectionTitleCursor? cursor = null;
        if (query.Cursor is not null)
        {
            if (!ConsumerCollectionMapping.TryDecodeCursor(query.Cursor, out var parsedCursor))
            {
                return ConsumerErrors.InvalidCursor();
            }

            cursor = parsedCursor;
        }

        int limit = Math.Clamp(query.Limit, 1, MaxLimit);
        var settings = await settingsStore.GetSettingsAsync(userId, ct);
        var titles = await collectionRepository.GetCollectionTitlesAsync(
            new ConsumerLibraryScope(userId),
            query.CollectionId,
            cursor,
            settings.ReleasePreference,
            limit + 1,
            ct);

        return titles switch
        {
            ConsumerLibraryReadResult<IReadOnlyList<ConsumerCollectionTitleReadModel>>.Found found =>
                ToPage(found.Value, limit),
            ConsumerLibraryReadResult<IReadOnlyList<ConsumerCollectionTitleReadModel>>.ItemNotFound =>
                ConsumerErrors.CollectionNotFound(),
            ConsumerLibraryReadResult<IReadOnlyList<ConsumerCollectionTitleReadModel>>.LibraryUnavailable =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<IReadOnlyList<ConsumerCollectionTitleReadModel>>.ProjectionInconsistent =>
                ConsumerErrors.CollectionNotFound(),
            _ => throw new InvalidOperationException("Unknown Consumer Library collection-titles result.")
        };
    }

    private static Page<ConsumerCollectionTitleDto> ToPage(
        IReadOnlyList<ConsumerCollectionTitleReadModel> titles,
        int limit)
    {
        bool hasNextPage = titles.Count > limit;
        var pageItems = titles.Take(limit).ToList();

        return new Page<ConsumerCollectionTitleDto>
        {
            Items = pageItems.Select(title => title.ToDto()).ToList(),
            HasNextPage = hasNextPage,
            NextCursor = hasNextPage ? ConsumerCollectionMapping.EncodeCursor(pageItems[^1]) : null
        };
    }
}
