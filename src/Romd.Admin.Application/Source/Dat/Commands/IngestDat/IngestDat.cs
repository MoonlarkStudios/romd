using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Matching;
using Romd.Domain.Source.Dat;
using Romd.Domain.Source.Dat.Parsing;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Source.Dat.Commands.IngestDat;

/// <summary>
///     Command to ingest a DAT file from a stream.
/// </summary>
public sealed record IngestDatCommand : ICommand<DatIngestResult>
{
    private IngestDatCommand() { }

    /// <summary>
    ///     The DAT file content stream.
    /// </summary>
    public required Stream FileStream { get; init; }

    /// <summary>
    ///     Original filename for reference.
    /// </summary>
    public required string OriginalFilename { get; init; }

    /// <summary>
    ///     Optional platform ID to associate with the DAT.
    /// </summary>
    public int? PlatformId { get; init; }

    /// <summary>
    ///     When set, the DAT is ingested as a <see cref="DatFileLifecycle.PendingActivation" />
    ///     version of this existing source instead of creating a new source that is Active at
    ///     birth. Used by the replace saga; activation is a separate command.
    /// </summary>
    public int? ReplacesDatSourceId { get; init; }

    /// <summary>Rejects queued replacement work whose reviewed baseline changed before ingestion.</summary>
    public int? ExpectedActiveDatId { get; init; }

    /// <summary>
    ///     Creates a validated command.
    /// </summary>
    public static ErrorOr<IngestDatCommand> Create(
        Stream fileStream,
        string originalFilename,
        int? platformId = null,
        int? replacesDatSourceId = null,
        int? expectedActiveDatId = null)
    {
        var errors = new List<Error>();

        if (fileStream is null)
        {
            errors.Add(Error.Validation("Command.NullStream", "File stream cannot be null"));
        }

        if (string.IsNullOrWhiteSpace(originalFilename))
        {
            errors.Add(Error.Validation("Command.InvalidFilename", "Original filename cannot be null or empty"));
        }

        if (platformId is <= 0)
        {
            errors.Add(Error.Validation("Command.InvalidPlatformId", "Platform ID must be positive if provided"));
        }

        if (replacesDatSourceId is <= 0)
        {
            errors.Add(Error.Validation(
                "Command.InvalidDatSourceId",
                "DAT source ID must be positive if provided"));
        }

        if (expectedActiveDatId is <= 0 || (expectedActiveDatId.HasValue && !replacesDatSourceId.HasValue))
        {
            errors.Add(Error.Validation("Command.InvalidBaseline", "A reviewed baseline requires an existing source and positive DAT ID."));
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        return new IngestDatCommand
        {
            FileStream = fileStream!,
            OriginalFilename = originalFilename!,
            PlatformId = platformId,
            ReplacesDatSourceId = replacesDatSourceId,
            ExpectedActiveDatId = expectedActiveDatId
        };
    }
}

public sealed class IngestDatCommandHandler : ICommandHandler<IngestDatCommand, DatIngestResult>
{
    private const int BatchSize = 1000;

    private readonly IBiosGrouper _biosGrouper;
    private readonly IDatReader _datReader;
    private readonly IFileStorageService _fileStorage;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILogger<IngestDatCommandHandler> _logger;
    private readonly IAdminEventOutbox _outbox;
    private readonly IPlatformHeaderResolver _platformHeaderResolver;
    private readonly IDatRepository _repository;
    private readonly ITempFileFactory _tempFileFactory;
    private readonly ITitleDerivationService _titleDerivation;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITaxonomyResolver<Region> _regionResolver;
    private readonly ITaxonomyResolver<GameLanguage> _languageResolver;
    private readonly ICatalogProjectionService _catalogProjection;
    private readonly ISourceLifecycle _sourceLifecycle;

