namespace Romd.Domain.Source.Dat.Parsing;

/// <summary>
///     Represents a non-fatal warning encountered during DAT parsing.
/// </summary>
public sealed record DatParseWarning(string Code, string Message, int? LineNumber = null);
