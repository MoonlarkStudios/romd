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
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Commands.MoveGame;

/// <summary>
///     Command to move a single DatGame from its current title <paramref name="TitleId" /> to a different title.
///     - If TargetTitleId is provided: move to that existing title
///     - If NewTitleName is provided: create a new title with that name
///     - If both are null: unassign game from any title
///     A move rewrites source assignments, not title rows, so it is not a member of the title family and
///     carries no precondition (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency").
/// </summary>
public sealed record MoveGameCommand(
    int TitleId,
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
    private readonly TimeProvider _clock;
    private readonly IReferenceCatalogService _referenceCatalog;
    private readonly ITitleSourceAssignmentStore _titleSourceAssignments;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ICatalogProjectionService _catalogProjection;
    private readonly ILogger<MoveGameCommandHandler> _logger;
    private readonly ITitleCreationStore _titleCreation;
    private readonly ITitleOrphanCleanup _orphanCleanup;
    private readonly ITitleReader _titleReader;
    private readonly ITitleRowLock _titleLock;
    private readonly IUnitOfWork _unitOfWork;

    public MoveGameCommandHandler(IReferenceCatalogService referenceCatalog,
        ITitleCreationStore titleCreation,
        ITitleOrphanCleanup orphanCleanup,
        ITitleReader titleReader,
        ITitleRowLock titleLock,
        ITitleSourceAssignmentStore titleSourceAssignments,
        IUnitOfWork unitOfWork,
        ILibraryRepository libraryRepository,
        ICatalogProjectionService catalogProjection,
        ILogger<MoveGameCommandHandler> logger,
        TimeProvider clock)
    {
        _referenceCatalog = referenceCatalog;
        _titleCreation = titleCreation;
        _orphanCleanup = orphanCleanup;
        _titleReader = titleReader;
        _titleLock = titleLock;
        _titleSourceAssignments = titleSourceAssignments;
        _unitOfWork = unitOfWork;
        _libraryRepository = libraryRepository;
        _catalogProjection = catalogProjection;
        _logger = logger;
        _clock = clock;
    }

    public async Task<ErrorOr<MoveGameResult>> HandleAsync(MoveGameCommand command, CancellationToken ct = default)
    {
        var systemKeys = await _referenceCatalog.GetSystemKeysAsync(ct);
        // Resolve the path resource before a subordinate game identity.
        if (await _titleReader.GetSummaryAsync(command.TitleId, ct) is null)
            return CatalogErrors.TitleNotFound();

        // 1. Load the game with its platform
        var assignmentContext = await _titleSourceAssignments.GetAssignmentContextAsync(command.DatGameId, ct);
        if (assignmentContext is null || assignmentContext.TitleId != command.TitleId)
        {
            return CatalogErrors.GameNotFound();
        }

        int? oldTitleId = assignmentContext.TitleId;
        bool titleCreated = false;
        bool oldTitleDeleted = false;
        int? newTitleId = null;
        TitleSummary? newTitle = null;

        // Validate: can't specify both TargetTitleId and NewTitleName
        if (command.TargetTitleId.HasValue && !string.IsNullOrWhiteSpace(command.NewTitleName))
        {
            return CatalogErrors.InvalidMoveParameters();
        }

        if (assignmentContext.PlatformId is not int platformId)
        {
            return CatalogErrors.GamePlatformRequired();
        }

        bool commitSucceeded = false;
        var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        bool failed = false;
        try
        {

            // Both titles stay locked until commit, in ID order as merges lock them, so concurrent moves of
            // the same game serialize on its current title.
            if (await LockTitlesAsync(command, ct) is { } missing)
            {
                return missing;
            }

            // A game moved by a concurrent request since it was read is no longer under this title.
            if ((await _titleSourceAssignments.GetAssignmentContextAsync(command.DatGameId, ct))?.TitleId
                != command.TitleId)
            {
                return CatalogErrors.GameNotFound();
            }

            // 2. Determine target
            if (command.TargetTitleId.HasValue)
            {
                // Move to existing title
                var target = await _titleReader.GetSummaryAsync(command.TargetTitleId.Value, ct);
                if (target is null)
                {
                    return CatalogErrors.TitleNotFound();
                }

                // Validate same platform
                if (target.PlatformId != platformId)
                {
                    return CatalogErrors.PlatformMismatch();
                }

                newTitleId = target.Id;
            }
            else if (!string.IsNullOrWhiteSpace(command.NewTitleName))
            {
                // Create new title
                string normalizedName = TitleNormalizer.Normalize(command.NewTitleName);

                // Check if title already exists
                if (await _titleCreation.ExistsWithNormalizedNameAsync(platformId, normalizedName, ct))
                {
                    return CatalogErrors.TitleAlreadyExists(normalizedName);
                }

                string displayName = TitleNormalizer.ToDisplayName(command.NewTitleName);
                var creation = await _titleCreation.AddStagedAsync(
                    Title.CreateNew(platformId, displayName, normalizedName, _clock.GetUtcNow()), ct);
                // Assignment rows need this insertion's generated identity, before their own flush.
                await _unitOfWork.FlushAsync(ct);
                newTitleId = creation.ReadIdAfterFlush();
                titleCreated = true;
            }
            // else: unassign (newTitleId remains null)

            // 3. Update the game's TitleId
            if (newTitleId is int assignedTitleId)
            {
                await _titleSourceAssignments.UpsertAssignmentsAsync(
                    [new TitleSourceAssignment(command.DatGameId, assignedTitleId)], ct);
                // Orphan cleanup and the catalog rollup query the persisted assignments.
                await _unitOfWork.FlushAsync(ct);
            }
            else
            {
                await _titleSourceAssignments.ClearAssignmentsAsync([command.DatGameId], ct);
            }

            // 4. Check if old title is now empty and should be deleted
            if (oldTitleId.HasValue && oldTitleId != newTitleId)
            {
                if (!await _orphanCleanup.HasSourceEntriesAsync(oldTitleId.Value, ct))
                {
                    // Retain the orphaned title as UserOnly if it carries user state; otherwise delete.
                    oldTitleDeleted = await _orphanCleanup.DeleteOrRetainAsync(oldTitleId.Value, ct);
                    _logger.LogInformation(
                        "Orphaned title ID {TitleId} after moving game {GameId} was {Action}",
                        oldTitleId.Value,
                        command.DatGameId,
                        oldTitleDeleted ? "deleted" : "retained as UserOnly");
                }
            }

            if (oldTitleId != newTitleId)
            {
                // Commit source attribution, projection invalidation, and library scheduling as
                // one fact. Recovery observes Dirty if request-lifetime acceleration is skipped.
                await _catalogProjection.MarkPlatformDirtyAsync(
                    platformId,
                    ct,
                    new[] { oldTitleId, newTitleId }.OfType<int>().ToArray());
                await _libraryRepository.FlagForRematerializationByPlatformAsync(platformId, ct);
            }

            // Read the response, including the projection-owned local-payload fact, after the
            // in-transaction rollup and before commit. Carrying it across commit avoids a
            // post-commit cancellation turning durable success into an error response while
            // keeping the returned contract truthful.
            if (newTitleId is int responseTitleId)
            {
                newTitle = await _titleReader.GetSummaryAsync(responseTitleId, ct);
            }

            await transaction.CommitAsync(ct);
            commitSucceeded = true;

            // Best-effort acceleration only: durable Dirty state is the recovery contract.
            if (oldTitleId != newTitleId)
            {
                await TryRebuildCatalogAsync(platformId, ct);
            }
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            await transaction.DisposePreservingOutcomeAsync(commitSucceeded, failed, _logger,
                (error, acknowledged) => _logger.LogWarning(error,
                    "Game move transaction cleanup failed (game {GameId}; commit acknowledged: {CommitAcknowledged})",
                    command.DatGameId, acknowledged));
        }

        _logger.LogInformation(
            "Moved game {GameId} from title {OldTitleId} to {NewTitleId}. Created: {Created}, Deleted old: {Deleted}",
            command.DatGameId, oldTitleId, newTitleId, titleCreated, oldTitleDeleted);

        return new MoveGameResult(
            IdCoder.Encode(command.DatGameId),
            newTitle?.ToContract(systemKeys),
            titleCreated,
            oldTitleDeleted);
    }

    /// <summary>Locks the current and target titles in ID order; a missing one is the error.</summary>
    private async Task<Error?> LockTitlesAsync(MoveGameCommand command, CancellationToken ct)
    {
        foreach (int titleId in new[] { command.TitleId, command.TargetTitleId }.OfType<int>().Distinct().Order())
        {
            if (!await _titleLock.LockAsync(titleId, ct))
            {
                return CatalogErrors.TitleNotFound();
            }
        }

        return null;
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