    public IngestDatCommandHandler(
        IDatReader datReader,
        IDatRepository repository,
        ITitleDerivationService titleDerivation,
        ITempFileFactory tempFileFactory,
        IFileStorageService fileStorage,
        IBiosGrouper biosGrouper,
        IUnitOfWork unitOfWork,
        IAdminEventOutbox outbox,
        ILibraryRepository libraryRepository,
        ITaxonomyResolver<Region> regionResolver,
        ITaxonomyResolver<GameLanguage> languageResolver,
        ICatalogProjectionService catalogProjection,
        IPlatformHeaderResolver platformHeaderResolver,
        ILogger<IngestDatCommandHandler> logger,
        ISourceLifecycle sourceLifecycle)
    {
        _datReader = datReader;
        _repository = repository;
        _titleDerivation = titleDerivation;
        _tempFileFactory = tempFileFactory;
        _fileStorage = fileStorage;
        _biosGrouper = biosGrouper;
        _unitOfWork = unitOfWork;
        _outbox = outbox;
        _libraryRepository = libraryRepository;
        _regionResolver = regionResolver;
        _languageResolver = languageResolver;
        _catalogProjection = catalogProjection;
        _platformHeaderResolver = platformHeaderResolver;
        _logger = logger;
        _sourceLifecycle = sourceLifecycle;
    }

