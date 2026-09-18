using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Catalog.Commands.BackfillBiosCatalog;

/// <summary>
///     Groups BIOS games of already platform-assigned DATs into BIOS catalog entries.
///     Idempotent: only games not yet grouped are processed.
/// </summary>
public sealed record BackfillBiosCatalogCommand : ICommand<BackfillBiosCatalogResult>;

/// <summary>
///     Result of the BIOS catalog backfill.
/// </summary>
public sealed record BackfillBiosCatalogResult(int BiosEntriesCreated, int GamesGrouped);

public sealed class BackfillBiosCatalogCommandHandler
    : ICommandHandler<BackfillBiosCatalogCommand, BackfillBiosCatalogResult>
{
    private readonly IBiosGrouper _biosGrouper;
    private readonly IBiosRepository _biosRepository;
    private readonly ILogger<BackfillBiosCatalogCommandHandler> _logger;
    private readonly IUnitOfWork _unitOfWork;

    public BackfillBiosCatalogCommandHandler(
        IBiosRepository biosRepository,
        IBiosGrouper biosGrouper,
        IUnitOfWork unitOfWork,
        ILogger<BackfillBiosCatalogCommandHandler> logger)
    {
        _biosRepository = biosRepository;
        _biosGrouper = biosGrouper;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<ErrorOr<BackfillBiosCatalogResult>> HandleAsync(
        BackfillBiosCatalogCommand command,
        CancellationToken ct = default)
    {
        try
        {
            var unmapped = await _biosRepository.GetUnmappedBiosGamesAsync(ct);
            if (unmapped.Count == 0)
            {
                return new BackfillBiosCatalogResult(0, 0);
            }

            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
            try
            {
                var created = 0;
                foreach (var platformGroup in unmapped.GroupBy(g => g.PlatformId))
                {
                    var games = platformGroup
                        .Select(g => (g.GameId, g.Name))
                        .ToList();
                    created += await _biosGrouper.GroupAsync(platformGroup.Key, games, ct);
                }

                await transaction.CommitAsync(ct);
                return new BackfillBiosCatalogResult(created, unmapped.Count);
            }
            catch (Exception)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to backfill BIOS catalog");
            return CatalogErrors.DatabaseFailed(ex.Message);
        }
    }
}
