using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Libraries;

namespace Romd.Admin.Application.Libraries.Commands.UpdateLibrary;

public sealed record UpdateLibraryCommand(
    int LibraryId,
    string Name,
    LibraryConfiguration? Configuration,
    bool? IsDefault) : ICommand<LibraryDto>;

public sealed class UpdateLibraryCommandHandler(IReferenceCatalogService referenceCatalog,
    ILibraryRepository libraryRepository,
    IAdminEventOutbox outbox,
    IUnitOfWork unitOfWork,
    ILogger<UpdateLibraryCommandHandler> logger,
    RomDeliveryPolicy deliveryPolicy,
    TimeProvider clock)
    : ICommandHandler<UpdateLibraryCommand, LibraryDto>
{
    public async Task<ErrorOr<LibraryDto>> HandleAsync(
        UpdateLibraryCommand command,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return LibraryErrors.NameRequired();
        }

        if (command.Configuration is not null
            && LibraryConfigurationValidator.Validate(command.Configuration) is not null)
        {
            return LibraryErrors.InvalidConfiguration();
        }

        ErrorOr<Library> receipt;
        try
        {
            receipt = await unitOfWork.ExecuteInTransactionAsync<Library>(async token =>
            {
                var library = await libraryRepository.GetByIdAsync(command.LibraryId, token);
                if (library is null)
                {
                    return LibraryErrors.NotFound();
                }

                var configuration = command.Configuration ?? library.Configuration;
                if (LibraryConfigurationValidator.Validate(configuration) is not null
                    || await libraryRepository.ValidateConfigurationReferencesAsync(configuration, token, command.LibraryId) is not null)
                {
                    return LibraryErrors.InvalidConfiguration();
                }

                var now = clock.GetUtcNow();
                library.UpdateConfiguration(command.Name, configuration, now);
                if (command.IsDefault == true)
                {
                    library.MarkAsDefault(now);
                }
                else if (command.IsDefault == false && library.IsDefault)
                {
                    library.UnmarkAsDefault(now);
                }

                if (command.IsDefault == true)
                {
                    await libraryRepository.ClearDefaultAsync(token);
                }

                await libraryRepository.UpdateStagedAsync(library, token);
                await outbox.EnqueueAsync(
                    AdminRealtimeEventTypes.LibraryUpdated,
                    library.ToUpdatedPayload(),
                    token);
                return library;
            }, logger, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update library");
            return LibraryErrors.PersistenceFailed();
        }

        return receipt.IsError ? receipt.Errors : receipt.Value.ToContract(systemKeys, deliveryPolicy);
    }

}
