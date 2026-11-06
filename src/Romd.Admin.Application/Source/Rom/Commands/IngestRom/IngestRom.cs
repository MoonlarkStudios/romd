using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Commands.IngestRom;

/// <summary>
///     Command to ingest a ROM file into the library.
/// </summary>
/// <param name="FileStream">The ROM file stream.</param>
/// <param name="OriginalFilename">The original filename.</param>
/// <param name="AllowUnidentified">
///     If true, allows ROMs that don't match any DAT entry.
///     Default is false (strict mode - require DAT match).
///     Typically set to true only for Manager+ users.
/// </param>
public sealed record IngestRomCommand(
    Stream FileStream,
    string OriginalFilename,
    bool AllowUnidentified = false,
    bool ArchiveOnly = false,
    bool TrackedOnly = false,
    UploadItemContext? UploadItem = null) : ICommand<RomIngestResult>;

public sealed class IngestRomCommandHandler : ICommandHandler<IngestRomCommand, RomIngestResult>
{
    private readonly IDatRepository _datRepository;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILogger<IngestRomCommandHandler> _logger;
    private readonly IAdminEventOutbox _outbox;
    private readonly IRomCatalogMatchReader _romCatalogMatches;
    private readonly IRomRepository _romRepository;
    private readonly ITempFileFactory _tempFileFactory;
    private readonly ITrackedTitleReader _trackedTitles;
    private readonly ITrackedTitleRepository _tracking;
    private readonly ITitlePayloadAvailabilityProjection _payloadAvailability;
    private readonly IRomPayloadAssertionImpactReader _payloadAssertionImpact;
    private readonly IRomPreparationService _preparation;
    private readonly IRomCdIngestor _cdIngestor;
    private readonly RomRepresentationPublisher _publisher;
    private readonly TimeProvider _clock;

    public IngestRomCommandHandler(
        IRomRepository romRepository,
        IDatRepository datRepository,
        IRomCatalogMatchReader romCatalogMatches,
        ITempFileFactory tempFileFactory,
        IRomPreparationService preparation,
        IRomCdIngestor cdIngestor,
        RomRepresentationPublisher publisher,
        IAdminEventOutbox outbox,
        ILibraryRepository libraryRepository,
        ITrackedTitleReader trackedTitles,
        ITrackedTitleRepository tracking,
        IRomPayloadAssertionImpactReader payloadAssertionImpact,
        ITitlePayloadAvailabilityProjection payloadAvailability,
        ILogger<IngestRomCommandHandler> logger,
        TimeProvider clock)
    {
        _romRepository = romRepository;
        _datRepository = datRepository;
        _romCatalogMatches = romCatalogMatches;
        _tempFileFactory = tempFileFactory;
        _outbox = outbox;
        _libraryRepository = libraryRepository;
        _trackedTitles = trackedTitles;
        _tracking = tracking;
        _payloadAssertionImpact = payloadAssertionImpact;
        _payloadAvailability = payloadAvailability;
        _logger = logger;
        _preparation = preparation;
        _cdIngestor = cdIngestor;
        _publisher = publisher;
        _clock = clock;
    }

