namespace Romd.Contracts.Management.Commands;

/// <summary>
///     Command to import files from a server-local directory under the configured
///     Romd:AllowedImportPaths allowlist.
/// </summary>
public sealed record ImportFromPath
{
    public required string Path { get; init; }
    public string? SystemKey { get; init; }
    public int? MaxParallelRoms { get; init; }
    public bool? AllowUnidentified { get; init; }
    public bool? ArchiveOnly { get; init; }
    public bool? Move { get; init; }
}
