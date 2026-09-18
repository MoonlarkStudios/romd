using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Management.Models;

/// <summary>
///     A physical ROM file in the user's library.
/// </summary>
public sealed record Rom
{
    public required string Id { get; init; }
    public required string OriginalFilename { get; init; }
    public required ByteCount Size { get; init; }
    public required string Sha1 { get; init; }
    public string? Md5 { get; init; }
    public string? Crc32 { get; init; }
    public DateTimeOffset ImportedAt { get; init; }

    /// <summary>
    ///     The catalog status of this ROM: "unidentified", "unrouted", or "cataloged".
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    ///     List of DAT files that match this ROM. Only populated for single-item GET requests.
    /// </summary>
    public IReadOnlyList<RomMatch>? Matches { get; init; }
}
