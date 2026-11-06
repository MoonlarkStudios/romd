using System.ComponentModel.DataAnnotations;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Admin.Application.Search.Queries.SearchGames;
using Romd.Admin.Application.Source;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Dat;
using Romd.Host.Authorization;
using CommonModels = Romd.Contracts.Common.Models;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Host.Endpoints;

/// <summary>
///     Endpoint definitions for search operations.
/// </summary>
public static class SearchEndpoints
{
    /// <summary>
    ///     Maps all search-related endpoints to the application.
    /// </summary>
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/search")
            .WithTags("Search");

        group.MapGet("games", SearchGames)
            .WithName("SearchGames")
            .WithDescription("Search games using full-text search with filters and pagination")
            .Produces<CommonModels.Page<Models.DatGame>>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        return app;
    }

    private static async Task<IResult> SearchGames(
        IQueryHandler<SearchGamesQuery, PagedList<DatGame>> handler,
        string? query = null,
        SystemKeyParameter? systemKey = null,
        string? year = null,
        string? manufacturer = null,
        Sqid? regionId = null,
        string? bios = null,
        string? sortBy = null,
        string? cursor = null,
        [Range(1, SearchGamesQuery.MaxLimit)] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        // Supplied-but-invalid values fail with 400 preserving every failure; absent values keep
        // documented defaults (docs/decisions/api-contract-policy.md, "Strict Validation").
        var validationFailures = new Dictionary<string, string[]>();

        var biosFilter = BiosFilter.Exclude;
        if (ParseBiosFilter(bios) is { } parsedBios)
        {
            biosFilter = parsedBios;
        }
        else
        {
            validationFailures["bios"] =
                [$"'{bios}' is not a valid BIOS filter. Accepted values: exclude, include, only."];
        }

        var sortField = GameSortField.Name;
        if (ParseSortField(sortBy) is { } parsedSortField)
        {
            sortField = parsedSortField;
        }
        else
        {
            validationFailures["sortBy"] =
                [$"'{sortBy}' is not a valid sort field. Accepted values: name, year, relevance."];
        }

        if (validationFailures.Count > 0)
        {
            return ProblemResults.ValidationProblem("Search.InvalidQuery", validationFailures);
        }

        var searchQuery = new SearchGamesQuery
        {
            Query = query,
            PlatformId = systemKey?.SystemId,
            Year = year,
            Manufacturer = manufacturer,
            RegionId = regionId?.Value,
            BiosFilter = biosFilter,
            SortBy = sortField,
            Cursor = cursor,
            Limit = limit
        };

        var result = await handler.HandleAsync(searchQuery, cancellationToken);
        return result.Match(
            pagedGames => Results.Ok(pagedGames.ToContract()),
            errors => errors.ToProblem());
    }

    private static BiosFilter? ParseBiosFilter(string? value)
    {
        if (value is null)
        {
            return BiosFilter.Exclude; // documented default when absent
        }

        return value.ToLowerInvariant() switch
        {
            "exclude" => BiosFilter.Exclude,
            "include" => BiosFilter.Include,
            "only" => BiosFilter.Only,
            _ => null
        };
    }

    private static GameSortField? ParseSortField(string? value)
    {
        if (value is null)
        {
            return GameSortField.Name; // documented default when absent
        }

        return value.ToLowerInvariant() switch
        {
            "name" => GameSortField.Name,
            "year" => GameSortField.Year,
            "relevance" => GameSortField.Relevance,
            _ => null
        };
    }
}
