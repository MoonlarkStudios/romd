namespace Romd.Dat.Models;

/// <summary>
///     A disk declaration with checksum and dump status information.
/// </summary>
public sealed record ParsedDisk
{
    public required string Name { get; init; }
    public string? Sha1 { get; init; }
    public string? Md5 { get; init; }
    public string? Status { get; init; }
    internal bool HasMalformedChecksum { get; init; }
}
