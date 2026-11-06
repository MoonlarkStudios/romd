namespace Romd.Domain.Collections;

/// <summary>
///     Represents a single title within a collection, with ordering and optional notes.
/// </summary>
public sealed class CollectionItem
{
    public const int MaxNoteLength = 500;

    private CollectionItem(
        int id,
        int collectionId,
        int titleId,
        int sortOrder,
        string? note,
        DateTimeOffset addedAt)
    {
        Id = id;
        CollectionId = collectionId;
        TitleId = titleId;
        SortOrder = sortOrder;
        Note = note;
        AddedAt = addedAt;
    }

    public int Id { get; private set; }
    public int CollectionId { get; private set; }
    public int TitleId { get; private set; }
    public int SortOrder { get; private set; }

    /// <summary>
    ///     Optional user note for this item in the collection (max 500 characters).
    /// </summary>
    public string? Note { get; private set; }

    public DateTimeOffset AddedAt { get; private set; }

    /// <summary>
    ///     Creates a new collection item with validated invariants.
    /// </summary>
    public static CollectionItem CreateNew(int collectionId, int titleId, int sortOrder, DateTimeOffset addedAt, string? note = null)
    {
        if (note is { Length: > MaxNoteLength })
            throw new ArgumentException($"Note cannot exceed {MaxNoteLength} characters.", nameof(note));

        return new CollectionItem(
            id: 0,
            collectionId: collectionId,
            titleId: titleId,
            sortOrder: sortOrder,
            note: note,
            addedAt: addedAt);
    }

    /// <summary>
    ///     Rehydrates a collection item from persistence. Trusts that data is valid.
    /// </summary>
    internal static CollectionItem Rehydrate(
        int id,
        int collectionId,
        int titleId,
        int sortOrder,
        string? note,
        DateTimeOffset addedAt)
    {
        return new CollectionItem(id, collectionId, titleId, sortOrder, note, addedAt);
    }

    internal void UpdateSortOrder(int sortOrder) => SortOrder = sortOrder;

    public void UpdateNote(string? note)
    {
        if (note is { Length: > MaxNoteLength })
            throw new ArgumentException($"Note cannot exceed {MaxNoteLength} characters.", nameof(note));

        Note = note;
    }
}
