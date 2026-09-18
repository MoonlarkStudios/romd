namespace Romd.Contracts.Management.Taxonomy;

public sealed class RegionDto
{
    public required bool CanMerge { get; init; }

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int SortOrder { get; init; }
    public required bool IsAutoCreated { get; init; }
    public required IReadOnlyList<AliasDto> Aliases { get; init; }
}
