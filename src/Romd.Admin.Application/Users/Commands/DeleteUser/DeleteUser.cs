using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;

namespace Romd.Admin.Application.Users.Commands.DeleteUser;

public sealed record DeleteUserCommand(Guid UserId) : ICommand<Deleted>;

public sealed class DeleteUserCommandHandler(
    IUserAdministration users,
    ICurrentUser currentUser,
    IUnitOfWork unitOfWork,
    ILogger<DeleteUserCommandHandler> logger)
    : ICommandHandler<DeleteUserCommand, Deleted>
{
    public async Task<ErrorOr<Deleted>> HandleAsync(DeleteUserCommand command, CancellationToken ct = default)
    {
        if (command.UserId == SystemActor.UserId)
        {
            return UserErrors.ProtectedUser("The ROMD system user cannot be deleted.");
        }

        if (currentUser.UserId == command.UserId)
        {
            return UserErrors.ProtectedUser("You cannot delete the account you are currently signed in with.");
        }

        try
        {
            return await unitOfWork.ExecuteInTransactionAsync<Deleted>(async token =>
            {
                var user = await users.GetByIdAsync(command.UserId, token);
                if (user is null)
                {
                    return UserErrors.NotFound();
                }

                if (string.Equals(user.Email, SystemActor.Email, StringComparison.OrdinalIgnoreCase))
                {
                    return UserErrors.ProtectedUser("The ROMD system user cannot be deleted.");
                }

                var result = await users.DeleteAsync(command.UserId, token);
                if (result.IsError)
                {
                    return result.Errors;
                }

                return Result.Deleted;
            }, logger, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete user (User ID: {UserId})", command.UserId);
            return UserErrors.PersistenceFailed();
        }
    }
}
