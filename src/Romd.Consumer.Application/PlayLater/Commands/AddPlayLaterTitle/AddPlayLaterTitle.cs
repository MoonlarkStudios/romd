using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;

namespace Romd.Consumer.Application.PlayLater.Commands.AddPlayLaterTitle;

public sealed record AddPlayLaterTitleCommand(Sqid TitleId) : ICommand<Success>;

public sealed class AddPlayLaterTitleCommandHandler(
    ICurrentUser currentUser, IPlayLaterRepository repository, IPlayLaterTransaction transaction,
    TimeProvider timeProvider)
    : ICommandHandler<AddPlayLaterTitleCommand, Success>
{
    public async Task<ErrorOr<Success>> HandleAsync(AddPlayLaterTitleCommand command, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return ConsumerErrors.CurrentUserRequired();

        return await transaction.ExecuteAsync<Success>(async token =>
        {
            bool available = await repository.StagePlayLaterMembershipAsync(
                new(userId), command.TitleId, timeProvider.GetUtcNow(), token);
            return available ? Result.Success : ConsumerErrors.TitleNotFound();
        }, ct);
    }
}