    public async Task<ErrorOr<RomIngestResult>> HandleAsync(
        IngestRomCommand command,
        CancellationToken ct = default)
    {
        IRomPreparationAdmission? admission = null;
        try
        {
            await using var tempFile = await _tempFileFactory.CreateAsync(command.FileStream, ct,
                magic => _preparation.RequiresDatHashes(magic, command.OriginalFilename),
                async (magic, token) => admission = await _preparation.AdmitAsync(magic, command.OriginalFilename, token),
                selectPrefixLength: prefix => _preparation.SelectPrefixLength(prefix, command.OriginalFilename));

            var preparation = admission is null
                ? await _preparation.PrepareAsync(tempFile, command.OriginalFilename, ct)
                : await admission.PrepareAsync(tempFile, command.OriginalFilename, ct);
            admission?.Dispose(); // Inspection finished; publication does not hold scarce media capacity.
            admission = null;
            if (preparation.IsError)
            {
                if (preparation.Errors.Any(error => error.Code == "Rom.Media.Containment"))
                    tempFile.Quarantine();
                return preparation.Errors;
            }
            await using var observation = preparation.Value;
            if (observation is PreparedRom.Cd cd)
            {
                var imported = await _cdIngestor.IngestAsync(cd, new(command.OriginalFilename, command.ArchiveOnly, command.TrackedOnly, command.UploadItem), ct);
                return imported.IsError ? imported.Errors : imported.Value;
            }
            var prepared = observation as PreparedRom.WholeFile ?? throw new InvalidDataException("Preparation returned no typed media observation.");
            var datHashes = prepared.Hashes;

            // Filter before both duplicate relinking and CAS writes. Shared content may match
            // untracked titles too, but this mode must never expand tracking intent.
            var catalogMatch = await _romCatalogMatches.ReadAsync(datHashes.Sha1, ct);
            if (command.TrackedOnly && (catalogMatch.TitleIds.Count == 0
                || !await _trackedTitles.HasTrackedAsync(catalogMatch.TitleIds, ct)))
            {
                return CatalogErrors.UntrackedRom();
            }
            bool archiveOnly = command.ArchiveOnly || command.TrackedOnly;

            var existing = await _romRepository.GetBySha1Async(datHashes.Sha1, ct);
            if (existing is not null)
            {
                var existingMatch = await LinkExistingRomToMatchedDatRomsAsync(
                    existing, datHashes.Sha1, catalogMatch, archiveOnly, ct, prepared, command.UploadItem, tempFile.FileSize, catalogMatch.HasCatalogMatch || command.AllowUnidentified);
                if (existingMatch.IsError)
                    return existingMatch.Errors;

                _logger.LogDebug(
                    "ROM already exists: {Sha1} (ID: {Id}), returning existing",
                    existing.Sha1.ToShortHex(), existing.Id);

                return new RomIngestResult.WholeFile
                {
                    RomFile = existingMatch.Value,
                    IsNew = false,
                    MatchedTitleIds = catalogMatch.TitleIds,
                    PlatformId = catalogMatch.PrimaryPlatformId
                };
            }

            if (!catalogMatch.HasCatalogMatch)
            {
                if (!command.AllowUnidentified)
                {
                    _logger.LogWarning(
                        "Rejected unidentified ROM: {Filename} (SHA1: {Sha1}) - no DAT match",
                        command.OriginalFilename, datHashes.Sha1.ToShortHex());

                    return CatalogErrors.UnidentifiedRom(
                        datHashes.Sha1,
                        datHashes.Md5,
                        datHashes.Crc32,
                        datHashes.BytesRead);
                }

                _logger.LogInformation(
                    "Allowing unidentified ROM: {Filename} (SHA1: {Sha1}) - AllowUnidentified=true",
                    command.OriginalFilename, datHashes.Sha1.ToShortHex());
            }

            try
            {
                var romFile = await _publisher.PublishAsync(prepared, null, true,
                    async (rom, _, token) =>
                    {
                        await LinkAndApplyTrackingAsync(datHashes.Sha1, rom.Id, catalogMatch, archiveOnly, token);
                        await EnqueueStatsChangedAsync(token);
                    }, rom => command.UploadItem is { } item ? JobItem.ForRom(item.JobId, item.RelativePath,
                        tempFile.FileSize, RomIngestOutcome.Ingested, rom.Id, catalogMatch.TitleIds,
                        catalogMatch.PrimaryPlatformId, null, createdAt: _clock.GetUtcNow(), archiveOnly) : null, ct);
                if (romFile.IsError)
                    return romFile.Errors;
                return new RomIngestResult.WholeFile
                {
                    RomFile = romFile.Value,
                    IsNew = true,
                    MatchedTitleIds = catalogMatch.TitleIds,
                    PlatformId = catalogMatch.PrimaryPlatformId
                };
            }
            catch (Exception ex) when (IngestionFailure.Classify(ex) == IngestionFailureKind.Permanent)
            {
                // A known rollback permits concurrent duplicate recovery in production too.
                var concurrent = await _romRepository.GetBySha1Async(datHashes.Sha1, ct);
                if (concurrent is null)
                    throw;
                var recovered = await LinkExistingRomToMatchedDatRomsAsync(concurrent, datHashes.Sha1, catalogMatch,
                    archiveOnly, ct, prepared, command.UploadItem, tempFile.FileSize, catalogMatch.HasCatalogMatch || command.AllowUnidentified);
                if (recovered.IsError)
                    return recovered.Errors;
                return new RomIngestResult.WholeFile
                {
                    RomFile = recovered.Value,
                    IsNew = false,
                    MatchedTitleIds = catalogMatch.TitleIds,
                    PlatformId = catalogMatch.PrimaryPlatformId
                };
            }
        }
        catch (IOException ex) when (IngestionFailure.Classify(ex) == IngestionFailureKind.Permanent)
        {
            _logger.LogError(ex, "Storage failure while ingesting ROM file '{Filename}'", command.OriginalFilename);
            return CatalogErrors.RomStorageFailed(ex.Message);
        }
        catch (Exception ex) when (IngestionFailure.Classify(ex) == IngestionFailureKind.Permanent)
        {
            _logger.LogError(ex, "Failed to ingest ROM file '{Filename}'", command.OriginalFilename);
            return CatalogErrors.RomIngestFailed(ex.Message);
        }
        finally { admission?.Dispose(); }
    }

