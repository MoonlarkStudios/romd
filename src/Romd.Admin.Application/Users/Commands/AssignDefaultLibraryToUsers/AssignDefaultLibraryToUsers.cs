using ErrorOr;
using Romd.Domain.Identity;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Users.Commands.AssignDefaultLibraryToUsers;

public sealed record AssignDefaultLibraryToUsersCommand(IReadOnlyList<Guid>? UserIds = null) : ICommand<int>;

public sealed class AssignDefaultLibraryToUsersCommandHandler(
    IUserAdministration users,
    ILibraryRepository libraries,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<AssignDefaultLibraryToUsersCommandHandler> logger)
    : ICommandHandler<AssignDefaultLibraryToUsersCommand, int>
{
    public async Task<ErrorOr<int>> HandleAsync(
        AssignDefaultLibraryToUsersCommand command,
        CancellationToken ct = default)
    {
        if (command.UserIds is null || command.UserIds.Count is < 1 or > 100)
            return Error.Validation("Users.SelectionRequired", "Select between 1 and 100 accounts.");
        if (command.UserIds.Contains(SystemActor.UserId))
            return UserErrors.ProtectedUser("The system account cannot receive library access.");
        int updatedCount = 0;
        bool commitSucceeded = false;
        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
            var defaultLibrary = await libraries.GetDefaultAsync(ct);
            if (defaultLibrary is null)
            {
                return UserErrors.NoDefaultLibrary();
            }

            if (!defaultLibrary.HasValidConfiguration)
            {
                return UserErrors.InvalidLibraryConfiguration();
            }

            foreach (var userId in command.UserIds.Distinct())
            {
                var user = await users.GetByIdAsync(userId, ct);
                if (user is null) return UserErrors.NotFound();
                if (user.LibraryId is not null || user.IsSuspended) continue;
                var assigned = await users.AssignLibraryAsync(userId, defaultLibrary.Id, timeProvider.GetUtcNow(), ct);
                if (assigned.IsError) return assigned.Errors;
                updatedCount++;
            }
            await transaction.CommitAsync(ct);
            commitSucceeded = true;
        }
        catch (Exception ex) when (commitSucceeded)
        {
            logger.LogWarning(ex, "Default library assignment committed but transaction cleanup failed");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to assign the default library to users");
            return UserErrors.PersistenceFailed();
        }

        return updatedCount;
    }
}
