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

        try
        {
            return await unitOfWork.ExecuteInTransactionAsync<ManagedUser>(async token =>
            {
                if (await users.GetByIdAsync(command.UserId, token) is null)
                {
                    return UserErrors.NotFound();
                }

                if (!UserRoleParser.TryParse(command.Role, out var role))
                {
                    return UserErrors.InvalidRole(command.Role);
                }

                var result = await users.ReplaceRoleAsync(command.UserId, role, timeProvider.GetUtcNow(), token);
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
            logger.LogError(ex, "Failed to assign user role (User ID: {UserId})", command.UserId);
            return UserErrors.PersistenceFailed();
        }
    }
}
