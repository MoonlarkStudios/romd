using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Cqrs;
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
    ILibraryMaterializationScheduler materializationScheduler,
    IAdminEventOutbox outbox,
    IUnitOfWork unitOfWork,
    ILogger<UpdateLibraryCommandHandler> logger)
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

        Library? library = null;
        bool commitSucceeded = false;
        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

            library = await libraryRepository.GetByIdAsync(command.LibraryId, ct);
            if (library is null)
            {
                return LibraryErrors.NotFound();
            }

            var configuration = command.Configuration ?? library.Configuration;
            if (LibraryConfigurationValidator.Validate(configuration) is not null
                || await libraryRepository.ValidateConfigurationReferencesAsync(configuration, ct) is not null)
            {
                return LibraryErrors.InvalidConfiguration();
            }

            library.UpdateConfiguration(command.Name, configuration);
            if (command.IsDefault == true)
            {
                library.MarkAsDefault();
            }
            else if (command.IsDefault == false && library.IsDefault)
            {
                library.UnmarkAsDefault();
            }

            if (command.IsDefault == true)
            {
                await libraryRepository.ClearDefaultAsync(ct);
            }

            await libraryRepository.UpdateStagedAsync(library, ct);
            await outbox.EnqueueAsync(
                AdminRealtimeEventTypes.LibraryUpdated,
                library.ToUpdatedPayload(),
                ct);
            await transaction.CommitAsync(ct);
            commitSucceeded = true;
        }
        catch (Exception ex) when (commitSucceeded)
        {
            logger.LogWarning(
                ex,
                "Library update committed but transaction cleanup failed (Library ID: {LibraryId})",
                library?.Id);
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

        await TryScheduleMaterializationAsync(library!, ct);
        return library!.ToContract(systemKeys);
    }

    private async Task TryScheduleMaterializationAsync(Library library, CancellationToken ct)
    {
        try
        {
            await materializationScheduler.EnqueueIfNeededAsync(library.Id, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Library update committed but materialization acceleration failed " +
                "(Library ID: {LibraryId}); the recurring dispatcher will recover",
                library.Id);
        }
    }
}
