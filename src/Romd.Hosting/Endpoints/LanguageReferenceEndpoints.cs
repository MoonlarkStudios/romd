using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class LanguageReferenceEndpoints
{
    public static void MapLanguageReferences(this IEndpointRouteBuilder app, bool admin)
    {
        var prefix = admin ? "Admin" : "Consumer";
        var group = app.MapGroup("languages").WithTags("languages").RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("", async ([FromServices] ILanguageReferenceReader reader, CancellationToken ct) => Results.Ok(await reader.ListAsync(ct)))
            .WithName("List" + prefix + "Languages").Produces<IReadOnlyList<LanguageResourceDto>>();
        group.MapGet("{key}", async (string key, [FromServices] ILanguageReferenceReader reader, HttpContext context, CancellationToken ct) =>
            ReferenceHttp.Read(context, await reader.GetAsync(key, ct)))
            .WithName("Get" + prefix + "Languages").Produces<LanguageResourceDto>().Produces(304).Produces(404);
    }
}
