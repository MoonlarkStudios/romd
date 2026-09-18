using ErrorOr;

namespace Romd.Admin.Application.Search;

/// <summary>
///     Error definitions for search operations.
/// </summary>
public static class SearchErrors
{
    /// <summary>
    ///     Relevance sort requires a search query.
    /// </summary>
    public static Error RelevanceSortRequiresQuery => Error.Validation(
        code: "Search.RelevanceSortRequiresQuery",
        description: "Cannot sort by relevance without a search query.");

    /// <summary>
    ///     Invalid platform ID format.
    /// </summary>
    public static Error InvalidPlatformId => Error.Validation(
        code: "Search.InvalidPlatformId",
        description: "The provided platform ID is not valid.");

    /// <summary>
    ///     Invalid cursor format.
    /// </summary>
    public static Error InvalidCursor => Error.Validation(
        code: "Search.InvalidCursor",
        description: "The provided cursor is not valid or has expired.");
}
