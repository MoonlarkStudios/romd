namespace Romd.Dat.Parsing.Models;

/// <summary>
///     Represents a disk (CHD) entry parsed from a DAT file.
///     Used primarily for CD/DVD-based systems where CHD format is common.
///     This is a transient object that exists only during parsing before becoming a persisted entity.
/// </summary>
public sealed record ParsedDisk
{
    public required string Name { get; init; }
    public string? Sha1 { get; init; }
    public string? Md5 { get; init; }
    public string? Status { get; init; }
}
