using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Commands.DeleteRom;

/// <summary>
///     Command to delete a ROM file from the library.
/// </summary>
public sealed record DeleteRomCommand(int RomId) : ICommand;

public sealed class DeleteRomCommandHandler : ICommandHandler<DeleteRomCommand, Deleted>
{
    private readonly IFileStorageService _fileStorage;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILogger<DeleteRomCommandHandler> _logger;
    private readonly IRomRepository _romRepository;
    private readonly IAdminEventOutbox _outbox;
    private readonly IRomOwnershipImpactReader _ownershipImpact;
    private readonly IRomPayloadAssertionImpactReader _payloadAssertionImpact;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITitlePayloadAvailabilityProjection _payloadAvailability;

    public DeleteRomCommandHandler(
        IRomRepository romRepository,
        IRomOwnershipImpactReader ownershipImpact,
        IRomPayloadAssertionImpactReader payloadAssertionImpact,
        IFileStorageService fileStorage,
        IAdminEventOutbox outbox,
        IUnitOfWork unitOfWork,
        ILibraryRepository libraryRepository,
        ITitlePayloadAvailabilityProjection payloadAvailability,
        ILogger<DeleteRomCommandHandler> logger)
    {
        _romRepository = romRepository;
        _ownershipImpact = ownershipImpact;
        _payloadAssertionImpact = payloadAssertionImpact;
        _fileStorage = fileStorage;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _libraryRepository = libraryRepository;
        _payloadAvailability = payloadAvailability;
        _logger = logger;
    }

    public async Task<ErrorOr<Deleted>> HandleAsync(DeleteRomCommand command, CancellationToken ct = default)
    {
        RomFile? existing = null;
        bool commitSucceeded = false;

        try
        {
            var loaded = await _romRepository.GetByIdAsync(command.RomId, ct);
            if (loaded is null)
            {
                return CatalogErrors.RomNotFound();
            }

            existing = loaded;

            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
            var matchedPlatformIds = await _ownershipImpact.ReadTitlePlatformIdsAsync(command.RomId, ct);
            var affectedSourceEntryIds = await _payloadAssertionImpact
                .ReadSourceEntryIdsByRomFileIdAsync(command.RomId, ct);
            foreach (int platformId in matchedPlatformIds)
            {
                await _libraryRepository.FlagForRematerializationByPlatformAsync(platformId, ct);
            }

            await _romRepository.DeleteAsync(command.RomId, ct);
            await _payloadAvailability.RefreshSourceEntryPayloadAssertionsAsync(affectedSourceEntryIds, ct);
            await EnqueueStatsChangedAsync(ct);
            await transaction.CommitAsync(ct);
            commitSucceeded = true;
        }
        catch (Exception ex) when (commitSucceeded)
        {
            _logger.LogWarning(
                ex,
                "ROM deletion committed but transaction cleanup failed (ROM ID: {RomId})",
                command.RomId);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Storage failure while deleting ROM (ID: {Id})", command.RomId);
            return CatalogErrors.RomStorageFailed();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to delete ROM (ID: {Id})", command.RomId);
            return CatalogErrors.RomDatabaseFailed();
        }

        var committedRom = existing!;
        await TryDeleteUnreferencedFileAsync(committedRom.FileId, command.RomId, ct);

        _logger.LogInformation(
            "Deleted ROM (ID: {Id}, SHA1: {Sha1})",
            command.RomId,
            committedRom.Sha1.ToShortHex());

        return Result.Deleted;
    }

    private async Task EnqueueStatsChangedAsync(CancellationToken ct)
    {
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, ct);
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.CoverageStatsChanged, ct);
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, ct);
    }

    private async Task TryDeleteUnreferencedFileAsync(int fileId, int romId, CancellationToken ct)
    {
        try
        {
            await _fileStorage.DeleteIfUnreferencedAsync(fileId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ROM deletion committed but immediate unreferenced-file cleanup failed " +
                "(ROM ID: {RomId}, File ID: {FileId}); recurring cleanup will retry",
                romId,
                fileId);
        }
    }
}
