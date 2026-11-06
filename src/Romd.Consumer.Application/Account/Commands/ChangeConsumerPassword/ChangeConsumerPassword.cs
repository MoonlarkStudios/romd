using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Common.Persistence;

namespace Romd.Consumer.Application.Account.Commands.ChangeConsumerPassword;

public sealed record ChangeConsumerPasswordCommand(string CurrentPassword, string NewPassword) : ICommand<Updated>;

public sealed class ChangeConsumerPasswordCommandHandler(
    ICurrentUser currentUser,
    IConsumerPasswordChanger passwordChanger,
    IConsumerSecurityTransaction transaction)
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

        var prepared = await passwordChanger.PrepareAsync(userId, command.CurrentPassword, command.NewPassword, ct);
        if (prepared.IsError)
        {
            return prepared.Errors;
        }

        return await transaction.ExecuteAsync(prepared.Value.ApplyStagedAsync, ct);
    }
}
