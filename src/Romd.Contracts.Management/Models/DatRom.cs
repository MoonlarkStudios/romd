using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Management.Models;

/// <summary>
///     A ROM entry within a DAT game (metadata from catalog).
/// </summary>
public sealed record DatRom
{
    public required string Id { get; init; }
    public required string GameId { get; init; }
    public required string Name { get; init; }
    public ByteCount Size { get; init; }
    public string? Crc { get; init; }
    public string? Md5 { get; init; }
    public string? Sha1 { get; init; }
    public string? Status { get; init; }
}
