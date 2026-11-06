namespace Romd.Contracts.Management.Taxonomy;

public sealed class AliasDto
{
    public required string Ownership { get; init; }

    public required string Id { get; init; }
    public required string Alias { get; init; }
}
