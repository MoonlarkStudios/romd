using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Matching;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Source.Dat.Commands.AssignPlatform;

/// <summary>
///     Command to assign a platform to a DAT file.
/// </summary>
public sealed record AssignPlatformCommand : ICommand<PlatformAssignmentResult>
{
    /// <summary>
    ///     The ID of the DAT file.
    /// </summary>
    public required int DatId { get; init; }

    /// <summary>
    ///     The ID of the platform to assign.
    /// </summary>
    public required int PlatformId { get; init; }

    private AssignPlatformCommand() { }

    /// <summary>
    ///     Creates a validated command.
    /// </summary>
    public static ErrorOr<AssignPlatformCommand> Create(int datId, int platformId)
    {
        var errors = new List<Error>();

        if (datId <= 0)
        {
            errors.Add(Error.Validation("Command.InvalidDatId", "DAT ID must be positive"));
        }

        if (platformId <= 0)
        {
            errors.Add(Error.Validation("Command.InvalidPlatformId", "Platform ID must be positive"));
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        return new AssignPlatformCommand { DatId = datId, PlatformId = platformId };
    }
}

/// <summary>
///     Handler for <see cref="AssignPlatformCommand" />.
/// </summary>
public sealed class AssignPlatformCommandHandler : ICommandHandler<AssignPlatformCommand, PlatformAssignmentResult>
{
    private readonly IBiosGrouper _biosGrouper;
    private readonly ILogger<AssignPlatformCommandHandler> _logger;
    private readonly ILibraryRepository _libraryRepository;
    private readonly IPlatformRepository _platformRepository;
    private readonly ICatalogProjectionService _catalogProjection;
    private readonly IDatRepository _repository;
    private readonly ITitleDerivationService _titleDerivation;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISourceLifecycle _sourceLifecycle;

    public AssignPlatformCommandHandler(
        IDatRepository repository,
        ITitleDerivationService titleDerivation,
        IPlatformRepository platformRepository,
        IBiosGrouper biosGrouper,
        IUnitOfWork unitOfWork,
        ILibraryRepository libraryRepository,
        ICatalogProjectionService catalogProjection,
        ILogger<AssignPlatformCommandHandler> logger,
        ISourceLifecycle sourceLifecycle)
    {
        _repository = repository;
        _titleDerivation = titleDerivation;
        _platformRepository = platformRepository;
        _biosGrouper = biosGrouper;
        _unitOfWork = unitOfWork;
        _libraryRepository = libraryRepository;
        _catalogProjection = catalogProjection;
        _logger = logger;
        _sourceLifecycle = sourceLifecycle;
    }

