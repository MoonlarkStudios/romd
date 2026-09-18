using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;

namespace Romd.Consumer.Application.Account.Commands.ChangeConsumerPassword;

public sealed record ChangeConsumerPasswordCommand(string CurrentPassword, string NewPassword) : ICommand<Updated>;

public sealed class ChangeConsumerPasswordCommandHandler(
    ICurrentUser currentUser,
    IConsumerPasswordChanger passwordChanger)
    : ICommandHandler<ChangeConsumerPasswordCommand, Updated>
{
    public async Task<ErrorOr<Updated>> HandleAsync(
        ChangeConsumerPasswordCommand command,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        if (string.IsNullOrWhiteSpace(command.CurrentPassword) || string.IsNullOrWhiteSpace(command.NewPassword))
        {
            return ConsumerErrors.PasswordChangeFailed("Current password and new password are required.");
        }

        return await passwordChanger.ChangePasswordAsync(userId, command.CurrentPassword, command.NewPassword, ct);
    }
}
