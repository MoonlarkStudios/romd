namespace Romd.Dat.Models;

/// <summary>
///     A ROM file declaration with size, checksums, status and serial information.
/// </summary>
public sealed record ParsedRom
{
    public required string Name { get; init; }

    /// <summary>The exact byte count, when declared and valid.</summary>
    public long? DeclaredSize { get; init; }

    /// <summary>The byte count used by ingest, with zero as the fallback for unknown sizes.</summary>
    public long Size => DeclaredSize.GetValueOrDefault();

    internal bool HasMalformedSize { get; init; }
    internal bool HasMalformedChecksum { get; init; }

    public string? Crc { get; init; }
    public string? Md5 { get; init; }
    public string? Sha1 { get; init; }
    public string? Status { get; init; }
    public string? Serial { get; init; }
}
