using Romd.Admin.Application.Source.Dat;
using Romd.Dat.Parsing.Models;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;

namespace Romd.Infrastructure.Dats.Format;

/// <summary>
///     Maps neutral parser results into ROMD application and domain types.
/// </summary>
public static class DatModelMapper
{
    public static DatMetadata ToMetadata(ParsedHeader header) => new()
    {
        Name = header.Name,
        Description = header.Description,
        DatType = header.Provenance switch
        {
            DatProvenance.NoIntro => DatType.NoIntro,
            DatProvenance.Redump => DatType.Redump,
            DatProvenance.Tosec => DatType.Tosec,
            DatProvenance.Mame => DatType.Mame,
            _ => DatType.Unknown
        },
        Version = header.Version,
        Author = header.Author,
        Url = header.Url ?? header.Homepage
    };

    /// <summary>
    ///     Builds a domain <see cref="DatGame" /> from a parsed entry.
    /// </summary>
    public static DatGame ToDatGame(ParsedGame parsed, int datFileId)
    {
        var game = DatGame.CreateNew(
            datFileId,
            parsed.Name,
            parsed.Description,
            parsed.Year,
            parsed.Manufacturer,
            parsed.CloneOf,
            parsed.RomOf,
            parsed.NameMetadata.Category,
            parsed.IsBios,
            parsed.NameMetadata.Region,
            parsed.NameMetadata.Language,
            parsed.NameMetadata.Revision);

        foreach (var rom in parsed.Roms)
        {
            game.AddRom(DatRom.CreateNew(
                rom.Name,
                rom.Size,
                ParseHash<Crc32>(rom.Crc, Crc32.TryParse),
                ParseHash<Md5>(rom.Md5, Md5.TryParse),
                ParseHash<Sha1>(rom.Sha1, Sha1.TryParse),
                rom.Status,
                rom.Serial));
        }

        foreach (var disk in parsed.Disks)
        {
            game.AddDisk(DatDisk.CreateNew(
                disk.Name,
                ParseHash<Sha1>(disk.Sha1, Sha1.TryParse),
                ParseHash<Md5>(disk.Md5, Md5.TryParse),
                disk.Status));
        }

        return game;
    }

    private delegate bool TryParseHashDelegate<T>(string? value, out T result) where T : struct;

    private static T? ParseHash<T>(string? value, TryParseHashDelegate<T> tryParse) where T : struct =>
        tryParse(value, out var result) ? result : null;
}
