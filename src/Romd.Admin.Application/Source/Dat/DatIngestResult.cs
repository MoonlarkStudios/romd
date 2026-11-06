using Romd.Domain.Source.Dat;
using Romd.Domain.Source.Dat.Parsing;

namespace Romd.Admin.Application.Source.Dat;

/// <summary>
///     Result of a DAT file ingestion operation.
///     Contains the imported DAT file along with statistics and any warnings encountered.
/// </summary>
public sealed record DatIngestResult
{
    public required DatFile DatFile { get; init; }
    public required IReadOnlyList<DatParseWarning> Warnings { get; init; }

    /// <summary>
    ///     Whether any warnings were encountered during parsing.
    /// </summary>
    public bool HasWarnings => Warnings.Count > 0;
}
