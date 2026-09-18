using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Storage.Files;

namespace Romd.Admin.Application.Source.Rom.Commands.PurgeUnidentified;

/// <summary>
///     Removes every unidentified ROM (the inbox) in one bulk operation, then reclaims the CAS
///     storage those ROMs occupied. Unidentified ROMs have no DAT match and no platform, so no
///     library rematerialization is needed.
/// </summary>
public sealed record PurgeUnidentifiedRomsCommand : ICommand<PurgeUnidentifiedRomsResult>;

public sealed record PurgeUnidentifiedRomsResult
{
    public required int DeletedCount { get; init; }
    public required int ReclaimedFileCount { get; init; }
}

public sealed class PurgeUnidentifiedRomsCommandHandler
    : ICommandHandler<PurgeUnidentifiedRomsCommand, PurgeUnidentifiedRomsResult>
{
    // Files become reclaimable the instant their last ROM is deleted, but require an age bound to
    // protect in-flight uploads. One hour is far longer than any ingest takes to link a stored blob.
    private static readonly TimeSpan _reclaimMinimumAge = TimeSpan.FromHours(1);

    private readonly IFileStorageService _fileStorage;
    private readonly ILogger<PurgeUnidentifiedRomsCommandHandler> _logger;
    private readonly IAdminEventOutbox _outbox;
    private readonly IRomRepository _romRepository;
    private readonly IUnitOfWork _unitOfWork;

    public PurgeUnidentifiedRomsCommandHandler(
        IRomRepository romRepository,
        IFileStorageService fileStorage,
        IAdminEventOutbox outbox,
        IUnitOfWork unitOfWork,
        ILogger<PurgeUnidentifiedRomsCommandHandler> logger)
    {
        _romRepository = romRepository;
        _fileStorage = fileStorage;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<ErrorOr<PurgeUnidentifiedRomsResult>> HandleAsync(
        PurgeUnidentifiedRomsCommand command,
        CancellationToken ct = default)
    {
        int deleted;
        await using (var transaction = await _unitOfWork.BeginTransactionAsync(ct))
        {
            deleted = await _romRepository.DeleteUnidentifiedAsync(ct);
            if (deleted > 0)
            {
                await EnqueueStatsChangedAsync(ct);
                await _unitOfWork.FlushAsync(ct);
                await transaction.CommitAsync(ct);
            }
        }

        // Reclaim the CAS rows + blobs the just-deleted ROMs left unreferenced (and any earlier
        // strays). Age-guarded so an in-flight upload is never deleted.
        int reclaimed = await _fileStorage.PruneUnreferencedFilesAsync(_reclaimMinimumAge, ct);

        _logger.LogInformation(
            "Purged {Deleted} unidentified ROMs; reclaimed {Reclaimed} unreferenced files",
            deleted, reclaimed);

        return new PurgeUnidentifiedRomsResult { DeletedCount = deleted, ReclaimedFileCount = reclaimed };
    }

    private async Task EnqueueStatsChangedAsync(CancellationToken ct)
    {
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, ct);
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.CoverageStatsChanged, ct);
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, ct);
    }
}
