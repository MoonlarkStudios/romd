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

        ManagedUser? updatedUser = null;
        bool commitSucceeded = false;
        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
            if (await users.GetByIdAsync(command.UserId, ct) is null)
            {
                return UserErrors.NotFound();
            }

            var result = await users.UpdateAsync(
                command.UserId,
                command.Email,
                command.NewPassword,
                timeProvider.GetUtcNow(),
                ct);
            if (result.IsError)
            {
                return result.Errors;
            }

            updatedUser = result.Value;
            await transaction.CommitAsync(ct);
            commitSucceeded = true;
        }
        catch (Exception ex) when (commitSucceeded)
        {
            logger.LogWarning(ex, "User update committed but transaction cleanup failed (User ID: {UserId})", command.UserId);
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

        return updatedUser!;
    }
}
