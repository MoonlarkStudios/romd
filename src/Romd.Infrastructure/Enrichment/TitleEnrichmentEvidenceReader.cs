using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Persistence.Queries;
using Romd.Persistence;

namespace Romd.Infrastructure.Enrichment;

/// <summary>
///     Reads enrichment evidence from the current DAT-backed catalog representation.
/// </summary>
public sealed class TitleEnrichmentEvidenceReader(RomdDbContext context) : ITitleEnrichmentEvidenceReader
{
    public async Task<IReadOnlyList<TitleEnrichmentEvidence>> ReadAsync(
        int titleId,
        CancellationToken cancellationToken = default)
    {
        var rows = await (
                from link in context.EffectiveTitleSourceLinks().AsNoTracking()
                join game in context.DatGames.AsNoTracking() on link.SourceEntryId equals game.SourceEntryId
                join rom in context.DatRoms.AsNoTracking() on game.Id equals rom.DatGameId into gameRoms
                from rom in gameRoms.DefaultIfEmpty()
                where link.TitleId == titleId && !game.IsBios
                orderby game.Id, rom.Id
                select new
                {
                    GameId = game.Id,
                    game.Name,
                    game.Year,
                    game.Manufacturer,
                    game.Region,
                    game.Revision,
                    game.DevelopmentStatus,
                    RomId = (int?)rom.Id,
                    rom.RomFileId,
                    rom.Sha1,
                    rom.Md5,
                    Crc32 = rom.Crc,
                    Size = (long?)rom.Size
                })
            .ToListAsync(cancellationToken);

        var evidence = new List<TitleEnrichmentEvidence>();
        foreach (var gameRows in rows.GroupBy(row => row.GameId))
        {
            var game = gameRows.First();
            var ownedRomHashes = gameRows
                .Where(row => row.RomFileId.HasValue)
                .Select(row => new OwnedRomHashEvidence(
                    row.Sha1,
                    row.Md5,
                    row.Crc32,
                    row.Size!.Value))
                .ToList();

            evidence.Add(new TitleEnrichmentEvidence(
                game.Name,
                game.Year,
                game.Manufacturer,
                game.Region,
                game.Revision,
                game.DevelopmentStatus,
                gameRows.Any(row => row.RomId.HasValue),
                ownedRomHashes));
        }

        return evidence;
    }
}
