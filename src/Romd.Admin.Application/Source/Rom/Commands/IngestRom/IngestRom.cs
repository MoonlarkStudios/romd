using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Domain.Hashing;
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
    bool ArchiveOnly = false) : ICommand<RomIngestResult>;

public sealed class IngestRomCommandHandler : ICommandHandler<IngestRomCommand, RomIngestResult>
{
    private readonly IDatRepository _datRepository;
    private readonly IFileStorageService _fileStorage;
    private readonly IHashingService _hashingService;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILogger<IngestRomCommandHandler> _logger;
    private readonly IAdminEventOutbox _outbox;
    private readonly IRomCatalogMatchReader _romCatalogMatches;
    private readonly IRomRepository _romRepository;
    private readonly ITempFileFactory _tempFileFactory;
    private readonly ITitleRepository _titleRepository;
    private readonly ITitlePayloadAvailabilityProjection _payloadAvailability;
    private readonly IRomPayloadAssertionImpactReader _payloadAssertionImpact;
    private readonly IUnitOfWork _unitOfWork;

    public IngestRomCommandHandler(
        IRomRepository romRepository,
        IDatRepository datRepository,
        IRomCatalogMatchReader romCatalogMatches,
        IFileStorageService fileStorage,
        ITempFileFactory tempFileFactory,
        IHashingService hashingService,
        IUnitOfWork unitOfWork,
        IAdminEventOutbox outbox,
        ILibraryRepository libraryRepository,
        ITitleRepository titleRepository,
        IRomPayloadAssertionImpactReader payloadAssertionImpact,
        ITitlePayloadAvailabilityProjection payloadAvailability,
        ILogger<IngestRomCommandHandler> logger)
    {
        _romRepository = romRepository;
        _datRepository = datRepository;
        _romCatalogMatches = romCatalogMatches;
        _fileStorage = fileStorage;
        _tempFileFactory = tempFileFactory;
        _hashingService = hashingService;
        _unitOfWork = unitOfWork;
        _outbox = outbox;
        _libraryRepository = libraryRepository;
        _titleRepository = titleRepository;
        _payloadAssertionImpact = payloadAssertionImpact;
        _payloadAvailability = payloadAvailability;
        _logger = logger;
    }

    public async Task<ErrorOr<RomIngestResult>> HandleAsync(
        IngestRomCommand command,
        CancellationToken ct = default)
    {
        try
        {
            await using var tempFile = await _tempFileFactory.CreateAsync(command.FileStream, ct);

            var datHashes = await _hashingService.ComputeDatHashesAsync(tempFile.OpenRead(), ct);

            var existing = await _romRepository.GetBySha1Async(datHashes.Sha1, ct);
            if (existing is not null)
            {
                var existingMatch = await LinkExistingRomToMatchedDatRomsAsync(
                    existing, datHashes.Sha1, command.ArchiveOnly, ct);

                _logger.LogDebug(
                    "ROM already exists: {Sha1} (ID: {Id}), returning existing",
                    existing.Sha1.ToShortHex(), existing.Id);

                return new RomIngestResult
                {
                    RomFile = existing,
                    IsNew = false,
                    MatchedTitleIds = existingMatch.TitleIds,
                    PlatformId = existingMatch.PrimaryPlatformId
                };
            }

            var catalogMatch = await _romCatalogMatches.ReadAsync(datHashes.Sha1, ct);

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
                        tempFile.FileSize);
                }

                _logger.LogInformation(
                    "Allowing unidentified ROM: {Filename} (SHA1: {Sha1}) - AllowUnidentified=true",
                    command.OriginalFilename, datHashes.Sha1.ToShortHex());
            }

            var fileResult = await _fileStorage.StoreFromTempFileAsync(tempFile, ct);

            var romFile = RomFile.CreateNew(
                command.OriginalFilename,
                fileResult.File.Id,
                fileResult.File.Size,
                datHashes.Sha1,
                datHashes.Md5,
                datHashes.Crc32);

            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);

            try
            {
                await _romRepository.AddStagedAsync(romFile, ct);
                await _unitOfWork.FlushAsync(ct);
                romFile = await _romRepository.GetBySha1Async(datHashes.Sha1, ct)
                    ?? throw new InvalidOperationException("Staged ROM was not available after flush.");

                int linkedCount = await LinkAndApplyTrackingAsync(
                    datHashes.Sha1, romFile.Id, catalogMatch, command.ArchiveOnly, ct);

                await EnqueueStatsChangedAsync(ct);
                await _unitOfWork.FlushAsync(ct);
                await transaction.CommitAsync(ct);

                _logger.LogInformation(
                    "Ingested ROM: {Filename} ({Size} bytes, SHA1: {Sha1}, Titles: {TitleCount}, BiosPlatforms: {BiosPlatformCount}, Linked: {LinkedCount}, ArchiveOnly: {ArchiveOnly})",
                    command.OriginalFilename, romFile.Size, romFile.Sha1.ToShortHex(),
                    catalogMatch.TitleIds.Count, catalogMatch.BiosPlatformIds.Count, linkedCount,
                    command.ArchiveOnly);

                return new RomIngestResult
                {
                    RomFile = romFile,
                    IsNew = true,
                    MatchedTitleIds = catalogMatch.TitleIds,
                    PlatformId = catalogMatch.PrimaryPlatformId
                };
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(CancellationToken.None);

                if (await _romRepository.ExistsBySha1Async(datHashes.Sha1, CancellationToken.None))
                {
                    _logger.LogDebug(ex, "Concurrent duplicate detected for SHA1 {Sha1}", datHashes.Sha1.ToShortHex());

                    var concurrentExisting =
                        await _romRepository.GetBySha1Async(datHashes.Sha1, CancellationToken.None);
                    var concurrentMatch = await LinkExistingRomToMatchedDatRomsAsync(
                        concurrentExisting!, datHashes.Sha1, command.ArchiveOnly, CancellationToken.None);
                    return new RomIngestResult
                    {
                        RomFile = concurrentExisting!,
                        IsNew = false,
                        MatchedTitleIds = concurrentMatch.TitleIds,
                        PlatformId = concurrentMatch.PrimaryPlatformId
                    };
                }

                throw;
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Storage failure while ingesting ROM file '{Filename}'", command.OriginalFilename);
            return CatalogErrors.RomStorageFailed(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to ingest ROM file '{Filename}'", command.OriginalFilename);
            return CatalogErrors.RomIngestFailed(ex.Message);
        }
    }

    private async Task<RomCatalogMatch> LinkExistingRomToMatchedDatRomsAsync(
            RomFile romFile,
            Sha1 sha1,
            bool archiveOnly,
            CancellationToken ct)
    {
        var catalogMatch = await _romCatalogMatches.ReadAsync(sha1, ct);

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        try
        {
            int linkedCount = await LinkAndApplyTrackingAsync(
                sha1, romFile.Id, catalogMatch, archiveOnly, ct);
            if (linkedCount > 0 || (!archiveOnly && catalogMatch.TitleIds.Count > 0))
            {
                await EnqueueStatsChangedAsync(ct);
                await _unitOfWork.FlushAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return catalogMatch;
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
                await _libraryRepository.FlagForRematerializationByPlatformAsync(platformId, ct);
            }
        }

        if (!archiveOnly && catalogMatch.TitleIds.Count > 0)
        {
            await _titleRepository.MarkTrackedAsync(catalogMatch.TitleIds, ct);
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