    private async Task<ErrorOr<RomFile>> LinkExistingRomToMatchedDatRomsAsync(
            RomFile romFile,
            Sha1 sha1,
            RomCatalogMatch catalogMatch,
            bool archiveOnly,
            CancellationToken ct,
            PreparedRom.WholeFile prepared,
            UploadItemContext? uploadItem,
            long sourceSize,
            bool canRetain)
    {
        if (romFile.ContentSha256 is { } knownHash && knownHash != prepared.ContentSha256)
            throw new InvalidDataException("A ROM with the same DAT hash has a different logical SHA-256.");

        return await _publisher.PublishAsync(prepared, romFile, canRetain,
            async (rom, representationChanged, token) =>
            {
                int linkedCount = await LinkAndApplyTrackingAsync(sha1, rom.Id, catalogMatch, archiveOnly, token);
                if (representationChanged || linkedCount > 0 || (!archiveOnly && catalogMatch.TitleIds.Count > 0))
                    await EnqueueStatsChangedAsync(token);
            }, rom => uploadItem is { } item ? JobItem.ForRom(item.JobId, item.RelativePath,
                sourceSize, RomIngestOutcome.Deduplicated, rom.Id, catalogMatch.TitleIds,
                catalogMatch.PrimaryPlatformId, null, createdAt: _clock.GetUtcNow(), archiveOnly) : null, ct);
    }

    /// <summary>
    ///     Links DatRoms for the supplied SHA-1 to the ROM file, flags affected libraries, and
    ///     idempotently tracks every matched title unless this is an archive-only upload. Must run
    ///     inside an open transaction.
    /// </summary>
    private async Task<int> LinkAndApplyTrackingAsync(
        Sha1 sha1,
        int romFileId,
        RomCatalogMatch catalogMatch,
        bool archiveOnly,
        CancellationToken ct)
    {
        int linkedCount = await _datRepository.LinkDatRomsToRomFileAsync(sha1, romFileId, ct);
        var affectedSourceEntryIds = await _payloadAssertionImpact
            .ReadSourceEntryIdsBySha1Async(sha1, ct);
        if (affectedSourceEntryIds.Count > 0)
        {
            await _payloadAvailability.RefreshSourceEntryPayloadAssertionsAsync(affectedSourceEntryIds, ct);
        }
        if (linkedCount > 0)
        {
            foreach (int platformId in catalogMatch.TitlePlatformIds)
            {
                await _libraryRepository.MarkUpdatesAvailableByPlatformAsync(platformId, ct);
            }
        }

        if (!archiveOnly && catalogMatch.TitleIds.Count > 0)
        {
            await _tracking.TrackByTitleIdsAsync(catalogMatch.TitleIds, ct);
        }

        return linkedCount;
    }

    private async Task EnqueueStatsChangedAsync(CancellationToken ct)
    {
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, ct);
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.CoverageStatsChanged, ct);
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, ct);
    }
}
