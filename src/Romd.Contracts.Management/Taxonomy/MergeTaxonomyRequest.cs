namespace Romd.Contracts.Management.Taxonomy;

/// <summary>The row of the same collection that absorbs the merged one.</summary>
public sealed class MergeTaxonomyRequest
{
    public required string TargetKey { get; init; }
}
