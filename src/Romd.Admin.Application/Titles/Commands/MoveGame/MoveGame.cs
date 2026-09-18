using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Titles.Matching;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Commands.MoveGame;

/// <summary>
///     Command to move a single DatGame to a different title.
///     - If TargetTitleId is provided: move to that existing title
///     - If NewTitleName is provided: create a new title with that name
///     - If both are null: unassign game from any title
/// </summary>
public sealed record MoveGameCommand(
    int DatGameId,
    int? TargetTitleId,
    string? NewTitleName
) : ICommand<MoveGameResult>;

/// <summary>
///     Handler for <see cref="MoveGameCommand" />.
///     Moves a game to an existing title, creates a new title, or unassigns.
/// </summary>
public sealed class MoveGameCommandHandler : ICommandHandler<MoveGameCommand, MoveGameResult>
{
    private readonly IReferenceCatalogService _referenceCatalog;
    private readonly ITitleSourceAssignmentStore _titleSourceAssignments;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ICatalogProjectionService _catalogProjection;
    private readonly ILogger<MoveGameCommandHandler> _logger;
    private readonly ITitleRepository _titleRepository;
    private readonly IUnitOfWork _unitOfWork;

    public MoveGameCommandHandler(IReferenceCatalogService referenceCatalog,
        ITitleRepository titleRepository,
        ITitleSourceAssignmentStore titleSourceAssignments,
        IUnitOfWork unitOfWork,
        ILibraryRepository libraryRepository,
        ICatalogProjectionService catalogProjection,
        ILogger<MoveGameCommandHandler> logger)
    {
        _referenceCatalog = referenceCatalog;
        _titleRepository = titleRepository;
        _titleSourceAssignments = titleSourceAssignments;
        _unitOfWork = unitOfWork;
        _libraryRepository = libraryRepository;
        _catalogProjection = catalogProjection;
        _logger = logger;
    }

    public async Task<ErrorOr<MoveGameResult>> HandleAsync(MoveGameCommand command, CancellationToken ct = default)
    {
        var systemKeys = await _referenceCatalog.GetSystemKeysAsync(ct);
        // 1. Load the game with its platform
        var assignmentContext = await _titleSourceAssignments.GetAssignmentContextAsync(command.DatGameId, ct);
        if (assignmentContext is null)
        {
            return CatalogErrors.GameNotFound(command.DatGameId);
        }

        int? oldTitleId = assignmentContext.TitleId;
        bool titleCreated = false;
        bool oldTitleDeleted = false;
        bool newTitleHasLocalPayload = false;
        Title? newTitle = null;

        // Validate: can't specify both TargetTitleId and NewTitleName
        if (command.TargetTitleId.HasValue && !string.IsNullOrWhiteSpace(command.NewTitleName))
        {
            return CatalogErrors.InvalidMoveParameters();
        }

        if (assignmentContext.PlatformId is not int platformId)
        {
            return CatalogErrors.GamePlatformRequired(command.DatGameId);
        }

        bool commitSucceeded = false;
        try
        {
            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);

            // 2. Determine target
            if (command.TargetTitleId.HasValue)
            {
                // Move to existing title
                newTitle = await _titleRepository.GetByIdAsync(command.TargetTitleId.Value, ct);
                if (newTitle is null)
                {
                    return CatalogErrors.TitleNotFound(command.TargetTitleId.Value);
                }

                // Validate same platform
                if (newTitle.PlatformId != platformId)
                {
                    return CatalogErrors.PlatformMismatch(platformId, newTitle.PlatformId);
                }
            }
            else if (!string.IsNullOrWhiteSpace(command.NewTitleName))
            {
                // Create new title
                string normalizedName = TitleNormalizer.Normalize(command.NewTitleName);

                // Check if title already exists
                var existing = await _titleRepository.GetByNormalizedNameAsync(platformId, normalizedName, ct);
                if (existing is not null)
                {
                    return CatalogErrors.TitleAlreadyExists(normalizedName);
                }

                string displayName = TitleNormalizer.ToDisplayName(command.NewTitleName);
                newTitle = Title.CreateNew(platformId, displayName, normalizedName);
                newTitle = await _titleRepository.AddAsync(newTitle, ct);
                titleCreated = true;
            }
            // else: unassign (newTitle remains null)

            // 3. Update the game's TitleId
            if (newTitle is not null)
            {
                await _titleSourceAssignments.UpsertAssignmentsAsync(
                    [new TitleSourceAssignment(command.DatGameId, newTitle.Id)], ct);
            }
            else
            {
                await _titleSourceAssignments.ClearAssignmentsAsync([command.DatGameId], ct);
            }

            // 4. Check if old title is now empty and should be deleted
            if (oldTitleId.HasValue && oldTitleId != newTitle?.Id)
            {
                bool hasGames = await _titleRepository.HasGamesAsync(oldTitleId.Value, ct);
                if (!hasGames)
                {
                    // Retain the orphaned title as UserOnly if it carries user state; otherwise delete.
                    oldTitleDeleted = await _titleRepository.DeleteOrRetainAsync(oldTitleId.Value, ct);
                    _logger.LogInformation(
                        "Orphaned title ID {TitleId} after moving game {GameId} was {Action}",
                        oldTitleId.Value,
                        command.DatGameId,
                        oldTitleDeleted ? "deleted" : "retained as UserOnly");
                }
            }

            if (oldTitleId != newTitle?.Id)
            {
                // Commit source attribution, projection invalidation, and library scheduling as
                // one fact. Recovery observes Dirty if request-lifetime acceleration is skipped.
                await _catalogProjection.MarkPlatformDirtyAsync(
                    platformId,
                    ct,
                    new[] { oldTitleId, newTitle?.Id }.OfType<int>().ToArray());
                await _libraryRepository.FlagForRematerializationByPlatformAsync(platformId, ct);
            }

            // Read the projection-owned fact after the in-transaction rollup and before commit.
            // Carrying it across commit avoids a post-commit cancellation turning durable success
            // into an error response while keeping the returned contract truthful.
            if (newTitle is not null)
            {
                newTitleHasLocalPayload = await _titleRepository.HasLocalPayloadAsync(newTitle.Id, ct);
            }

            await transaction.CommitAsync(ct);
            commitSucceeded = true;

            // Best-effort acceleration only: durable Dirty state is the recovery contract.
            if (oldTitleId != newTitle?.Id)
            {
                await TryRebuildCatalogAsync(platformId, ct);
            }
        }
        catch (Exception ex) when (commitSucceeded)
        {
            _logger.LogWarning(
                ex,
                "Game move committed but transaction cleanup failed for game {GameId}",
                command.DatGameId);
        }

        _logger.LogInformation(
            "Moved game {GameId} from title {OldTitleId} to {NewTitleId}. Created: {Created}, Deleted old: {Deleted}",
            command.DatGameId, oldTitleId, newTitle?.Id, titleCreated, oldTitleDeleted);

        return new MoveGameResult(
            IdCoder.Encode(command.DatGameId),
            newTitle?.ToContract(systemKeys, newTitleHasLocalPayload),
            titleCreated,
            oldTitleDeleted);
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
                "Game move committed but catalog rebuild acceleration failed for platform {PlatformId}; recovery will retry",
                platformId);
        }
    }
}
