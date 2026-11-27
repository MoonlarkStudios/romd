using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;

namespace Romd.Consumer.Application.PlayLater.Commands.RemovePlayLaterTitle;

public sealed record RemovePlayLaterTitleCommand(Sqid TitleId) : ICommand<Deleted>;

public sealed class RemovePlayLaterTitleCommandHandler(
    ICurrentUser currentUser, IPlayLaterRepository repository, IPlayLaterTransaction transaction)
    : ICommandHandler<RemovePlayLaterTitleCommand, Deleted>
{
    public async Task<ErrorOr<Deleted>> HandleAsync(RemovePlayLaterTitleCommand command, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return ConsumerErrors.CurrentUserRequired();

        return await transaction.ExecuteAsync<Deleted>(async token =>
        {
            await repository.StagePlayLaterMembershipAsync(new(userId), command.TitleId, null, token);
            return Result.Deleted;
        }, ct);
    }
}