    public async Task<ErrorOr<DatIngestResult>> HandleAsync(IngestDatCommand command, CancellationToken ct = default)
    {
        FileStoreResult? fileResult = null;
        bool datCommitted = false;
        DatIngestResult? durableResult = null;

        try
        {
            await using var tempFile = await _tempFileFactory.CreateAsync(command.FileStream, ct);

            fileResult = await _fileStorage.StoreFromTempFileAsync(tempFile, ct);

            var existingDat = await _repository.GetByFileIdAsync(fileResult.File.Id, ct);
            if (existingDat is not null)
            {
                if (IsAlreadyIngestedReplacement(command, existingDat))
                {
                    // A resumed replace saga re-delivers the same content: the pending version
                    // already committed, so return it instead of re-ingesting the graph.
                    return new DatIngestResult { DatFile = existingDat, Warnings = [] };
                }

                return CatalogErrors.DatAlreadyExists(fileResult.File.Sha256);
            }

            DatMetadata metadata;
            await using (var stream = tempFile.OpenRead())
            {
                var metadataResult = await _datReader.ReadHeaderAsync(stream, ct);
                if (metadataResult.IsError)
                {
                    await CleanupStoredFileAsync(fileResult.File.Id, ct);
                    return metadataResult.Errors;
                }

                metadata = metadataResult.Value;
            }

            var platformId = command.PlatformId;
            if (platformId is null)
            {
                platformId = await _platformHeaderResolver.ResolvePlatformIdAsync(metadata.Name, ct);
                if (platformId is not null)
                {
                    _logger.LogInformation(
                        "Auto-routed DAT '{Name}' to platform {PlatformId} by header name",
                        metadata.Name,
                        platformId);
                }
            }

            var datFile = command.ReplacesDatSourceId is int replacesDatSourceId
                ? DatFile.CreatePendingVersion(
                    metadata.Name,
                    metadata.Description,
                    metadata.DatType,
                    command.OriginalFilename,
                    fileResult.File.Id,
                    replacesDatSourceId,
                    platformId,
                    metadata.Version,
                    metadata.Author,
                    metadata.Url)
                : DatFile.CreateNew(
                    metadata.Name,
                    metadata.Description,
                    metadata.DatType,
                    command.OriginalFilename,
                    fileResult.File.Id,
                    platformId,
                    metadata.Version,
                    metadata.Author,
                    metadata.Url);

            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);

            try
            {
                if (command.ExpectedActiveDatId is int expectedActiveId)
                {
                    await _repository.AcquireMutationWriteLockAsync(expectedActiveId, ct);
                    var active = await _repository.GetActiveBySourceIdAsync(command.ReplacesDatSourceId!.Value, ct);
                    if (active?.Id != expectedActiveId)
                        return Error.Conflict("DatReview.Stale", "The active catalog changed. Preview the update again before applying.");
                }

                if (command.ReplacesDatSourceId is null)
                {
                    // Fresh ingest creates the source anchor with the version Active at birth,
                    // staged in the same commit as the DAT row itself.
                    await _repository.AddStagedAsync(datFile, DatSource.CreateNew(), ct);
                }
                else
                {
                    // Replacement ingest attaches a PendingActivation version to the existing
                    // source; the partial unique index forbids a second pending version.
                    await _repository.AddVersionStagedAsync(datFile, ct);
                }

                await _unitOfWork.FlushAsync(ct);
                datFile = await _repository.GetByFileIdAsync(fileResult.File.Id, ct)
                    ?? throw new InvalidOperationException("Staged DAT was not available after flush.");

                int? catalogSourceId = null;
                IReadOnlyList<int>? formerlyLinkedTitleIds = null;
                if (platformId.HasValue)
                {
                    catalogSourceId = await _repository.GetCatalogSourceIdAsync(datFile.DatSourceId, ct);
                    formerlyLinkedTitleIds = await _sourceLifecycle.GetLinkedTitleIdsAsync(
                        catalogSourceId.Value,
                        ct);
                }

                StreamGamesResult streamResult;
                await using (var stream = tempFile.OpenRead())
                {
                    var gameStream = _datReader.StreamGamesAsync(stream, datFile.Id, ct);
                    streamResult = await StreamGamesToDatabaseAsync(gameStream, datFile.DatSourceId, platformId, ct);
                }

                await _repository.UpdateCountsAsync(
                    datFile.Id,
                    streamResult.GameCount,
                    streamResult.RomCount,
                    streamResult.DiskCount,
                    ct);

                datFile.UpdateStatistics(
                    streamResult.GameCount,
                    streamResult.RomCount,
                    streamResult.DiskCount);

                if (platformId.HasValue)
                {
                    // The platform's Dirty projection state becomes durable with the source
                    // mutation, together with the affected-library flag. The post-commit
                    // rebuild below stays best-effort acceleration and the worker recovery
                    // dispatcher converges if it never runs.
                    await _catalogProjection.RefreshCatalogSourcePayloadAsync(catalogSourceId!.Value, ct);
                    await _catalogProjection.MarkPlatformDirtyAsync(
                        platformId.Value,
                        ct,
                        formerlyLinkedTitleIds);
                    if (streamResult.TitleAssignmentCount > 0)
                    {
                        await _libraryRepository.FlagForRematerializationByPlatformAsync(platformId.Value, ct);
                    }
                }

                await EnqueueStatsChangedAsync(ct);
                await _unitOfWork.FlushAsync(ct);
                await transaction.CommitAsync(ct);
                datCommitted = true;
                durableResult = new DatIngestResult { DatFile = datFile, Warnings = streamResult.Warnings };

                // Rebuild the canonical catalog from the now-committed source as best-effort
                // acceleration. Dirty state and library scheduling are already durable.
                if (platformId.HasValue)
                {
                    await TryRebuildCatalogAsync(platformId.Value, ct);
                }

                if (streamResult.Warnings.Count > 0)
                {
                    _logger.LogWarning(
                        "DAT '{Name}' ingested with {WarningCount} warnings",
                        datFile.Name,
                        streamResult.Warnings.Count);
                }

                _logger.LogInformation(
                    "Ingested DAT '{Name}' with {GameCount} games, {RomCount} ROMs, {DiskCount} disks, {BiosCount} BIOS entries",
                    datFile.Name,
                    streamResult.GameCount,
                    streamResult.RomCount,
                    streamResult.DiskCount,
                    streamResult.BiosEntriesCreated);

                return durableResult;
            }
            catch (Exception ex) when (datCommitted)
            {
                _logger.LogWarning(
                    ex,
                    "DAT ingestion committed but transaction cleanup failed for file {Filename}",
                    command.OriginalFilename);
                return durableResult!;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(CancellationToken.None);

                var concurrent = await _repository.GetByFileIdAsync(fileResult.File.Id, CancellationToken.None);
                if (concurrent is not null)
                {
                    if (IsAlreadyIngestedReplacement(command, concurrent))
                    {
                        _logger.LogDebug(
                            ex,
                            "Concurrent replacement ingest converged on pending version {DatId}",
                            concurrent.Id);
                        return new DatIngestResult { DatFile = concurrent, Warnings = [] };
                    }

                    _logger.LogDebug(ex, "Concurrent duplicate detected for file {FileId}", fileResult.File.Id);
                    return CatalogErrors.DatAlreadyExists(fileResult.File.Sha256);
                }

                throw;
            }
        }
        catch (Exception ex) when (datCommitted)
        {
            _logger.LogWarning(
                ex,
                "DAT ingestion committed but transaction cleanup failed for file {Filename}",
                command.OriginalFilename);
            return durableResult!;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("DAT ingestion cancelled for {Filename}", command.OriginalFilename);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ingest DAT file {Filename}", command.OriginalFilename);
            if (fileResult is not null && !datCommitted)
            {
                await CleanupStoredFileAsync(fileResult.File.Id, CancellationToken.None);
            }

            throw;
        }
    }

