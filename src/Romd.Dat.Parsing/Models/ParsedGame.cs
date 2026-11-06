namespace Romd.Dat.Parsing.Models;

/// <summary>
///     Represents a game entry parsed from a DAT file.
///     A game can contain multiple ROM files (e.g., multi-disc games or games with multiple files).
///     This is a transient object that exists only during parsing before becoming a persisted entity.
/// </summary>
public sealed record ParsedGame
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Year { get; init; }
    public string? Manufacturer { get; init; }
    public string? CloneOf { get; init; }
    public string? RomOf { get; init; }
    public required ParsedNameMetadata NameMetadata { get; init; }
    public bool IsBios { get; init; }
    public required IReadOnlyList<ParsedRom> Roms { get; init; }
    public IReadOnlyList<ParsedDisk> Disks { get; init; } = [];
}
