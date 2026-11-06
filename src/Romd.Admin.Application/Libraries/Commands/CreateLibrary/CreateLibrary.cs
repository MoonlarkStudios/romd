using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Libraries;

namespace Romd.Admin.Application.Libraries.Commands.CreateLibrary;

public sealed record CreateLibraryCommand(
    string Name,
    LibraryConfiguration Configuration,
    bool IsDefault) : ICommand<LibraryDto>;

public sealed class CreateLibraryCommandHandler(IReferenceCatalogService referenceCatalog,
    ILibraryRepository libraryRepository,
    IAdminEventOutbox outbox,
    IUnitOfWork unitOfWork,
    ILogger<CreateLibraryCommandHandler> logger,
    RomDeliveryPolicy deliveryPolicy,
    TimeProvider clock)
    : ICommandHandler<CreateLibraryCommand, LibraryDto>
{
    public async Task<ErrorOr<LibraryDto>> HandleAsync(
        CreateLibraryCommand command,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return LibraryErrors.NameRequired();
        }

        if (LibraryConfigurationValidator.Validate(command.Configuration) is not null)
        {
            return LibraryErrors.InvalidConfiguration();
        }

        var now = clock.GetUtcNow();
        var library = Library.CreateNew(command.Name, command.Configuration, now);
        if (command.IsDefault)
        {
            library.MarkAsDefault(now);
        }

        ErrorOr<Library> receipt;
        try
        {
            receipt = await unitOfWork.ExecuteInTransactionAsync<Library>(async token =>
            {
                if (await libraryRepository.ValidateConfigurationReferencesAsync(command.Configuration, token) is not null)
                {
                    return LibraryErrors.InvalidConfiguration();
                }

                if (library.IsDefault)
                {
                    await libraryRepository.ClearDefaultAsync(token);
                }

                await libraryRepository.AddStagedAsync(library, token);
                await unitOfWork.FlushAsync(token);
                var committedLibrary = await libraryRepository.GetByNameAsync(library.Name, token)
                    ?? throw new InvalidOperationException("Staged library was not available after flush.");

                await outbox.EnqueueAsync(
                    AdminRealtimeEventTypes.LibraryUpdated,
                    committedLibrary.ToUpdatedPayload(),
                    token);
                return committedLibrary;
            }, logger, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create library");
            return LibraryErrors.PersistenceFailed();
        }

        return receipt.IsError ? receipt.Errors : receipt.Value.ToContract(systemKeys, deliveryPolicy);
    }

}
