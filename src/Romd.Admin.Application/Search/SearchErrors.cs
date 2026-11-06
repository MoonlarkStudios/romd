using ErrorOr;
using Romd.Application.Common.Validation;

namespace Romd.Admin.Application.Search;

/// <summary>
///     Error definitions for search operations.
/// </summary>
public static class SearchErrors
{
    /// <summary>
    ///     Relevance sort requires a search query.
    /// </summary>
    public static Error RelevanceSortRequiresQuery => FieldErrors.Validation(
        code: "Search.RelevanceSortRequiresQuery",
        description: "Cannot sort by relevance without a search query.", field: "sortBy");

    /// <summary>
    ///     Invalid platform ID format.
    /// </summary>
    public static Error InvalidPlatformId => Error.Validation(
        code: "Search.InvalidPlatformId",
        description: "The provided platform ID is not valid.");

    /// <summary>
    ///     Invalid cursor format.
    /// </summary>
    public static Error InvalidCursor => FieldErrors.Validation(
        code: "Search.InvalidCursor",
        description: "The cursor is invalid for this query. Start from the first page without a cursor.", field: "cursor");

    /// <summary>
    ///     A supplied enrichment filter list contains no values.
    /// </summary>
    public static Error EnrichmentFilterRequired => Error.Validation(
        code: "Search.EnrichmentFilterRequired",
        description: "Choose at least one enrichment filter.");

    /// <summary>
    ///     An enrichment filter value is outside the accepted vocabulary.
    /// </summary>
    public static Error InvalidEnrichmentFilter(string? value) => Error.Validation(
        code: "Search.InvalidEnrichmentFilter",
        description: $"'{value}' is not a valid enrichment filter. Accepted values: NeedsReview, Blocked, Processing, Automatic, Settled.");

    /// <summary>
    ///     Settled means no flags, so it cannot be combined with any flag.
    /// </summary>
    public static Error EnrichmentFilterConflict => Error.Validation(
        code: "Search.EnrichmentFilterConflict",
        description: "Settled cannot be combined with other enrichment filters.");
}
