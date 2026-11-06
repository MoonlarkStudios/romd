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

        try
        {
            return await unitOfWork.ExecuteInTransactionAsync<ManagedUser>(async token =>
            {
                if (await users.GetByIdAsync(command.UserId, token) is null)
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

                    var library = await libraries.GetByIdAsync(decodedLibraryId, token);
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
            logger.LogError(ex, "Failed to assign user library (User ID: {UserId})", command.UserId);
            return UserErrors.PersistenceFailed();
        }
    }
}
