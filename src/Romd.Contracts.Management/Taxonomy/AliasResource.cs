namespace Romd.Contracts.Management.Taxonomy;

public sealed class AliasResource
{
    public required string Ownership { get; init; }

    public required string Id { get; init; }
    public required string Alias { get; init; }
}
