using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Catalog.Commands.SetSourceStatus;

/// <summary>
///     Command to transition a catalog source's lifecycle status.
/// </summary>
public sealed record SetSourceStatusCommand : ICommand<SourceStatusResult>
{
    /// <summary>The DAT addressed by the admin surface; the handler resolves its catalog source.</summary>
    public required int DatId { get; init; }

    public required CatalogSourceStatus Status { get; init; }

    private SetSourceStatusCommand() { }

    /// <summary>
    ///     Creates a validated command. The status arrives as the enum member name
    ///     (named-string enums at the API boundary).
    /// </summary>
    public static ErrorOr<SetSourceStatusCommand> Create(int datId, string status)
    {
        if (datId <= 0)
        {
            return Error.Validation("Command.InvalidDatId", "DAT ID must be positive");
        }

        // Exact ordinal membership: Enum.TryParse is lenient (numeric strings, and
        // comma-composed names like "Active, Discontinued" parse as flag sums that can
        // collide with defined values). The boundary speaks the three names only.
        if (!Enum.GetNames<CatalogSourceStatus>().Contains(status, StringComparer.Ordinal))
        {
            return Error.Validation(
                "Command.InvalidSourceStatus",
                $"Unknown source status '{status}'. Valid values: " +
                string.Join(", ", Enum.GetNames<CatalogSourceStatus>()));
        }

        return new SetSourceStatusCommand { DatId = datId, Status = Enum.Parse<CatalogSourceStatus>(status) };
    }
}

/// <summary>The status after the call, and whether this call changed it.</summary>
public sealed record SourceStatusResult(CatalogSourceStatus Status, bool Changed);

/// <summary>
///     Transitions a source's status and converges downstream state in one commit: the
///     source's entry platforms are marked catalog-Dirty and their libraries flagged for
///     rematerialization, so stale materialized grants cannot outlive the lifecycle window
///     (#160 review invariant). The worker's recovery dispatcher rebuilds Dirty platforms;
///     materialization is gated until they are Clean. Status never blocks admin mutations
///     (docs/decisions/neutral-source-identity.md).
/// </summary>
public sealed class SetSourceStatusCommandHandler : ICommandHandler<SetSourceStatusCommand, SourceStatusResult>
{
    private readonly IDatRepository _datRepository;
    private readonly ISourceLifecycle _sourceLifecycle;
    private readonly ICatalogProjectionService _catalogProjection;
    private readonly ILibraryRepository _libraryRepository;
    private readonly IAdminEventOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SetSourceStatusCommandHandler> _logger;

    public SetSourceStatusCommandHandler(
        IDatRepository datRepository,
        ISourceLifecycle sourceLifecycle,
        ICatalogProjectionService catalogProjection,
        ILibraryRepository libraryRepository,
        IAdminEventOutbox outbox,
        IUnitOfWork unitOfWork,
        ILogger<SetSourceStatusCommandHandler> logger)
    {
        _datRepository = datRepository;
        _sourceLifecycle = sourceLifecycle;
        _catalogProjection = catalogProjection;
        _libraryRepository = libraryRepository;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<ErrorOr<SourceStatusResult>> HandleAsync(
        SetSourceStatusCommand command,
        CancellationToken ct = default)
    {
        try
        {
            var existing = await _datRepository.GetByIdAsync(command.DatId, ct);
            if (existing is null)
            {
                return CatalogErrors.DatNotFound(command.DatId);
            }

            int catalogSourceId = await _datRepository.GetCatalogSourceIdAsync(existing.DatSourceId, ct);

            var current = await _sourceLifecycle.GetStatusAsync(catalogSourceId, ct);
            if (current is null)
            {
                return CatalogErrors.SourceNotFound(catalogSourceId);
            }

            if (current == command.Status)
            {
                return new SourceStatusResult(command.Status, Changed: false);
            }

            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);

            await _datRepository.AcquireMutationWriteLockAsync(command.DatId, ct);
            await _catalogProjection.RefreshCatalogSourcePayloadAsync(catalogSourceId, ct);
            await _sourceLifecycle.PreserveOwnedTitleIdentitiesAsync(catalogSourceId, ct);
            await _sourceLifecycle.SetStatusAsync(catalogSourceId, command.Status, ct);

            var platformIds = await _sourceLifecycle.GetEntryPlatformIdsAsync(catalogSourceId, ct);
            foreach (int platformId in platformIds)
            {
                await _catalogProjection.MarkCatalogSourceDirtyAsync(platformId, catalogSourceId, ct);
                await _libraryRepository.FlagForRematerializationByPlatformAsync(platformId, ct);
            }

            await _outbox.EnqueueAsync(AdminRealtimeEventTypes.CoverageStatsChanged, ct);
            await _outbox.EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, ct);

            await transaction.CommitAsync(ct);

            _logger.LogInformation(
                "Catalog source {CatalogSourceId} status {OldStatus} -> {NewStatus}; {PlatformCount} platform(s) marked for convergence",
                catalogSourceId, current, command.Status, platformIds.Count);

            return new SourceStatusResult(command.Status, Changed: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Failed to set status {Status} for DAT {DatId}",
                command.Status, command.DatId);
            return CatalogErrors.DatabaseFailed(ex.Message);
        }
    }
}
