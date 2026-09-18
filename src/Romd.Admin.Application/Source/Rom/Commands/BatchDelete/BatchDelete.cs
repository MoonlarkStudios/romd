using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Source.Rom.Commands.BatchDelete;

/// <summary>
///     Command to delete multiple ROM files.
/// </summary>
public sealed record BatchDeleteRomsCommand : ICommand<BatchDeleteResult>
{
    /// <summary>
    ///     ROM IDs to delete.
    /// </summary>
    public required IReadOnlyList<int> RomIds { get; init; }
}

/// <summary>
///     Result of batch delete operation.
/// </summary>
public sealed record BatchDeleteResult
{
    /// <summary>
    ///     Number of ROMs successfully deleted.
    /// </summary>
    public required int DeletedCount { get; init; }

    /// <summary>
    ///     Number of ROMs that failed to delete.
    /// </summary>
    public required int FailedCount { get; init; }

    /// <summary>
    ///     Error messages for failed deletions.
    /// </summary>
    public required IReadOnlyList<string> Errors { get; init; }
}

public sealed class BatchDeleteRomsCommandHandler : ICommandHandler<BatchDeleteRomsCommand, BatchDeleteResult>
{
    private readonly IFileStorageService _fileStorage;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILogger<BatchDeleteRomsCommandHandler> _logger;
    private readonly IAdminEventOutbox _outbox;
    private readonly IRomOwnershipImpactReader _ownershipImpact;
    private readonly IRomPayloadAssertionImpactReader _payloadAssertionImpact;
    private readonly IRomRepository _romRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITitlePayloadAvailabilityProjection _payloadAvailability;

    public BatchDeleteRomsCommandHandler(
        IRomRepository romRepository,
        IRomOwnershipImpactReader ownershipImpact,
        IRomPayloadAssertionImpactReader payloadAssertionImpact,
        IFileStorageService fileStorage,
        IAdminEventOutbox outbox,
        IUnitOfWork unitOfWork,
        ILibraryRepository libraryRepository,
        ITitlePayloadAvailabilityProjection payloadAvailability,
        ILogger<BatchDeleteRomsCommandHandler> logger)
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

    public async Task<ErrorOr<BatchDeleteResult>> HandleAsync(
        BatchDeleteRomsCommand command,
        CancellationToken ct = default)
    {
        if (command.RomIds.Count == 0)
        {
            return new BatchDeleteResult { DeletedCount = 0, FailedCount = 0, Errors = [] };
        }

        _logger.LogInformation("Starting batch delete of {Count} ROMs", command.RomIds.Count);

        int deletedCount = 0;
        int failedCount = 0;
        var errors = new List<string>();

        foreach (int romId in command.RomIds)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var existing = await _romRepository.GetByIdAsync(romId, ct);
                if (existing is null)
                {
                    deletedCount++;
                    continue;
                }

                await using (var transaction = await _unitOfWork.BeginTransactionAsync(ct))
                {
                    try
                    {
                        var matchedPlatformIds =
                            await _ownershipImpact.ReadTitlePlatformIdsAsync(romId, ct);
                        var affectedSourceEntryIds = await _payloadAssertionImpact
                            .ReadSourceEntryIdsByRomFileIdAsync(romId, ct);
                        foreach (int platformId in matchedPlatformIds.Distinct())
                        {
                            await _libraryRepository.FlagForRematerializationByPlatformAsync(platformId, ct);
                        }

                        await _romRepository.DeleteAsync(romId, ct);
                        await _payloadAvailability.RefreshSourceEntryPayloadAssertionsAsync(
                            affectedSourceEntryIds,
                            ct);
                        await EnqueueStatsChangedAsync(ct);
                        await _unitOfWork.FlushAsync(ct);
                        await transaction.CommitAsync(ct);
                    }
                    catch
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        throw;
                    }
                }

                await _fileStorage.DeleteIfUnreferencedAsync(existing.FileId, ct);

                deletedCount++;

                _logger.LogDebug(
                    "Deleted ROM (ID: {Id}, SHA1: {Sha1})",
                    romId,
                    existing.Sha1.ToShortHex());
            }
            catch (IOException ex)
            {
                failedCount++;
                string message = $"ROM {romId}: Storage error - {ex.Message}";
                errors.Add(message);
                _logger.LogWarning(ex, "Failed to delete ROM storage (ID: {Id})", romId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedCount++;
                string message = $"ROM {romId}: {ex.Message}";
                errors.Add(message);
                _logger.LogWarning(ex, "Failed to delete ROM (ID: {Id})", romId);
            }
        }

        _logger.LogInformation(
            "Batch delete complete. Deleted: {Deleted}, Failed: {Failed}",
            deletedCount,
            failedCount);

        return new BatchDeleteResult { DeletedCount = deletedCount, FailedCount = failedCount, Errors = errors };
    }

    private async Task EnqueueStatsChangedAsync(CancellationToken ct)
    {
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, ct);
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.CoverageStatsChanged, ct);
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, ct);
    }
}
