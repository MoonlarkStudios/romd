namespace Romd.Admin.Application.Taxonomy.ReadModels;

public sealed record RegionWithAliases(
    int Id,
    string Name,
    int SortOrder,
    bool IsAutoCreated,
    bool CanMerge,
    IReadOnlyList<(int Id, string Alias, string Ownership)> Aliases);
