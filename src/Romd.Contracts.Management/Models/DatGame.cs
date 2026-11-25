namespace Romd.Contracts.Management.Models;

/// <summary>
///     A game entry within a DAT file.
/// </summary>
public sealed record DatGame
{
    public required string Id { get; init; }
    public required string DatId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Year { get; init; }
    public string? Manufacturer { get; init; }
    /// <summary>The entry identifier declared by the DAT.</summary>
    public string? DeclaredId { get; init; }
    public string? CloneOf { get; init; }
    /// <summary>The parent identifier declared by the DAT, before resolution.</summary>
    public string? CloneOfId { get; init; }
    public string? RomOf { get; init; }
    public bool IsBios { get; init; }
    public IReadOnlyList<DatRom> Roms { get; init; } = [];
    public IReadOnlyList<DatDisk> Disks { get; init; } = [];
}
