namespace Romd.Admin.Application.Taxonomy.ReadModels;

public sealed record LanguageWithAliases(
    int Id,
    string Name,
    string Code,
    int SortOrder,
    bool IsAutoCreated,
    bool CanMerge,
    IReadOnlyList<(int Id, string Alias, string Ownership)> Aliases);
