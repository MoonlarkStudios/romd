using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class RatingBoardReferenceEndpoints
{
    public static void MapRatingBoardReferences(this IEndpointRouteBuilder app, bool admin)
    {
        var prefix = admin ? "Admin" : "Consumer";
        var group = app.MapGroup("rating-boards").WithTags("rating-boards").RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("", async ([FromServices] IRatingBoardReferenceReader reader, CancellationToken ct) => Results.Ok(await reader.ListAsync(ct)))
            .WithName("List" + prefix + "RatingBoards").Produces<IReadOnlyList<RatingBoardResourceDto>>();
        group.MapGet("{key}", async (string key, [FromServices] IRatingBoardReferenceReader reader, HttpContext context, CancellationToken ct) =>
            ReferenceHttp.Read(context, await reader.GetAsync(key, ct)))
            .WithName("Get" + prefix + "RatingBoards").Produces<RatingBoardResourceDto>().Produces(304).Produces(404);
    }
}
