using ErrorOr;
using Romd.Domain.Identity;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;

namespace Romd.Admin.Application.Users.Commands.AssignUserLibrary;

public sealed record AssignUserLibraryCommand(Guid UserId, string? LibraryId) : ICommand<ManagedUser>;

public sealed class AssignUserLibraryCommandHandler(
    IUserAdministration users,
    ILibraryRepository libraries,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<AssignUserLibraryCommandHandler> logger)
    : ICommandHandler<AssignUserLibraryCommand, ManagedUser>
{
    public async Task<ErrorOr<ManagedUser>> HandleAsync(
        AssignUserLibraryCommand command,
        CancellationToken ct = default)
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

            int? libraryId = null;
            if (command.LibraryId is not null)
            {
                if (!IdCoder.TryDecode(command.LibraryId, out var decodedLibraryId))
                {
                    return UserErrors.InvalidLibraryId(command.LibraryId);
                }

                var library = await libraries.GetByIdAsync(decodedLibraryId, ct);
                if (library is null)
                {
                    return UserErrors.LibraryNotFound();
                }

                if (!library.HasValidConfiguration)
                {
                    return UserErrors.InvalidLibraryConfiguration();
                }

                libraryId = decodedLibraryId;
            }

            var result = await users.AssignLibraryAsync(
                command.UserId,
                libraryId,
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
            logger.LogWarning(ex, "User library assignment committed but transaction cleanup failed (User ID: {UserId})", command.UserId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to assign user library (User ID: {UserId})", command.UserId);
            return UserErrors.PersistenceFailed();
        }

        return updatedUser!;
    }
}
