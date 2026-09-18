using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

public sealed class ConsumerBiosRepository(RomdDbContext context) : IConsumerBiosRepository
{
    private static readonly string ValidConfigurationState = LibraryConfigurationState.Valid.ToString();
    private readonly LiveConsumerLibraryQuery _liveConsumerLibrary = new(context);

    public Task<ConsumerLibraryReadResult<ConsumerPlatformBios>> GetPlatformBiosAsync(
        ConsumerPlatformBiosRequest request,
        CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<ConsumerPlatformBios>(
            request.Scope,
            (libraryId, token) => GetPlatformBiosForLibraryAsync(
                libraryId,
                request.PlatformShortName,
                token),
            ct);

    private async Task<ConsumerLibraryProjectionResult<ConsumerPlatformBios>> GetPlatformBiosForLibraryAsync(
        int libraryId,
        string platformShortName,
        CancellationToken ct)
    {
        var platform = await context.Platforms
            .AsNoTracking()
            .Where(platform => platform.CanonicalKey == platformShortName)
            .Select(platform => new { platform.Id, platform.CanonicalKey })
            .FirstOrDefaultAsync(ct);

        if (platform is null)
        {
            return new ConsumerLibraryProjectionResult<ConsumerPlatformBios>.ItemNotFound();
        }

        // The platform is exposed to the consumer when their valid library owns and exposes at least
        // one materialized release of it — the same access predicate the release-manifest path uses.
        bool isExposed = await context.MaterializedLibraryReleases
            .AsNoTracking()
            .AnyAsync(release =>
                    release.LibraryId == libraryId &&
                    release.PlatformId == platform.Id &&
                    release.IsOwned &&
                    release.IsExposed &&
                    context.Libraries.Any(library =>
                        library.Id == release.LibraryId &&
                        library.ConfigurationState == ValidConfigurationState),
                ct);

        if (!isExposed)
        {
            return new ConsumerLibraryProjectionResult<ConsumerPlatformBios>.ItemNotFound();
        }

        var files = await LoadBiosFilesAsync(platform.Id, ct);

        return new ConsumerLibraryProjectionResult<ConsumerPlatformBios>.Found(
            new ConsumerPlatformBios(platform.Id, platform.CanonicalKey!, IsExposed: true, files));
    }

    private async Task<IReadOnlyList<ConsumerBiosFile>> LoadBiosFilesAsync(int platformId, CancellationToken ct)
    {
        // Pull every DAT ROM mapped into the platform's BIOS groups. Volume is tiny (a platform has a
        // handful of BIOS files), so deduping the same firmware listed across DAT revisions happens
        // in memory, mirroring the admin-side BiosRepository.
        var romRows = await (
                from bios in context.Bios.AsNoTracking()
                where bios.PlatformId == platformId
                join mapping in context.BiosGameMappings.AsNoTracking()
                    on bios.Id equals mapping.BiosId
                join game in context.DatGames.AsNoTracking()
                    on mapping.DatGameId equals game.Id
                join entry in context.EffectiveSourceEntries()
                    on game.SourceEntryId equals entry.Id
                join rom in context.DatRoms.AsNoTracking()
                    on mapping.DatGameId equals rom.DatGameId
                select new RomRow(bios.Id, bios.Name, rom.Name, rom.Size, rom.Sha1, rom.Md5, rom.RomFileId))
            .ToListAsync(ct);

        var ownedRomFileIds = romRows
            .Where(row => row.RomFileId.HasValue)
            .Select(row => row.RomFileId!.Value)
            .Distinct()
            .ToList();

        var ownedByRomFileId = ownedRomFileIds.Count == 0
            ? new Dictionary<int, OwnedFile>()
            : await (
                    from romFile in context.RomFiles.AsNoTracking()
                    where ownedRomFileIds.Contains(romFile.Id)
                    join storedFile in context.Files.AsNoTracking()
                        on romFile.FileId equals storedFile.Id
                    select new OwnedFile(romFile.Id, storedFile.Id, storedFile.Sha256, storedFile.Size))
                .ToDictionaryAsync(owned => owned.RomFileId, ct);

        return romRows
            .GroupBy(row => (row.BiosId, Key: row.Sha1?.ToString() ?? $"name:{row.FileName}"))
            .Select(group => group.FirstOrDefault(row => row.RomFileId.HasValue) ?? group.First())
            .Select(row =>
            {
                OwnedFile? owned = row.RomFileId is { } romFileId
                    && ownedByRomFileId.TryGetValue(romFileId, out var match)
                        ? match
                        : null;

                return new ConsumerBiosFile(
                    row.BiosId,
                    row.BiosName,
                    row.FileName,
                    row.Size,
                    row.Sha1,
                    row.Md5,
                    owned?.FileId,
                    owned?.Sha256,
                    owned?.ContentSizeBytes);
            })
            .OrderBy(file => file.BiosName, StringComparer.Ordinal)
            .ThenBy(file => file.FileName, StringComparer.Ordinal)
            .ToList();
    }

    private sealed record RomRow(
        int BiosId,
        string BiosName,
        string FileName,
        long Size,
        Sha1? Sha1,
        Md5? Md5,
        int? RomFileId);

    private sealed record OwnedFile(
        int RomFileId,
        int FileId,
        Sha256 Sha256,
        long ContentSizeBytes);
}
