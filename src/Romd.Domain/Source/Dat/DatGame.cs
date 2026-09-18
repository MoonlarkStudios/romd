namespace Romd.Domain.Source.Dat;

/// <summary>
///     Represents a game entry within a managed DAT file.
///     A game is a collection of ROMs that belong together (e.g., a game dump).
/// </summary>
public sealed class DatGame
{
    private readonly List<DatDisk> _disks = [];
    private readonly List<DatRom> _roms = [];

    private DatGame(
        int id,
        int datFileId,
        string name,
        string? description,
        string? year,
        string? manufacturer,
        string? region,
        string? language,
        string? revision,
        string? developmentStatus,
        string? category,
        string? cloneOf,
        string? romOf,
        bool isBios,
        IEnumerable<DatRom> roms,
        IEnumerable<DatDisk> disks)
    {
        Id = id;
        DatFileId = datFileId;
        Name = name;
        Description = description;
        Year = year;
        Manufacturer = manufacturer;
        Region = region;
        Language = language;
        Revision = revision;
        DevelopmentStatus = developmentStatus;
        Category = category;
        CloneOf = cloneOf;
        RomOf = romOf;
        IsBios = isBios;
        _roms.AddRange(roms);
        _disks.AddRange(disks);
    }

    public int Id { get; private set; }
    public int DatFileId { get; private set; }

    public string Name { get; private set; }
    public string? Description { get; private set; }
    public string? Year { get; private set; }
    public string? Manufacturer { get; private set; }

    public string? Region { get; private set; } // "USA", "Europe", "Japan"
    public string? Language { get; private set; } // "En,Fr,De"
    public string? Revision { get; private set; } // "Rev 1", "v1.2", "Beta"
    public string? DevelopmentStatus { get; private set; } // "Stable", "Prototype", "Unlicensed"

    public string? Category { get; private set; } // "Games", "BIOS"

    public bool IsBios { get; private set; }

    // Parent/Clone relationships
    public string? CloneOf { get; private set; }
    public string? RomOf { get; private set; }

    // Child collections (small per-game, safe to load)
    public IReadOnlyCollection<DatRom> Roms => _roms;
    public IReadOnlyCollection<DatDisk> Disks => _disks;

    /// <summary>
    ///     Creates a new game entry with validated invariants.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when name is null or whitespace.</exception>
    public static DatGame CreateNew(
        int datFileId,
        string name,
        string? description = null,
        string? year = null,
        string? manufacturer = null,
        string? cloneOf = null,
        string? romOf = null,
        string? category = null,
        bool isBios = false,
        string? region = null,
        string? language = null,
        string? revision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new DatGame(
            0,
            datFileId,
            name,
            description,
            year,
            manufacturer,
            region,
            language,
            revision,
            null,
            category,
            cloneOf,
            romOf,
            isBios,
            [],
            []);
    }

    /// <summary>
    ///     Rehydrates a game entry from persistence. Trusts that data is valid.
    /// </summary>
    internal static DatGame Rehydrate(
        int id,
        int datFileId,
        string name,
        string? description,
        string? year,
        string? manufacturer,
        string? region,
        string? language,
        string? revision,
        string? developmentStatus,
        string? category,
        string? cloneOf,
        string? romOf,
        bool isBios,
        IEnumerable<DatRom> roms,
        IEnumerable<DatDisk> disks)
    {
        return new DatGame(
            id, datFileId, name, description, year, manufacturer,
            region, language, revision, developmentStatus, category,
            cloneOf, romOf, isBios, roms, disks);
    }

    public void AddRom(DatRom rom) => _roms.Add(rom);
    public void AddDisk(DatDisk disk) => _disks.Add(disk);
}
