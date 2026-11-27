using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.PlayLater;

namespace Romd.Consumer.Application.PlayLater.Queries.GetPlayLaterMembership;

public sealed record GetPlayLaterMembershipQuery(IReadOnlyList<Sqid> TitleIds) : IQuery<ConsumerPlayLaterMembership>
{
    public const int MaxTitleIds = 100;
}

public sealed class GetPlayLaterMembershipQueryHandler(ICurrentUser currentUser, IPlayLaterRepository repository)
    : IQueryHandler<GetPlayLaterMembershipQuery, ConsumerPlayLaterMembership>
{
    public async Task<ErrorOr<ConsumerPlayLaterMembership>> HandleAsync(
        GetPlayLaterMembershipQuery query, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return ConsumerErrors.CurrentUserRequired();

        var ids = query.TitleIds.Select(id => id.Value).Distinct().ToArray();
        var result = await repository.GetPlayLaterMembershipAsync(new(userId), ids, ct);
        return result switch
        {
            ConsumerLibraryReadResult<IReadOnlyList<int>>.Found found =>
                new ConsumerPlayLaterMembership(found.Value.Select(IdCoder.Encode).ToArray()),
            ConsumerLibraryReadResult<IReadOnlyList<int>>.LibraryUnavailable => new ConsumerPlayLaterMembership([]),
            _ => throw new InvalidOperationException("Unknown Play Later membership result.")
        };
    }
}
