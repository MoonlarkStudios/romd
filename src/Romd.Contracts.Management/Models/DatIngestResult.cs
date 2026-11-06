namespace Romd.Contracts.Management.Models;

/// <summary>
///     Result of a DAT ingestion operation.
/// </summary>
public sealed record DatIngestResult
{
    public required Dat Dat { get; init; }
    public required IReadOnlyList<DatParseWarning> Warnings { get; init; }
}

/// <summary>
///     A warning encountered during DAT parsing.
/// </summary>
public sealed record DatParseWarning
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public int? LineNumber { get; init; }
}