    private static bool IsAlreadyIngestedReplacement(IngestDatCommand command, DatFile existingDat) =>
        command.ReplacesDatSourceId is int sourceId
        && existingDat.DatSourceId == sourceId
        && existingDat.Lifecycle == DatFileLifecycle.PendingActivation;

    private async Task CleanupStoredFileAsync(int fileId, CancellationToken ct)
    {
        try
        {
            await _fileStorage.DeleteIfUnreferencedAsync(fileId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup stored file {FileId}", fileId);
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
                "DAT ingestion committed but catalog rebuild acceleration failed for platform {PlatformId}; recovery will retry",
                platformId);
        }
    }

    private async Task EnqueueStatsChangedAsync(CancellationToken ct)
    {
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, ct);
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.CoverageStatsChanged, ct);
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, ct);
    }

    private async Task<StreamGamesResult> StreamGamesToDatabaseAsync(
        IAsyncEnumerable<ErrorOr<DatGame>> gameStream,
        int datSourceId,
        int? platformId,
        CancellationToken ct)
    {
        var warnings = new List<DatParseWarning>();

        // BIOS games (with assigned IDs) collected across batches, grouped once the stream ends.
        var biosGames = new List<(int GameId, string Name)>();

        int gameCount = 0;
        int romCount = 0;
        int diskCount = 0;
        int titleAssignmentCount = 0;

        int catalogSourceId = await _repository.GetCatalogSourceIdAsync(datSourceId, ct);

        // The derivation service yields exactly one assignment per claim, in claim order, so
        // a queue correlates each assignment back to the parsed game it answers. Games are
        // held in memory only (never staged on the context) between pulls, per the flush
        // protocol in docs/decisions/neutral-source-identity.md.
        var pendingGames = new Queue<GameWithTaxonomy>();

        async IAsyncEnumerable<TitleClaim> StreamClaimsAsync()
        {
            await foreach (var gameResult in gameStream.WithCancellation(ct))
            {
                if (gameResult.IsError)
                {
                    foreach (var error in gameResult.Errors)
                    {
                        warnings.Add(new DatParseWarning(error.Code, error.Description));
                        _logger.LogWarning("Parse warning: [{Code}] {Description}", error.Code, error.Description);
                    }

                    continue;
                }

                var game = gameResult.Value;

                // Resolve taxonomy tokens to IDs
                var regionIds = await _regionResolver.ResolveAsync(game.Region, ct);
                var languageIds = await _languageResolver.ResolveAsync(game.Language, ct);

                pendingGames.Enqueue(new GameWithTaxonomy(game, regionIds, languageIds));
                gameCount++;
                romCount += game.Roms.Count;
                diskCount += game.Disks.Count;

                yield return new TitleClaim(game.Name, game.Name, platformId, game.IsBios);
            }
        }

        var batch = new List<GameWithEntry>(BatchSize);

        // DAT uses UpsertAsync, not ReconcileAsync: the deletion arm for DAT entries is the
        // version-retention prune (DAT-side grace policy — superseded versions keep entries
        // alive until cleanup). Reconcile's stamp-based deletion serves providers without
        // payload references.
        await foreach (var assignmentResult in _titleDerivation
                           .UpsertAsync(catalogSourceId, StreamClaimsAsync(), ct)
                           .WithCancellation(ct))
        {
            var pending = pendingGames.Dequeue();

            if (assignmentResult.IsError)
            {
                foreach (var error in assignmentResult.Errors)
                {
                    warnings.Add(new DatParseWarning(error.Code, error.Description));
                    _logger.LogWarning(
                        "Derivation warning for '{GameName}': [{Code}] {Description}",
                        pending.Game.Name, error.Code, error.Description);
                }

                gameCount--;
                romCount -= pending.Game.Roms.Count;
                diskCount -= pending.Game.Disks.Count;
                continue;
            }

            var assignment = assignmentResult.Value;
            if (assignment.TitleId is not null)
            {
                titleAssignmentCount++;
            }

            batch.Add(new GameWithEntry(pending, assignment.SourceEntryId));

            if (batch.Count >= BatchSize)
            {
                await FlushBatchAsync(batch, biosGames, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await FlushBatchAsync(batch, biosGames, ct);
        }

        // BIOS entries don't become Titles; group them into the platform's BIOS catalog
        // (the firmware analog of title matching). Unrouted DATs are grouped on platform assignment.
        int biosEntriesCreated = 0;
        if (platformId.HasValue && biosGames.Count > 0)
        {
            biosEntriesCreated = await _biosGrouper.GroupAsync(platformId.Value, biosGames, ct);
        }

        return new StreamGamesResult(
            gameCount,
            romCount,
            diskCount,
            titleAssignmentCount,
            biosEntriesCreated,
            warnings);
    }

    private async Task FlushBatchAsync(
        List<GameWithEntry> batch,
        List<(int GameId, string Name)> biosGames,
        CancellationToken ct)
    {
        var games = batch
            .Select(b => new DatGameWithEntry(b.Item.Game, b.SourceEntryId))
            .ToList();
        var assignedIds = await _repository.AddGamesBatchAsync(games, ct);

        for (int i = 0; i < batch.Count; i++)
        {
            if (batch[i].Item.Game.IsBios)
            {
                biosGames.Add((assignedIds[i], batch[i].Item.Game.Name));
            }
        }

        await _repository.LinkDatRomsToExistingRomFilesAsync(assignedIds, ct);

        // Junction rows for regions and languages — index-aligned, no reference-identity lookup
        var regionMappings = new List<(int GameId, int RegionId)>();
        var languageMappings = new List<(int GameId, int LanguageId)>();

        for (int i = 0; i < batch.Count; i++)
        {
            var gameId = assignedIds[i];

            foreach (var rid in batch[i].Item.RegionIds)
                regionMappings.Add((gameId, rid));

            foreach (var lid in batch[i].Item.LanguageIds)
                languageMappings.Add((gameId, lid));
        }

        await _repository.AddGameRegionsBatchAsync(regionMappings, ct);
        await _repository.AddGameLanguagesBatchAsync(languageMappings, ct);
    }

    private readonly record struct GameWithEntry(GameWithTaxonomy Item, int SourceEntryId);

    private readonly record struct GameWithTaxonomy(
        DatGame Game,
        IReadOnlyList<int> RegionIds,
        IReadOnlyList<int> LanguageIds);

    private sealed record StreamGamesResult(
        int GameCount,
        int RomCount,
        int DiskCount,
        int TitleAssignmentCount,
        int BiosEntriesCreated,
        IReadOnlyList<DatParseWarning> Warnings);
}
