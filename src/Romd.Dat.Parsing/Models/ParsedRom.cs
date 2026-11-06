namespace Romd.Dat.Parsing.Models;

/// <summary>
///     Represents a ROM entry parsed from a DAT file.
///     Contains validated hash information used to verify ROM files.
///     This is a transient object that exists only during parsing before becoming a persisted entity.
/// </summary>
public sealed record ParsedRom
{
    public required string Name { get; init; }
    public required long Size { get; init; }
    public string? Crc { get; init; }
    public string? Md5 { get; init; }
    public string? Sha1 { get; init; }
    public string? Status { get; init; }
    public string? Serial { get; init; }
}
