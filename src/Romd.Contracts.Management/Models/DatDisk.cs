namespace Romd.Contracts.Management.Models;

/// <summary>
///     A disk (CHD) entry within a DAT game.
/// </summary>
public sealed record DatDisk
{
    public required string Id { get; init; }
    public required string GameId { get; init; }
    public required string Name { get; init; }
    public string? Sha1 { get; init; }
    public string? Md5 { get; init; }
    public string? Status { get; init; }
}
