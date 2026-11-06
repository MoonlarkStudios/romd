using ErrorOr;
using Romd.Application.Common.Validation;

namespace Romd.Admin.Application.Taxonomy;

/// <summary>
///     Region and language errors. Messages never echo internal ids or the supplied keys; validation errors
///     name the request input they belong to.
/// </summary>
public static class TaxonomyErrors
{
    /// <summary>The longest alias the alias column holds.</summary>
    public const int MaxAliasLength = 100;

    /// <summary>An unresolved region or language key, the same code every reference resource answers.</summary>
    public static Error NotFound() => ReferenceData.ReferenceEditErrors.Missing();

    public static readonly Error MergeIntoSelf =
        FieldErrors.Validation("Taxonomy.MergeIntoSelf", "Cannot merge a region or language into itself.", "targetKey");

    public static Error UnknownTarget() =>
        FieldErrors.Validation("Taxonomy.UnknownTarget", "The merge target is not a known key in this collection.", "targetKey");

    public static Error ReferenceIdentity() =>
        Error.Conflict("Taxonomy.ReferenceIdentity", "A registered reference identity cannot be merged into another identity.");

    /// <summary>
    ///     The merge lost a deadlock twice, or a lock wait (behind an ingest, a catalog rebuild or another merge) outlasted
    ///     its lock timeout; nothing changed, and it can be sent again.
    /// </summary>
    public static Error MergeConflict() =>
        Error.Conflict("Taxonomy.MergeConflict", "The merge conflicted with concurrent catalog work and changed nothing. Retry it.");

    /// <summary>
    ///     An alias write's lock wait (behind an ingest's uncommitted equal alias, a merge or another alias write)
    ///     outlasted its lock timeout, or the write lost a deadlock; nothing changed, and it can be sent again.
    /// </summary>
    public static Error AliasConflict() =>
        Error.Conflict("Taxonomy.AliasConflict", "The alias change conflicted with concurrent catalog work and changed nothing. Retry it.");

    public static Error InvalidAlias() =>
        FieldErrors.Validation("Taxonomy.InvalidAlias",
            $"An alias must be 1 to {MaxAliasLength} characters after trimming, without control characters.", "alias");

    public static Error AliasAlreadyExists() =>
        Error.Conflict("Taxonomy.AliasAlreadyExists", "That alias is already in use.");

    public static Error AliasNotFound() =>
        Error.NotFound("Taxonomy.AliasNotFound", "The alias does not belong to this region or language.");

    public static Error InvalidCursor() =>
        FieldErrors.Validation("Taxonomy.InvalidCursor", "Use a valid cursor from the same collection.", "cursor");

    public static Error InvalidLimit(int maximum) =>
        FieldErrors.Validation("Taxonomy.InvalidLimit", $"Limit must be between 1 and {maximum}.", "limit");
}
