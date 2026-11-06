namespace Romd.Persistence.Enrichment;

/// <summary>A value and one-based position from PostgreSQL 18's information_schema._pg_expandarray.</summary>
public sealed class EnrichmentBatchMember
{
    public int Value { get; init; }
    public int Ordinal { get; init; }
}
