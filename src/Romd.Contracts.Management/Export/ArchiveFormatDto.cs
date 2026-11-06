namespace Romd.Contracts.Management.Export;

/// <summary>
///     A format an archive download or library export can be requested in. <see cref="Format" /> is the wire value a
///     download ticket or export request sends; <see cref="Label" /> comes from the server's format catalog, so clients
///     display it instead of keeping format tables.
/// </summary>
public sealed record ArchiveFormatDto
{
    public required string Format { get; init; }

    /// <summary>The format's display label, for example <c>Original</c>.</summary>
    public required string Label { get; init; }
}
