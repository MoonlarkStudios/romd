using ErrorOr;
using Romd.Domain.Identity;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Users.Commands.UpdateUser;

public sealed record UpdateUserCommand(
    Guid UserId,
    string? Email,
    string? NewPassword) : ICommand<ManagedUser>;

public sealed class UpdateUserCommandHandler(
    IUserAdministration users,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<UpdateUserCommandHandler> logger)
    : ICommandHandler<UpdateUserCommand, ManagedUser>
{
    public async Task<ErrorOr<ManagedUser>> HandleAsync(UpdateUserCommand command, CancellationToken ct = default)
    {
        if (command.UserId == SystemActor.UserId)
        {
            return UserErrors.ProtectedUser("The ROMD system account cannot be edited.");
        }

        try
        {
            return await unitOfWork.ExecuteInTransactionAsync<ManagedUser>(async token =>
            {
                if (await users.GetByIdAsync(command.UserId, token) is null)
                {
                    return UserErrors.NotFound();
                }

                var result = await users.UpdateAsync(
                    command.UserId,
                    command.Email,
                    command.NewPassword,
                    timeProvider.GetUtcNow(),
                    token);
                if (result.IsError)
                {
                    return result.Errors;
                }

                return result.Value;
            }, logger, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update user (User ID: {UserId})", command.UserId);
            return UserErrors.PersistenceFailed();
        }
    }
}
