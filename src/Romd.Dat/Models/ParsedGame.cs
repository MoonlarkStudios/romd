namespace Romd.Dat.Models;

/// <summary>
///     A DAT entry with source declarations, file records and derived metadata.
/// </summary>
public sealed record ParsedGame
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Year { get; init; }
    public string? Manufacturer { get; init; }
    public string? DeclaredId { get; init; }
    public string? CloneOf { get; init; }
    public string? CloneOfId { get; init; }
    public string? RomOf { get; init; }
    public string? DeclaredCategory { get; init; }

    /// <summary>The literal isbios attribute, independently of inferred BIOS status.</summary>
    public string? BiosDeclaration { get; init; }

    public required ParsedNameMetadata NameMetadata { get; init; }
    public bool IsBios { get; init; }
    public required IReadOnlyList<ParsedRom> Roms { get; init; }
    public IReadOnlyList<ParsedDisk> Disks { get; init; } = [];
}
