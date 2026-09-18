using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;

namespace Romd.Consumer.Application.Activity.Commands.ClearPlayActivity;

public sealed record ClearPlayActivityCommand : ICommand<Deleted>;

public sealed class ClearPlayActivityCommandHandler(ICurrentUser currentUser, IPlayActivityRepository repository)
    : ICommandHandler<ClearPlayActivityCommand, Deleted>
{
    public async Task<ErrorOr<Deleted>> HandleAsync(ClearPlayActivityCommand command, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        await repository.ClearAsync(userId, ct);
        return Result.Deleted;
    }
}
