using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class RatingReferenceEndpoints
{
    public static void MapRatingReferences(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("rating-boards/{board}/ratings").WithTags("Ratings").RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("", async (string board, [FromServices] IRatingReferenceReader reader, [FromServices] IRatingBoardReferenceReader boards, CancellationToken ct) =>
            await boards.GetAsync(board, ct) is null ? ReferenceCatalogEndpoints.UnknownReference() : Results.Ok((await reader.ListAsync(ct)).Where(x => x.Board == board)))
            .WithName("ListRatings")
            .WithDescription("Every rating of one board; bounded by the built-in reference catalog.")
            .Produces<IReadOnlyList<RatingResourceDto>>().ProducesProblems(404);
        group.MapGet("{ratingCode}", async (string board, string ratingCode, [FromServices] IRatingReferenceReader reader, HttpContext context, CancellationToken ct) =>
            await reader.GetAsync(board + ":" + ratingCode, ct) is { } found
                ? Preconditions.ConditionalRead(context, found.Resource, found.Revision)
                : ReferenceCatalogEndpoints.UnknownReference())
            .WithName("GetRating").Produces<RatingResourceDto>().ConditionalRead().ProducesProblems(404);
    }
}
