using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ReferenceCatalogEndpoints
{
    public static IEndpointRouteBuilder MapReferenceCatalogEndpoints(this IEndpointRouteBuilder app, bool admin)
    {
        var prefix = admin ? "Admin" : "Consumer";
        app.MapGet("catalog-snapshot", Current).WithTags("Catalog snapshots").WithName("Get" + prefix + "CatalogSnapshot")
            .RequireAuthorization(AuthorizationPolicies.RequireUser).Produces<ReferenceCatalogDto>().Produces(304).Produces(503);

        app.MapGet("assets/{hash}", Asset).WithTags("Reference assets").WithName("Get" + prefix + "ReferenceAsset")
            .AllowAnonymous().Produces(200).Produces(404);
        if (admin) app.MapPost("assets", UploadAsset).WithTags("Reference assets").WithName("UploadReferenceAsset")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin).Produces<string>(201).Produces<ProblemDetails>(400).Produces(413);

        app.MapSystemReferences(admin);
        app.MapCompanyReferences(admin);
        app.MapRegionReferences(admin);
        app.MapLanguageReferences(admin);
        app.MapRatingBoardReferences(admin);
        app.MapRatingReferences(admin);
        return app;
    }

    private static bool Matches(HttpContext context, string etag) => context.Request.GetTypedHeaders().IfNoneMatch?.Any(x => x.Tag.Value == etag || x.Tag.Value == "*") == true;
    private static async Task<IResult> Current([FromServices] IReferenceCatalogService service, HttpContext context, CancellationToken ct)
    {
        var catalog = await service.GetCurrentAsync(ct);
        return catalog is null ? Results.StatusCode(503) : CatalogResult(context, catalog);
    }
    private static IResult CatalogResult(HttpContext context, ReferenceCatalogDto catalog)
    {
        var etag = $"\"{catalog.Revision}\"";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "private, no-cache";
        return Matches(context, etag) ? Results.StatusCode(304) : Results.Ok(catalog);
    }
    private static async Task<IResult> Asset(string hash, [FromServices] IReferenceCatalogService service, HttpContext context, CancellationToken ct)
    {
        var asset = await service.GetAssetAsync(hash, ct);
        if (asset is null) return Results.NotFound();
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(asset.Bytes, asset.ContentType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{hash}\""));
    }
    private static async Task<IResult> UploadAsset(HttpRequest request, [FromServices] IReferenceCatalogService service, CancellationToken ct)
    {
        const int max = 2097152;
        using var buffer = new MemoryStream(); var chunk = new byte[16384]; int count;
        while ((count = await request.Body.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + count > max) return Results.StatusCode(413);
            buffer.Write(chunk, 0, count);
        }
        var result = await service.AddAssetAsync(buffer.ToArray(), request.ContentType ?? "", ct);
        return result.IsError ? result.Errors.ToProblem() : Results.Created("/api/assets/" + result.Value, result.Value);
    }
}
