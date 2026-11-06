namespace Romd.Domain.Taxonomy;

/// <summary>
///     Common contract for taxonomy domain entities (Region, GameLanguage, etc.).
///     Enables generic repository and resolver infrastructure.
/// </summary>
public interface ITaxonomyEntity
{
    int Id { get; }
    string Name { get; }
    int SortOrder { get; }
    bool IsAutoCreated { get; }
}
