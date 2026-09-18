using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Domain.Source.Dat;

/// <summary>
///     Represents a ROM file entry within a DAT game.
///     Hashes are indexed for fast lookup during ROM verification.
/// </summary>
public sealed class DatRom
{
    private DatRom(
        int id,
        int datGameId,
        string name,
        long size,
        Crc32? crc,
        Md5? md5,
        Sha1? sha1,
        string? status,
        string? serial,
        int? romFileId)
    {
        Id = id;
        DatGameId = datGameId;
        Name = name;
        Size = size;
        Crc = crc;
        Md5 = md5;
        Sha1 = sha1;
        Status = status;
        Serial = serial;
        RomFileId = romFileId;
    }

    public int Id { get; private set; }
    public int DatGameId { get; private set; }

    public string Name { get; private set; }
    public long Size { get; private set; }

    // Hashes (indexed for lookup)
    public Crc32? Crc { get; private set; }
    public Md5? Md5 { get; private set; }
    public Sha1? Sha1 { get; private set; }

    // Status from DAT (good, baddump, nodump)
    public string? Status { get; private set; }

    // Optional serial number
    public string? Serial { get; private set; }

    /// <summary>
    ///     Reference to the physical ROM file that satisfies this DAT entry.
    ///     Null if no matching file has been ingested.
    /// </summary>
    public int? RomFileId { get; private set; }

    /// <summary>
    ///     Navigation property to the linked RomFile.
    /// </summary>
    public RomFile? RomFile { get; private set; }

    /// <summary>
    ///     Creates a new ROM entry with validated invariants.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when name is null or whitespace.</exception>
    public static DatRom CreateNew(
        string name,
        long size,
        Crc32? crc = null,
        Md5? md5 = null,
        Sha1? sha1 = null,
        string? status = null,
        string? serial = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new DatRom(
            0,
            0,
            name,
            size,
            crc,
            md5,
            sha1,
            status,
            serial,
            null);
    }

    /// <summary>
    ///     Rehydrates a ROM entry from persistence. Trusts that data is valid.
    /// </summary>
    internal static DatRom Rehydrate(
        int id,
        int datGameId,
        string name,
        long size,
        Crc32? crc,
        Md5? md5,
        Sha1? sha1,
        string? status,
        string? serial,
        int? romFileId) =>
        new(id, datGameId, name, size, crc, md5, sha1, status, serial, romFileId);
}
