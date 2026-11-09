using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Application.Common.Pagination;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Collections;

namespace Romd.Consumer.Application.Collections.Queries.ListConsumerCollectionTitles;

public sealed record ListConsumerCollectionTitlesQuery(
    int CollectionId,
    string? Cursor = null,
    int Limit = 50) : IQuery<Page<ConsumerCollectionTitle>>
{
    /// <summary>The largest page the operation serves; the endpoint publishes and enforces it.</summary>
    public const int MaxLimit = 100;
}

public sealed class ListConsumerCollectionTitlesQueryHandler(
    ICurrentUser currentUser,
    IConsumerUserSettingsStore settingsStore,
    IConsumerCollectionReadRepository collectionRepository)
    : IQueryHandler<ListConsumerCollectionTitlesQuery, Page<ConsumerCollectionTitle>>
{
    public async Task<ErrorOr<Page<ConsumerCollectionTitle>>> HandleAsync(
        ListConsumerCollectionTitlesQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var settings = await settingsStore.GetSettingsAsync(userId, ct);
        var titles = await collectionRepository.GetCollectionTitlesPageAsync(
            new ConsumerLibraryScope(userId),
            query.CollectionId,
            query.Cursor,
            settings.ReleasePreference,
            query.Limit,
            ct);

        return titles switch
        {
            ConsumerLibraryReadResult<PagedList<ConsumerCollectionTitleReadModel>>.Found found =>
                new Page<ConsumerCollectionTitle> { Items = found.Value.Items.Select(title => title.ToDto()).ToArray(), NextCursor = found.Value.NextCursor, HasNextPage = found.Value.HasNextPage },
            ConsumerLibraryReadResult<PagedList<ConsumerCollectionTitleReadModel>>.Rejected rejected => rejected.Error,
            ConsumerLibraryReadResult<PagedList<ConsumerCollectionTitleReadModel>>.ItemNotFound =>
                ConsumerErrors.CollectionNotFound(),
            ConsumerLibraryReadResult<PagedList<ConsumerCollectionTitleReadModel>>.LibraryUnavailable =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<PagedList<ConsumerCollectionTitleReadModel>>.ProjectionInconsistent =>
                ConsumerErrors.CollectionNotFound(),
            _ => throw new InvalidOperationException("Unknown Consumer Library collection-titles result.")
        };
    }

}
