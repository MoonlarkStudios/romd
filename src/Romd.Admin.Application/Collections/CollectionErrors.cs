using ErrorOr;
using Romd.Application.Common.Validation;
using Romd.Domain.Collections;

namespace Romd.Admin.Application.Collections;

public static class CollectionErrors
{
    public static Error NotFound() =>
        Error.NotFound("Collections.NotFound", "Collection not found.");

    public static Error DuplicateTitle() =>
        Error.Conflict("Collections.DuplicateTitle", "The title is already in this collection.");

    public static Error TitleNotInCollection() =>
        Error.NotFound("Collections.TitleNotInCollection", "The title is not in this collection.");

    public static Error ReorderMismatch() =>
        Error.Validation("Collections.ReorderMismatch",
            "Provided title IDs do not match the items currently in the collection");

    public static Error EmptyName() =>
        FieldErrors.Validation("Collections.EmptyName", "Collection name cannot be empty", "name");

    public static Error NameTooLong() =>
        FieldErrors.Validation("Collections.NameTooLong", $"Collection name cannot exceed {Collection.MaxNameLength} characters.", "name");

    public static Error DescriptionTooLong() =>
        FieldErrors.Validation("Collections.DescriptionTooLong", $"Collection description cannot exceed {Collection.MaxDescriptionLength} characters.", "description");

    public static Error NoteTooLong() =>
        FieldErrors.Validation("Collections.NoteTooLong", $"Collection item note cannot exceed {CollectionItem.MaxNoteLength} characters.", "note");

    public static Error CoverMediaNotFound() =>
        FieldErrors.Validation("Collections.CoverMediaNotFound", "Cover media not found.", "coverMediaId");
}