    public async Task<ErrorOr<PlatformAssignmentResult>> HandleAsync(
        AssignPlatformCommand command,
        CancellationToken ct = default)
    {
        bool commitSucceeded = false;
        PlatformAssignmentResult? durableResult = null;
        try
        {
            var datFile = await _repository.GetByIdAsync(command.DatId, ct);
            if (datFile is null)
            {
                return CatalogErrors.DatNotFound(command.DatId);
            }

            if (datFile.PlatformId.HasValue)
            {
                return CatalogErrors.DatAlreadyHasPlatform(command.DatId);
            }

            var platform = await _platformRepository.GetByIdAsync(command.PlatformId, ct);
            if (platform is null)
            {
                return CatalogErrors.PlatformNotFound(command.PlatformId);
            }

            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);

            try
            {
                await _repository.UpdateDatFilePlatformAsync(command.DatId, command.PlatformId, ct);

                // Get all games including BIOS for full count, but filter BIOS for title matching
                var allGames = await _repository.GetAllGamesByDatIdAsync(command.DatId, BiosFilter.Include, ct);

                if (allGames.Count == 0)
                {
                    await _catalogProjection.MarkPlatformDirtyAsync(command.PlatformId, ct, []);
                    await transaction.CommitAsync(ct);
                    commitSucceeded = true;
                    durableResult = new PlatformAssignmentResult
                    {
                        GamesUpdated = 0, NewTitlesCreated = 0, ExistingTitlesMatched = 0
                    };
                    await TryRebuildCatalogAsync(command.PlatformId, ct);
                    return durableResult;
                }

                // Routing derives titles through the catalog's single derivation door.
                // Claims carry the platform (stamping entries routed-late) and the BIOS flag
                // (BIOS claims get entry identity, never links). Existing links are preserved.
                int catalogSourceId = await _repository.GetCatalogSourceIdAsync(datFile.DatSourceId, ct);
                IReadOnlyList<int> formerlyLinkedTitleIds =
                    await _sourceLifecycle.GetLinkedTitleIdsAsync(catalogSourceId, ct);

                async IAsyncEnumerable<TitleClaim> StreamClaimsAsync()
                {
                    foreach (var game in allGames)
                    {
                        yield return new TitleClaim(game.Name, game.Name, command.PlatformId, game.IsBios);
                    }
                }

                int linkedCount = 0;
                int newTitleCount = 0;
                await foreach (var assignmentResult in _titleDerivation
                                   .UpsertAsync(catalogSourceId, StreamClaimsAsync(), ct)
                                   .WithCancellation(ct))
                {
                    if (assignmentResult.IsError)
                    {
                        continue;
                    }

                    var assignment = assignmentResult.Value;
                    if (assignment.TitleId is not null)
                    {
                        linkedCount++;
                        if (assignment.TitleWasCreated)
                        {
                            newTitleCount++;
                        }
                    }
                }

                // BIOS games don't become Titles; group them into platform-scoped BIOS entries.
                var biosGames = allGames
                    .Where(g => g.IsBios)
                    .Select(g => (g.Id, g.Name))
                    .ToList();
                await _biosGrouper.GroupAsync(command.PlatformId, biosGames, ct);

                // The source mutation, projection invalidation, and affected-library flag are
                // one durable fact. If the process stops after commit, the recovery dispatcher
                // can see Dirty and converge the catalog without exposing stale materialization.
                await _catalogProjection.RefreshCatalogSourcePayloadAsync(catalogSourceId, ct);
                await _catalogProjection.MarkPlatformDirtyAsync(
                    command.PlatformId,
                    ct,
                    formerlyLinkedTitleIds);
                if (linkedCount > 0)
                {
                    await _libraryRepository.FlagForRematerializationByPlatformAsync(command.PlatformId, ct);
                }

                await transaction.CommitAsync(ct);
                commitSucceeded = true;
                durableResult = new PlatformAssignmentResult
                {
                    GamesUpdated = linkedCount,
                    NewTitlesCreated = newTitleCount,
                    ExistingTitlesMatched = linkedCount - newTitleCount
                };

                await TryRebuildCatalogAsync(command.PlatformId, ct);

                return durableResult;
            }
            catch (Exception ex) when (commitSucceeded)
            {
                _logger.LogWarning(
                    ex,
                    "Platform assignment committed but transaction cleanup failed for DAT {DatId}",
                    command.DatId);
                return durableResult!;
            }
            catch (Exception)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (Exception ex) when (commitSucceeded)
        {
            _logger.LogWarning(
                ex,
                "Platform assignment committed but transaction cleanup failed for DAT {DatId}",
                command.DatId);
            return durableResult!;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Failed to assign platform {PlatformId} to DAT {DatId}",
                command.PlatformId,
                command.DatId);
            return CatalogErrors.DatabaseFailed(ex.Message);
        }
    }

    private async Task TryRebuildCatalogAsync(int platformId, CancellationToken ct)
    {
        try
        {
            await _catalogProjection.RebuildPlatformAsync(platformId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Platform assignment committed but catalog rebuild acceleration failed for platform {PlatformId}; recovery will retry",
                platformId);
        }
    }
}
