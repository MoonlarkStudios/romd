namespace Romd.Contracts.Management.Models;

public sealed record ServerInfo
{
    /// <summary>The running admin host's build-time release tag, or development for an untagged build.</summary>
    public required string Version { get; init; }
}
