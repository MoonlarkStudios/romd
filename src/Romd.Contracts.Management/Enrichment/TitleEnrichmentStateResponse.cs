namespace Romd.Contracts.Management.Enrichment;

public sealed record TitleEnrichmentStateResponse
{
    public required string TitleId { get; init; }

    /// <summary>The title's validator, also sent as the ETag.</summary>
    public required string Revision { get; init; }

    public required string Name { get; init; }
    public required string EnrichmentStatus { get; init; }
    public Dictionary<string, string>? FieldProvenance { get; init; }
    public Dictionary<string, string>? FieldSourceOverrides { get; init; }
    public required List<MetadataLayer> Layers { get; init; }
    public required List<ProviderExternalId> ExternalIds { get; init; }
    public DateTimeOffset? LastEnrichedAt { get; init; }
}

public sealed record MetadataLayer
{
    public required string SourceId { get; init; }
    public required string SourceType { get; init; }
    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public string? Developer { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? Players { get; init; }
    public double? Rating { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ProviderExternalId
{
    public required string Provider { get; init; }
    public required string ExternalId { get; init; }
    public float MatchConfidence { get; init; }
    public bool IsConfirmed { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
