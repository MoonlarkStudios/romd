namespace Romd.Contracts.Management.Models;

/// <summary>
///     A canonical game title with enriched metadata.
/// </summary>
public sealed record Title
{
    public required string Id { get; init; }
    public required string SystemKey { get; init; }
    public required string Name { get; init; }

    /// <summary>
    ///     Whether any effective source assertion for this title has locally stored payload.
    /// </summary>
    public bool HasLocalPayload { get; init; }

    /// <summary>
    ///     Current enrichment status (None, Pending, Completed, Failed, NotFound).
    /// </summary>
    public required string EnrichmentStatus { get; init; }

    // Enriched metadata (populated after provider enrichment)
    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public string? Developer { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? Players { get; init; }
    public double? Rating { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastEnrichedAt { get; init; }
}
