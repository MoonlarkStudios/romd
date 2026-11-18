namespace Romd.Contracts.Management.Models;

public sealed record ServerInfo
{
    /// <summary>The installed deployment's release tag, or the admin host's build tag for a standalone build.</summary>
    public required string Version { get; init; }
}
