using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class RatingReferenceEndpoints
{
    public static void MapRatingReferences(this IEndpointRouteBuilder app, bool admin)
    {
        var prefix = admin ? "Admin" : "Consumer";
        var group = app.MapGroup("rating-boards/{board}/ratings").WithTags("Ratings").RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("", async (string board, [FromServices] IRatingReferenceReader reader, [FromServices] IRatingBoardReferenceReader boards, CancellationToken ct) =>
            await boards.GetAsync(board, ct) is null ? Results.NotFound() : Results.Ok((await reader.ListAsync(ct)).Where(x => x.Board == board)))
            .WithName("List" + prefix + "Ratings").Produces<IReadOnlyList<RatingResourceDto>>().Produces(404);
        group.MapGet("{code}", async (string board, string code, [FromServices] IRatingReferenceReader reader, HttpContext context, CancellationToken ct) =>
            ReferenceHttp.Read(context, await reader.GetAsync(board + ":" + code, ct)))
            .WithName("Get" + prefix + "Rating").Produces<RatingResourceDto>().Produces(304).Produces(404);
    }
}
