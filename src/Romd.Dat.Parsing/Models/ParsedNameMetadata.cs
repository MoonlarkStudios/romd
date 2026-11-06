namespace Romd.Dat.Parsing.Models;

public sealed record ParsedNameMetadata
{
    public string? Region { get; init; }
    public string? Language { get; init; }
    public string? Revision { get; init; }
    public required string Category { get; init; }
    public bool IsVerified { get; init; }
}
