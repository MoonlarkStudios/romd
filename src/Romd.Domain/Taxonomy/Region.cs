namespace Romd.Domain.Taxonomy;

/// <summary>
///     Represents a canonical geographic region for game releases.
///     Regions are resolved from raw DAT file tokens via an alias lookup system.
/// </summary>
public sealed class Region : ITaxonomyEntity
{
    private Region(
        int id,
        string name,
        int sortOrder,
        bool isAutoCreated)
    {
        Id = id;
        Name = name;
        SortOrder = sortOrder;
        IsAutoCreated = isAutoCreated;
    }

    public int Id { get; private set; }

    /// <summary>
    ///     Canonical display name of the region.
    ///     Example: "USA", "Japan", "Europe"
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    ///     Determines default 1G1R priority (lower = higher priority).
    /// </summary>
    public int SortOrder { get; private set; }

    /// <summary>
    ///     True if this region was automatically created from an unrecognized DAT token.
    /// </summary>
    public bool IsAutoCreated { get; private set; }

    /// <summary>
    ///     Creates a new region with validated invariants.
    /// </summary>
    public static Region CreateNew(
        string name,
        int sortOrder = 0,
        bool isAutoCreated = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Region(0, name, sortOrder, isAutoCreated);
    }

    /// <summary>
    ///     Rehydrates a region from persistence. Trusts that data is valid.
    /// </summary>
    internal static Region Rehydrate(
        int id,
        string name,
        int sortOrder,
        bool isAutoCreated) =>
        new(id, name, sortOrder, isAutoCreated);
}
