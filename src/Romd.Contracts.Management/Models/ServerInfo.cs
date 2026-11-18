namespace Romd.Contracts.Management.Models;

public sealed record ServerInfo
{
    /// <summary>The release tag of the installed deployment bundle, or null when the host does not run from one.</summary>
    public required string? Release { get; init; }

    /// <summary>The build of the running admin host.</summary>
    public required ServerBuild Build { get; init; }
}

public sealed record ServerBuild
{
    /// <summary>The release tag the image was built for, or development for an untagged build.</summary>
    public required string Tag { get; init; }

    /// <summary>The source commit the image was built from, or development for an untagged build.</summary>
    public required string Revision { get; init; }
}
