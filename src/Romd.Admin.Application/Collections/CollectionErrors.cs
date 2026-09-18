using ErrorOr;

namespace Romd.Admin.Application.Collections;

public static class CollectionErrors
{
    public static Error NotFound(int id) =>
        Error.NotFound("Collections.NotFound", $"Collection with ID {id} not found");

    public static Error DuplicateTitle(int titleId) =>
        Error.Conflict("Collections.DuplicateTitle", $"Title {titleId} is already in this collection");

    public static Error TitleNotInCollection(int titleId) =>
        Error.NotFound("Collections.TitleNotInCollection", $"Title {titleId} is not in this collection");

    public static Error ReorderMismatch() =>
        Error.Validation("Collections.ReorderMismatch",
            "Provided title IDs do not match the items currently in the collection");

    public static Error EmptyName() =>
        Error.Validation("Collections.EmptyName", "Collection name cannot be empty");
}
