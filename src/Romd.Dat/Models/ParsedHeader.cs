namespace Romd.Dat.Models;

/// <summary>
///     Metadata for a DAT document, including the provenance inferred when reading it.
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
