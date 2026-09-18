using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Browse;
using CommonModels = Romd.Contracts.Common.Models;

namespace Romd.Consumer.Application.Browse.Queries.ListConsumerPlatforms;

public sealed record ListConsumerPlatformsQuery(
    string? Cursor,
    int Limit) : IQuery<CommonModels.Page<ConsumerPlatformSummaryDto>>;

public sealed class ListConsumerPlatformsQueryHandler(
    ICurrentUser currentUser,
    IConsumerBrowseRepository browseRepository)
    : IQueryHandler<ListConsumerPlatformsQuery, CommonModels.Page<ConsumerPlatformSummaryDto>>
{
    public async Task<ErrorOr<CommonModels.Page<ConsumerPlatformSummaryDto>>> HandleAsync(
        ListConsumerPlatformsQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var result = await browseRepository.ListPlatformsAsync(
            new ConsumerLibraryScope(userId),
            query.Cursor,
            query.Limit,
            ct);

        return result switch
        {
            ConsumerLibraryReadResult<PagedList<ConsumerPlatformSummaryData>>.Found found =>
                found.Value.ToContract(),
            ConsumerLibraryReadResult<PagedList<ConsumerPlatformSummaryData>>.ItemNotFound =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<PagedList<ConsumerPlatformSummaryData>>.LibraryUnavailable =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<PagedList<ConsumerPlatformSummaryData>>.ProjectionInconsistent =>
                ConsumerErrors.LibraryNotFound(),
            _ => throw new InvalidOperationException("Unknown Consumer Library platform-list result.")
        };
    }
}
