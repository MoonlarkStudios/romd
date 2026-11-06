namespace Romd.Admin.Application.Taxonomy;

/// <summary>
///     How strongly a region or language row is locked until the transaction ends. Each caller takes the weakest mode
///     that still serializes it against a merge: a running DAT ingest holds <see cref="KeyShare" /> on every row its
///     uncommitted links reference, and of these modes only <see cref="Update" /> waits for it.
/// </summary>
public enum TaxonomyRowLock
{
    /// <summary><c>FOR KEY SHARE</c>: the row cannot be merged away until commit. An alias add takes it.</summary>
    KeyShare,

    /// <summary>
    ///     <c>FOR NO KEY UPDATE</c>: as <see cref="KeyShare" />, and holders of this mode on one row also take turns. A
    ///     merge's target takes it so two merges into it rewrite its links one after the other, and an alias remove
    ///     takes it so a second remove of the same alias finds it gone.
    /// </summary>
    NoKeyUpdate,

    /// <summary><c>FOR UPDATE</c>: the caller deletes the row, so it waits for every transaction that references it.</summary>
    Update
}
