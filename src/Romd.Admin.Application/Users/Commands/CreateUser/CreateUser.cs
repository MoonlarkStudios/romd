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

        try
        {
            return await unitOfWork.ExecuteInTransactionAsync<ManagedUser>(async token =>
            {
                if (await users.GetByEmailAsync(command.Email, token) is not null)
                {
                    return UserErrors.DuplicateEmail(command.Email);
                }

                var libraryId = await ResolveLibraryIdAsync(command.LibraryId, token);
                if (libraryId.IsError)
                {
                    return libraryId.Errors;
                }

                var createResult = command.RequiresActivation
                    ? await users.CreatePendingAsync(command.Email, libraryId.Value, timeProvider.GetUtcNow(), token)
                    : await users.CreateAsync(command.Email, command.Password, libraryId.Value, timeProvider.GetUtcNow(), token);
                if (createResult.IsError)
                {
                    return createResult.Errors;
                }

                var roleResult = await users.AddRoleAsync(createResult.Value.Id, role, token);
                if (roleResult.IsError)
                {
                    return roleResult.Errors;
                }

                return roleResult.Value;
            }, logger, ct);
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
