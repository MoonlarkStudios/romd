using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;

namespace Romd.Admin.Application.Users.Commands.CreateUser;

public sealed record CreateUserCommand(
    string Email,
    string Password,
    string Role,
    string? LibraryId,
    bool RequiresActivation = false) : ICommand<ManagedUser>;

public sealed class CreateUserCommandHandler(
    IUserAdministration users,
    ILibraryRepository libraries,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<CreateUserCommandHandler> logger)
    : ICommandHandler<CreateUserCommand, ManagedUser>
{
    public async Task<ErrorOr<ManagedUser>> HandleAsync(
        CreateUserCommand command,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Email) || (!command.RequiresActivation && string.IsNullOrWhiteSpace(command.Password)))
        {
            return UserErrors.MissingRequiredFields();
        }

        if (!UserRoleParser.TryParse(command.Role, out var role))
        {
            return UserErrors.InvalidRole(command.Role);
        }

        ManagedUser? createdUser = null;
        bool commitSucceeded = false;
        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

            if (await users.GetByEmailAsync(command.Email, ct) is not null)
            {
                return UserErrors.DuplicateEmail(command.Email);
            }

            var libraryId = await ResolveLibraryIdAsync(command.LibraryId, ct);
            if (libraryId.IsError)
            {
                return libraryId.Errors;
            }

            var createResult = command.RequiresActivation
                ? await users.CreatePendingAsync(command.Email, libraryId.Value, timeProvider.GetUtcNow(), ct)
                : await users.CreateAsync(command.Email, command.Password, libraryId.Value, timeProvider.GetUtcNow(), ct);
            if (createResult.IsError)
            {
                return createResult.Errors;
            }

            var roleResult = await users.AddRoleAsync(createResult.Value.Id, role, ct);
            if (roleResult.IsError)
            {
                return roleResult.Errors;
            }

            createdUser = roleResult.Value;
            await transaction.CommitAsync(ct);
            commitSucceeded = true;
        }
        catch (Exception ex) when (commitSucceeded)
        {
            logger.LogWarning(ex, "User creation committed but transaction cleanup failed (User ID: {UserId})", createdUser?.Id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create user");
            return UserErrors.PersistenceFailed();
        }

        return createdUser!;
    }

    private async Task<ErrorOr<int?>> ResolveLibraryIdAsync(string? encodedLibraryId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(encodedLibraryId))
        {
            if (!IdCoder.TryDecode(encodedLibraryId, out var libraryId))
            {
                return UserErrors.InvalidLibraryId(encodedLibraryId);
            }

            var library = await libraries.GetByIdAsync(libraryId, ct);
            if (library is null)
            {
                return UserErrors.LibraryNotFound();
            }

            return library.HasValidConfiguration
                ? libraryId
                : UserErrors.InvalidLibraryConfiguration();
        }

        var defaultLibrary = await libraries.GetDefaultAsync(ct);
        if (defaultLibrary is { HasValidConfiguration: false })
        {
            return UserErrors.InvalidLibraryConfiguration();
        }

        return defaultLibrary?.Id;
    }
}
