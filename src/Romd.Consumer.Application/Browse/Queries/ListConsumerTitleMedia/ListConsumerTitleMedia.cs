using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Browse;

namespace Romd.Consumer.Application.Browse.Queries.ListConsumerTitleMedia;

public sealed record ListConsumerTitleMediaQuery(int TitleId, string? Cursor, int Limit) : IQuery<Page<ConsumerMediaRefDto>>
{
    public const int MaxLimit = 100;
}

public sealed class ListConsumerTitleMediaQueryHandler(ICurrentUser currentUser, IConsumerBrowseRepository repository)
    : IQueryHandler<ListConsumerTitleMediaQuery, Page<ConsumerMediaRefDto>>
{
    public async Task<ErrorOr<Page<ConsumerMediaRefDto>>> HandleAsync(ListConsumerTitleMediaQuery query, CancellationToken ct = default)
    {
        if (currentUser.UserId is not { } userId)
            return ConsumerErrors.CurrentUserRequired();
        var result = await repository.ListTitleMediaAsync(new(userId), query.TitleId, query.Cursor, query.Limit, ct);
        return result switch
        {
            ConsumerLibraryReadResult<PagedList<ConsumerMediaData>>.Found found => new Page<ConsumerMediaRefDto>
            {
                Items = found.Value.Items.Select(item => item.ToContract()).ToArray(),
                NextCursor = found.Value.NextCursor,
                HasNextPage = found.Value.HasNextPage
            },
            ConsumerLibraryReadResult<PagedList<ConsumerMediaData>>.Rejected rejected => rejected.Error,
            _ => ConsumerErrors.TitleNotFound()
        };
    }
}
