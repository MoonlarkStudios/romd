using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Libraries;

namespace Romd.Admin.Application.Libraries.Commands.CreateLibrary;

public sealed record CreateLibraryCommand(
    string Name,
    LibraryConfiguration Configuration,
    bool IsDefault) : ICommand<LibraryDto>;

public sealed class CreateLibraryCommandHandler(IReferenceCatalogService referenceCatalog,
    ILibraryRepository libraryRepository,
    ILibraryMaterializationScheduler materializationScheduler,
    IAdminEventOutbox outbox,
    IUnitOfWork unitOfWork,
    ILogger<CreateLibraryCommandHandler> logger)
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

        var library = Library.CreateNew(command.Name, command.Configuration);
        if (command.IsDefault)
        {
            library.MarkAsDefault();
        }

        Library? committedLibrary = null;
        bool commitSucceeded = false;
        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

            if (await libraryRepository.ValidateConfigurationReferencesAsync(command.Configuration, ct) is not null)
            {
                return LibraryErrors.InvalidConfiguration();
            }

            if (library.IsDefault)
            {
                await libraryRepository.ClearDefaultAsync(ct);
            }

            await libraryRepository.AddStagedAsync(library, ct);
            await unitOfWork.FlushAsync(ct);
            committedLibrary = await libraryRepository.GetByNameAsync(library.Name, ct)
                ?? throw new InvalidOperationException("Staged library was not available after flush.");

            await outbox.EnqueueAsync(
                AdminRealtimeEventTypes.LibraryUpdated,
                committedLibrary.ToUpdatedPayload(),
                ct);
            await transaction.CommitAsync(ct);
            commitSucceeded = true;
        }
        catch (Exception ex) when (commitSucceeded)
        {
            logger.LogWarning(
                ex,
                "Library creation committed but transaction cleanup failed (Library ID: {LibraryId})",
                committedLibrary?.Id);
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

        await TryScheduleMaterializationAsync(committedLibrary!, ct);
        return committedLibrary!.ToContract(systemKeys);
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
                "Library creation committed but materialization acceleration failed " +
                "(Library ID: {LibraryId}); the recurring dispatcher will recover",
                library.Id);
        }
    }
}
