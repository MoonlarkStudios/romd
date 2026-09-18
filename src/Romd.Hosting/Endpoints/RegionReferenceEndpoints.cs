using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class RegionReferenceEndpoints
{
    public static void MapRegionReferences(this IEndpointRouteBuilder app, bool admin)
    {
        var prefix = admin ? "Admin" : "Consumer";
        var group = app.MapGroup("regions").WithTags("regions").RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("", async ([FromServices] IRegionReferenceReader reader, CancellationToken ct) => Results.Ok(await reader.ListAsync(ct)))
            .WithName("List" + prefix + "Regions").Produces<IReadOnlyList<RegionResourceDto>>();
        group.MapGet("{key}", async (string key, [FromServices] IRegionReferenceReader reader, HttpContext context, CancellationToken ct) =>
            ReferenceHttp.Read(context, await reader.GetAsync(key, ct)))
            .WithName("Get" + prefix + "Regions").Produces<RegionResourceDto>().Produces(304).Produces(404);
    }
}
