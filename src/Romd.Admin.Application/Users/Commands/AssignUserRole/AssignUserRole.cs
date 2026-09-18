using ErrorOr;
using Romd.Domain.Identity;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Users.Commands.AssignUserRole;

public sealed record AssignUserRoleCommand(Guid UserId, string Role) : ICommand<ManagedUser>;

public sealed class AssignUserRoleCommandHandler(
    IUserAdministration users,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<AssignUserRoleCommandHandler> logger)
    : ICommandHandler<AssignUserRoleCommand, ManagedUser>
{
    public async Task<ErrorOr<ManagedUser>> HandleAsync(AssignUserRoleCommand command, CancellationToken ct = default)
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

            if (!UserRoleParser.TryParse(command.Role, out var role))
            {
                return UserErrors.InvalidRole(command.Role);
            }

            var result = await users.ReplaceRoleAsync(command.UserId, role, timeProvider.GetUtcNow(), ct);
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
            logger.LogWarning(ex, "User role assignment committed but transaction cleanup failed (User ID: {UserId})", command.UserId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to assign user role (User ID: {UserId})", command.UserId);
            return UserErrors.PersistenceFailed();
        }

        return updatedUser!;
    }
}
