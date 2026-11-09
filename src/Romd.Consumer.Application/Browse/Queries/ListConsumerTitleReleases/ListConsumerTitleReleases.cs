using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Releases;

namespace Romd.Consumer.Application.Browse.Queries.ListConsumerTitleReleases;

public sealed record ListConsumerTitleReleasesQuery(int TitleId, string? Cursor, int Limit) : IQuery<Page<ConsumerRelease>>
{
    public const int MaxLimit = 100;
}

public sealed class ListConsumerTitleReleasesQueryHandler(ICurrentUser currentUser, IConsumerBrowseRepository repository)
    : IQueryHandler<ListConsumerTitleReleasesQuery, Page<ConsumerRelease>>
{
    public async Task<ErrorOr<Page<ConsumerRelease>>> HandleAsync(ListConsumerTitleReleasesQuery query, CancellationToken ct = default)
    {
        if (currentUser.UserId is not { } userId)
            return ConsumerErrors.CurrentUserRequired();
        var result = await repository.ListTitleReleasesAsync(new(userId), query.TitleId, query.Cursor, query.Limit, ct);
        return result switch
        {
            ConsumerLibraryReadResult<PagedList<ConsumerReleaseData>>.Found found => new Page<ConsumerRelease>
            {
                Items = found.Value.Items.Select(item => item.ToContract()).ToArray(),
                NextCursor = found.Value.NextCursor,
                HasNextPage = found.Value.HasNextPage
            },
            ConsumerLibraryReadResult<PagedList<ConsumerReleaseData>>.Rejected rejected => rejected.Error,
            _ => ConsumerErrors.TitleNotFound()
        };
    }
}
