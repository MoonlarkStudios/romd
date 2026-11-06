using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;

namespace Romd.Consumer.Application.Activity.Commands.DeletePlaySession;

public sealed record DeletePlaySessionCommand(Guid SessionId) : ICommand<Deleted>;

public sealed class DeletePlaySessionCommandHandler(ICurrentUser currentUser, IPlayActivityRepository repository)
    : ICommandHandler<DeletePlaySessionCommand, Deleted>
{
    public async Task<ErrorOr<Deleted>> HandleAsync(DeletePlaySessionCommand command, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        await repository.DeleteAsync(userId, command.SessionId, ct);
        return Result.Deleted;
    }
}
