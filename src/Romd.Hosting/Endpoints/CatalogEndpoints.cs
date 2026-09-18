using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Systems;
using Romd.Admin.Application.Catalog;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Catalog.Queries.GetCatalogFilters;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Search;
using Romd.Admin.Application.Search.ReadModels;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Identity;
using Romd.Host.Authorization;
using CommonModels = Romd.Contracts.Common.Models;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Host.Endpoints;

/// <summary>
///     Endpoint definitions for catalog operations (Title search).
/// </summary>
public static class CatalogEndpoints
{
    /// <summary>
    ///     Maps all catalog-related endpoints to the application.
    /// </summary>
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/catalog")
            .WithTags("Catalog");

        group.MapGet("", SearchCatalog)
            .WithName("SearchCatalog")
            .WithDescription("Search titles for catalog display with DAT release-completeness filter")
            .Produces<CommonModels.Page<Models.CatalogTitle>>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("filters", GetFilters)
            .WithName("GetCatalogFilters")
            .WithDescription("Get aggregated filter facets with counts")
            .Produces<Models.CatalogFilters>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("rating-boards", GetRatingBoards)
            .WithName("GetRatingBoards")
            .WithDescription("Get the canonical content rating board catalog and each board's valid categories")
            .Produces<Models.RatingBoardCatalogResponse>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        return app;
    }

    private static IResult GetRatingBoards()
    {
        var boards = Enum.GetValues<RatingBoard>()
            .Select(board => new Models.RatingBoardInfo
            {
                Board = board.ToContract(),
                Name = board.ToString(),
                Categories = RatingBoardCatalog.GetCategories(board)
                    .Select(category => new Models.RatingCategoryInfo
                    {
                        Code = category.Code,
                        Designation = category.Designation.ToContract(),
                        MinimumAge = category.MinimumAge
                    })
                    .ToList()
            })
            .ToList();

        return Results.Ok(new Models.RatingBoardCatalogResponse { Boards = boards });
    }

    private static async Task<IResult> SearchCatalog([FromServices] IReferenceCatalogService referenceCatalog,
        ISearchRepository searchRepository,
        ICurrentUser currentUser,
        string? query = null,
        string? systemKey = null,
        string? genre = null,
        string? releaseCompleteness = null,
        string? enrichmentStatus = null,
        string? tracked = null,
        string? sortBy = null,
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        // Strict validation (docs/decisions/admin-api-contract-policy.md): supplied-but-invalid
        // values fail with 400 preserving every failure; absent values keep documented defaults.
        var validationFailures = new Dictionary<string, string[]>();

        var sortField = TitleSortField.Name;
        if (ParseSortField(sortBy) is { } parsedSortField)
        {
            sortField = parsedSortField;
        }
        else
        {
            validationFailures["sortBy"] =
                [$"'{sortBy}' is not a valid sort field. Accepted values: name, rating, relevance."];
        }

        var releaseCompletenessFilter = ReleaseCompletenessFilter.All;
        if (ParseReleaseCompletenessFilter(releaseCompleteness) is { } parsedReleaseCompleteness)
        {
            releaseCompletenessFilter = parsedReleaseCompleteness;
        }
        else
        {
            validationFailures["releaseCompleteness"] =
                [$"'{releaseCompleteness}' is not a valid release-completeness filter. Accepted values: all, complete, partial, none."];
        }

        var trackedFilter = TrackedFilter.All;
        if (ParseTrackedFilter(tracked) is { } parsedTracked)
        {
            trackedFilter = parsedTracked;
        }
        else
        {
            validationFailures["tracked"] =
                [$"'{tracked}' is not a valid tracked filter. Accepted values: all, tracked, untracked."];
        }

        if (limit is < 1 or > MaxLimit)
        {
            validationFailures["limit"] = [$"Limit must be between 1 and {MaxLimit}."];
        }

        if (validationFailures.Count > 0)
        {
            return ProblemResults.ValidationProblem("Catalog.InvalidQuery", validationFailures);
        }

        // Validate relevance sort requires a query
        if (sortField == TitleSortField.Relevance && string.IsNullOrWhiteSpace(query))
        {
            return ProblemResults.ValidationProblem(
                "Catalog.RelevanceSortRequiresQuery",
                "sortBy",
                "Relevance sorting requires a search query.");
        }

        var filters = new TitleSearchFilters(
            PlatformId: (systemKey is null ? (int?)null : await referenceCatalog.ResolveSystemIdAsync(systemKey, cancellationToken) ?? 0),
            Genre: genre,
            ReleaseCompleteness: releaseCompletenessFilter,
            EnrichmentStatus: enrichmentStatus,
            Tracked: trackedFilter);

        // Resolve library ID for user (Contributors+ bypass filtering)
        int? libraryId = currentUser.HasRole(RomdRoleType.Contributor) ? null : currentUser.LibraryId;

        var result = await searchRepository.SearchTitlesAsync(
            query,
            filters,
            sortField,
            cursor,
            limit,
            libraryId,
            cancellationToken);

        return Results.Ok(result.ToContract(await referenceCatalog.GetSystemKeysAsync(cancellationToken)));
    }

    private const int MaxLimit = 100;

    private static TitleSortField? ParseSortField(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TitleSortField.Name; // documented default when absent
        }

        return value.ToLowerInvariant() switch
        {
            "name" => TitleSortField.Name,
            "rating" => TitleSortField.Rating,
            "relevance" => TitleSortField.Relevance,
            _ => null
        };
    }

    private static ReleaseCompletenessFilter? ParseReleaseCompletenessFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ReleaseCompletenessFilter.All; // documented default when absent
        }

        return value.ToLowerInvariant() switch
        {
            "all" => ReleaseCompletenessFilter.All,
            "complete" => ReleaseCompletenessFilter.Complete,
            "partial" => ReleaseCompletenessFilter.Partial,
            "none" => ReleaseCompletenessFilter.None,
            _ => null
        };
    }

    private static TrackedFilter? ParseTrackedFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TrackedFilter.All; // documented default when absent
        }

        return value.ToLowerInvariant() switch
        {
            "all" => TrackedFilter.All,
            "tracked" => TrackedFilter.Tracked,
            "untracked" => TrackedFilter.Untracked,
            _ => null
        };
    }

    /// <summary>
    ///     Maps PagedList of CatalogTitleData to Page of CatalogTitle contract.
    /// </summary>
    private static CommonModels.Page<Models.CatalogTitle> ToContract(this PagedList<CatalogTitleData> pagedList, SystemKeys systemKeys)
    {
        return new CommonModels.Page<Models.CatalogTitle>
        {
            Items = pagedList.Items.Select(item => item.ToContract(systemKeys)).ToList(),
            NextCursor = pagedList.NextCursor,
            HasNextPage = pagedList.HasNextPage
        };
    }

    private static async Task<IResult> GetFilters(IQueryHandler<GetCatalogFiltersQuery, Models.CatalogFilters> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetCatalogFiltersQuery(), ct);

        return result.Match(
            filters => Results.Ok(filters),
            errors => errors.ToProblem());
    }
}
