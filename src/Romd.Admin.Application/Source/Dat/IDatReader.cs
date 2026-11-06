using ErrorOr;
using Romd.Domain.Source.Dat;

namespace Romd.Admin.Application.Source.Dat;

/// <summary>
///     Reads DAT files in two phases: header metadata first, then streaming games.
///     Each operation is stateless and independent.
/// </summary>
public interface IDatReader
{
    /// <summary>
    ///     Parses DAT file header metadata. Does not create domain entities.
    /// </summary>
    Task<ErrorOr<DatMetadata>> ReadHeaderAsync(
        Stream stream,
        CancellationToken ct = default);

    /// <summary>
    ///     Streams game entities from a DAT file.
    /// </summary>
    IAsyncEnumerable<ErrorOr<DatGame>> StreamGamesAsync(
        Stream stream,
        int datFileId,
        CancellationToken ct = default);
}

/// <summary>
///     Metadata extracted from a DAT file header.
///     This is a parsing result, not a domain entity.
/// </summary>
public sealed record DatMetadata
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required DatType DatType { get; init; }
    public string? Version { get; init; }
    public string? Author { get; init; }
    public string? Url { get; init; }
}
