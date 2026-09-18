namespace Romd.Dat.Parsing.Models;

/// <summary>
///     Represents the header section parsed from a DAT file containing metadata about the DAT itself.
///     This is a transient object that exists only during parsing before becoming a persisted entity.
/// </summary>
public sealed record ParsedHeader
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string? Version { get; init; }
    public string? Author { get; init; }
    public string? Homepage { get; init; }
    public string? Url { get; init; }
    public string? Date { get; init; }
    public string? Email { get; init; }
    public string? Comment { get; init; }
    public DatProvenance Provenance { get; init; }
}
