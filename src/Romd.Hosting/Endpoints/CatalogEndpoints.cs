using System.ComponentModel.DataAnnotations;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Systems;
using Romd.Admin.Application.Catalog;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Catalog.Queries.GetCatalogFilters;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Search;
using Romd.Admin.Application.Search.ReadModels;
using Romd.Domain.Identity;
using Romd.Host.Authorization;
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
            .WithName("ListCatalogTitles")
            .WithDescription("Search titles for catalog display with DAT release-completeness filter")
            .Produces<Romd.Contracts.Common.Models.Page<Models.CatalogTitle>>().ProducesProblems(400)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("stats", CountCatalog)
            .WithName("GetCatalogStats")
            .WithDescription("Count matching catalog titles without fetching results or artwork")
            .Produces<Models.CatalogCount>().ProducesProblems(400)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("filters", GetFilters)
            .WithName("GetCatalogFilters")
            .WithDescription("Get aggregated filter facets with counts")
            .Produces<Models.CatalogFilters>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        return app;
    }

    private static Task<IResult> SearchCatalog([FromServices] IReferenceCatalogService referenceCatalog,
        ISearchRepository searchRepository,
        ICurrentUser currentUser,
        string? query = null,
        SystemKeyParameter? systemKey = null,
        string? genre = null,
        [ClosedValues("all", "complete", "partial", "none")] string? releaseCompleteness = null,
        [ClosedValues("all", "missing", "upgrades")] string? releaseReview = null,
        [ClosedValues("None", "Pending", "Completed", "Failed", "NotFound", "LowConfidence")] string? enrichmentStatus = null,
        string[]? enrichment = null,
        [ClosedValues("Poster", "Backdrop", "Logo", "Hero", "NeedsMatch")] string? artworkStatus = null,
        [ClosedValues("all", "tracked", "untracked")] string? tracked = null,
        [ClosedValues("name", "rating", "relevance")] string? sortBy = null,
        string? cursor = null,
        [Range(1, MaxLimit)] int limit = 50,
        CancellationToken cancellationToken = default)

        => ReadCatalog(referenceCatalog, searchRepository, currentUser, query, systemKey, genre, releaseCompleteness, releaseReview, enrichmentStatus, enrichment, artworkStatus, tracked, sortBy, cursor, limit, cancellationToken, false);

    private static Task<IResult> CountCatalog([FromServices] IReferenceCatalogService referenceCatalog,
        ISearchRepository searchRepository, ICurrentUser currentUser,
        string? query = null, SystemKeyParameter? systemKey = null, string? genre = null,
        [ClosedValues("all", "complete", "partial", "none")] string? releaseCompleteness = null, [ClosedValues("all", "missing", "upgrades")] string? releaseReview = null,
        [ClosedValues("None", "Pending", "Completed", "Failed", "NotFound", "LowConfidence")] string? enrichmentStatus = null, string[]? enrichment = null,
        [ClosedValues("Poster", "Backdrop", "Logo", "Hero", "NeedsMatch")] string? artworkStatus = null, [ClosedValues("all", "tracked", "untracked")] string? tracked = null,
        CancellationToken cancellationToken = default)
        => ReadCatalog(referenceCatalog, searchRepository, currentUser, query, systemKey, genre,
            releaseCompleteness, releaseReview, enrichmentStatus, enrichment, artworkStatus, tracked,
            null, null, 50, cancellationToken, true);

    private static async Task<IResult> ReadCatalog([FromServices] IReferenceCatalogService referenceCatalog,
        ISearchRepository searchRepository,
        ICurrentUser currentUser,
        string? query = null,
        SystemKeyParameter? systemKey = null,
        string? genre = null,
        [ClosedValues("all", "complete", "partial", "none")] string? releaseCompleteness = null,
        [ClosedValues("all", "missing", "upgrades")] string? releaseReview = null,
        [ClosedValues("None", "Pending", "Completed", "Failed", "NotFound", "LowConfidence")] string? enrichmentStatus = null,
        string[]? enrichment = null,
        [ClosedValues("Poster", "Backdrop", "Logo", "Hero", "NeedsMatch")] string? artworkStatus = null,
        [ClosedValues("all", "tracked", "untracked")] string? tracked = null,
        [ClosedValues("name", "rating", "relevance")] string? sortBy = null,
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default,
        bool countOnly = false)
    {
        // Supplied-but-invalid values fail with 400 preserving every failure; absent values keep
        // documented defaults (docs/decisions/api-contract-policy.md, "Strict Validation").
        var validationFailures = new Dictionary<string, string[]>();

        if (artworkStatus is not (null or "Poster" or "Backdrop" or "Logo" or "Hero" or "NeedsMatch"))
            validationFailures["artworkStatus"] = ["Choose Poster, Backdrop, Logo, Hero, or NeedsMatch."];
        if (!string.IsNullOrWhiteSpace(enrichmentStatus) && !EnrichmentFilterParser.IsLastAttemptStatus(enrichmentStatus))
            validationFailures["enrichmentStatus"] =
                [$"'{enrichmentStatus}' is not a valid last-attempt status. Accepted values: None, Pending, Completed, Failed, NotFound, LowConfidence. Filter assessment flags with 'enrichment'."];
        EnrichmentFlagFilter[]? enrichmentFlags = null;
        // A query string cannot express an empty list; a supplied blank value binds as an entry and is rejected.
        if (enrichment is { Length: > 0 })
        {
            var parsedEnrichment = EnrichmentFilterParser.ParseFlags(enrichment);
            if (parsedEnrichment.IsError)
                validationFailures["enrichment"] = parsedEnrichment.Errors.Select(error => error.Description).ToArray();
            else
                enrichmentFlags = parsedEnrichment.Value;
        }
        var review = releaseReview switch
        {
            "missing" => (Romd.Admin.Application.TrackedCollection.TrackedCollectionView?)Romd.Admin.Application.TrackedCollection.TrackedCollectionView.Missing,
            "upgrades" => Romd.Admin.Application.TrackedCollection.TrackedCollectionView.Upgrades,
            _ => null
        };
        if (releaseReview is not (null or "all" or "missing" or "upgrades"))
            validationFailures["releaseReview"] = ["Choose all, missing, or upgrades."];

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
            PlatformId: systemKey?.SystemId,
            Genre: genre,
            ReleaseCompleteness: releaseCompletenessFilter,
            EnrichmentStatus: enrichmentStatus,
            Tracked: trackedFilter, ArtworkStatus: artworkStatus, Enrichment: enrichmentFlags, ReleaseReview: review);

        // Resolve library ID for user (Contributors+ bypass filtering)
        int? libraryId = currentUser.HasRole(RomdRoleType.Contributor) ? null : currentUser.LibraryId;

        var result = await searchRepository.SearchTitlesAsync(
            query,
            filters,
            sortField,
            cursor,
            limit,
            libraryId,
            cancellationToken, includeTotal: countOnly, countOnly: countOnly);

        if (result.IsError)
            return result.Errors.ToProblem();
        if (countOnly)
            return Results.Ok(new Models.CatalogCount(result.Value.TotalCount ?? 0));
        return Results.Ok(result.Value.ToContract(await referenceCatalog.GetSystemKeysAsync(cancellationToken)));
    }

    /// <summary>The largest catalog page; the repository serves whatever the endpoint admits.</summary>
    private const int MaxLimit = SearchCursors.MaxLimit;

    private static TitleSortField? ParseSortField(string? value)
    {
        if (value is null)
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
        if (value is null)
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
        if (value is null)
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
    private static Romd.Contracts.Common.Models.Page<Models.CatalogTitle> ToContract(this CatalogSearchPage pagedList, SystemKeys systemKeys)
    {
        return new Romd.Contracts.Common.Models.Page<Models.CatalogTitle>
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
