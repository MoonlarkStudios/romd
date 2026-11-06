using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class RatingBoardReferenceEndpoints
{
    /// <summary>
    ///     A board's key binds as <c>{board}</c>, the name every route and contract field carrying a rating board's
    ///     key uses, such as <c>/api/titles/{titleId}/content-ratings/{board}</c>.
    /// </summary>
    public static void MapRatingBoardReferences(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("rating-boards").WithTags("rating-boards").RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("", async ([FromServices] IRatingBoardReferenceReader reader, CancellationToken ct) => Results.Ok(await reader.ListAsync(ct)))
            .WithName("ListRatingBoards")
            .WithDescription("Every rating board ROMD defines; bounded by the built-in reference catalog.")
            .Produces<IReadOnlyList<RatingBoardResourceDto>>();
        group.MapGet("{board}", async (string board, [FromServices] IRatingBoardReferenceReader reader, HttpContext context, CancellationToken ct) =>
            await reader.GetAsync(board, ct) is { } found
                ? Preconditions.ConditionalRead(context, found.Resource, found.Revision)
                : ReferenceCatalogEndpoints.UnknownReference())
            .WithName("GetRatingBoard").Produces<RatingBoardResourceDto>().ConditionalRead().ProducesProblems(404);
    }
}
