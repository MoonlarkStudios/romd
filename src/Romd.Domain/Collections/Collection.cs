using System.Collections.ObjectModel;

namespace Romd.Domain.Collections;

/// <summary>
///     A user-curated, ordered grouping of Titles for presentation purposes.
///     Aggregate root managing CollectionItem children.
/// </summary>
public sealed class Collection
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 2000;

    private readonly List<CollectionItem> _items = [];
    private readonly ReadOnlyCollection<CollectionItem> _itemsView;

    private Collection(
        int id,
        string name,
        string? description,
        int? coverMediaId,
        int? platformId,
        bool isSystem,
        int sortOrder,
        DateTimeOffset createdAt,
        IEnumerable<CollectionItem>? items)
    {
        _itemsView = _items.AsReadOnly();
        Id = id;
        Name = name;
        Description = description;
        CoverMediaId = coverMediaId;
        PlatformId = platformId;
        IsSystem = isSystem;
        SortOrder = sortOrder;
        CreatedAt = createdAt;
        if (items != null)
            _items.AddRange(items);
    }

    public int Id { get; private set; }

    /// <summary>
    ///     Display name for the collection (required, max 200 characters).
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    ///     Optional description (max 2000 characters).
    /// </summary>
    public string? Description { get; private set; }

    /// <summary>
    ///     Optional cover image (references TitleMedia).
    /// </summary>
    public int? CoverMediaId { get; private set; }

    /// <summary>
    ///     Optional platform scope. Null means cross-platform.
    /// </summary>
    public int? PlatformId { get; private set; }

    /// <summary>
    ///     System collections are auto-generated and cannot be deleted by users.
    /// </summary>
    public bool IsSystem { get; private set; }

    /// <summary>
    ///     Display ordering among sibling collections.
    /// </summary>
    public int SortOrder { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyCollection<CollectionItem> Items => _itemsView;

    public int ItemCount => _items.Count;

    /// <summary>
    ///     Creates a new collection with validated invariants.
    /// </summary>
    public static Collection CreateNew(
        string name,
        DateTimeOffset createdAt,
        string? description = null,
        int? coverMediaId = null,
        int? platformId = null)
    {
        ValidateDetails(name, description);

        return new Collection(
            id: 0,
            name: name.Trim(),
            description: description?.Trim(),
            coverMediaId: coverMediaId,
            platformId: platformId,
            isSystem: false,
            sortOrder: 0,
            createdAt: createdAt,
            items: null);
    }

    /// <summary>
    ///     Rehydrates a collection from persistence. Trusts that data is valid.
    /// </summary>
    internal static Collection Rehydrate(
        int id,
        string name,
        string? description,
        int? coverMediaId,
        int? platformId,
        bool isSystem,
        int sortOrder,
        DateTimeOffset createdAt,
        IEnumerable<CollectionItem>? items = null)
    {
        return new Collection(id, name, description, coverMediaId, platformId, isSystem, sortOrder, createdAt, items);
    }

    /// <summary>
    ///     Updates the collection's display details.
    /// </summary>
    public void UpdateDetails(string name, string? description, int? coverMediaId, int? platformId)
    {
        ValidateDetails(name, description);
        Name = name.Trim();
        Description = description?.Trim();
        CoverMediaId = coverMediaId;
        PlatformId = platformId;
    }

    private static void ValidateDetails(string name, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Trim().Length > MaxNameLength)
            throw new ArgumentException($"Name cannot exceed {MaxNameLength} characters.", nameof(name));
        if (description?.Trim().Length > MaxDescriptionLength)
            throw new ArgumentException($"Description cannot exceed {MaxDescriptionLength} characters.", nameof(description));
    }

    /// <summary>
    ///     Adds a title to the collection. Rejects duplicates.
    /// </summary>
    /// <returns>The added item, or null if the title already exists in the collection.</returns>
    public CollectionItem? AddItem(int titleId, DateTimeOffset addedAt, string? note = null)
    {
        if (_items.Any(i => i.TitleId == titleId))
            return null;

        int nextSortOrder = _items.Count > 0 ? _items.Max(i => i.SortOrder) + 1 : 0;
        var item = CollectionItem.CreateNew(Id, titleId, nextSortOrder, addedAt, note);
        _items.Add(item);
        return item;
    }

    /// <summary>
    ///     Removes a title from the collection and compacts sort orders.
    /// </summary>
    /// <returns>True if removed, false if title was not in collection.</returns>
    public bool RemoveItem(int titleId)
    {
        var item = _items.FirstOrDefault(i => i.TitleId == titleId);
        if (item is null)
            return false;

        _items.Remove(item);

        // Compact sort orders
        var sorted = _items.OrderBy(i => i.SortOrder).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            sorted[i].UpdateSortOrder(i);
        }

        return true;
    }

    /// <summary>
    ///     Reorders items to match the provided title ID sequence.
    ///     The list must contain exactly the same title IDs as currently in the collection.
    /// </summary>
    /// <returns>True if reordered, false if title IDs don't match.</returns>
    public bool ReorderItems(IReadOnlyList<int> titleIdsInOrder)
    {
        if (titleIdsInOrder.Count != _items.Count)
            return false;

        var existingIds = _items.Select(i => i.TitleId).ToHashSet();
        var newIds = titleIdsInOrder.ToHashSet();

        if (!existingIds.SetEquals(newIds))
            return false;

        for (int i = 0; i < titleIdsInOrder.Count; i++)
        {
            var item = _items.First(it => it.TitleId == titleIdsInOrder[i]);
            item.UpdateSortOrder(i);
        }

        return true;
    }
}
