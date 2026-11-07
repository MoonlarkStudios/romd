using ErrorOr;
using System.ComponentModel.DataAnnotations;
using Romd.Contracts.Common.Models;
using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.ReferenceData.Assets.Commands.UploadReferenceAsset;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ReferenceCatalogEndpoints
{
    public static IEndpointRouteBuilder MapReferenceCatalogEndpoints(this IEndpointRouteBuilder app, bool admin)
    {
        var prefix = admin ? "Admin" : "Consumer";
        app.MapGet("catalog-snapshot", Current).WithTags("Catalog snapshots").WithName("Get" + prefix + "CatalogSnapshot")
            .RequireAuthorization(AuthorizationPolicies.RequireUser).Produces<ReferenceCatalogDescriptor>().ConditionalRead().ProducesProblems(503);
        MapSnapshotPages(app, prefix);

        app.MapGet("assets/{hash}", Asset).WithTags("Reference assets").WithName("Get" + prefix + "ReferenceAsset")
            .AllowAnonymous().Produces(200).ProducesProblems(404);
        if (admin)
            app.MapPost("assets", UploadAsset).WithTags("Reference assets").WithName("UploadReferenceAsset")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin).ProducesCreated<string>().ProducesProblems(400, 413);

        app.MapSystemReferences(admin);
        app.MapCompanyReferences(admin);
        app.MapRegionReferences(admin);
        app.MapLanguageReferences(admin);
        app.MapRatingBoardReferences();
        app.MapRatingReferences();
        return app;
    }

    /// <summary>The errorCode area of the reference resources' preconditions.</summary>
    internal const string PreconditionArea = "ReferenceResource";

    /// <summary>An unresolved company, region, language, rating board or rating key.</summary>
    internal static IResult UnknownReference() =>
        ProblemResults.Problem(StatusCodes.Status404NotFound, ProblemCodes.ReferenceResourceNotFound, "Unknown reference identity.");

    /// <summary>
    ///     A system the key seam resolved but its reader no longer finds, answered as the seam answers an unknown key.
    /// </summary>
    internal static IResult UnknownSystem() =>
        ProblemResults.Problem(StatusCodes.Status404NotFound, ProblemCodes.SystemsNotFound, "No system has this key.");

    private static async Task<IResult> Current([FromServices] IReferenceCatalogService service, HttpContext context, CancellationToken ct)
    {
        var catalog = await service.GetCurrentAsync(ct);
        return catalog is null
            ? ProblemResults.Problem(503, ProblemCodes.ReferenceCatalogUnavailable, "No reference catalog has been published yet.")
            : Descriptor(context, catalog);
    }
    private static IResult Descriptor(HttpContext context, ReferenceCatalog catalog)
    {
        var descriptor = ReferenceCatalogCanonicalJson.Describe(catalog);
        return Preconditions.ConditionalRead(context, descriptor, descriptor.Revision);
    }
    private static void MapSnapshotPages(IEndpointRouteBuilder app, string prefix)
    {
        MapSnapshotPage(app, prefix, "systems", "Systems", (ReferenceCatalog x) => x.Systems, x => x.Key);
        MapSnapshotPage(app, prefix, "companies", "Companies", (ReferenceCatalog x) => x.Companies, x => x.Key);
        MapSnapshotPage(app, prefix, "regions", "Regions", (ReferenceCatalog x) => x.Regions, x => x.Key);
        MapSnapshotPage(app, prefix, "languages", "Languages", (ReferenceCatalog x) => x.Languages, x => x.Key);
        MapSnapshotPage(app, prefix, "rating-boards", "RatingBoards", (ReferenceCatalog x) => x.RatingBoards, x => x.Key);
        MapSnapshotPage(app, prefix, "ratings", "Ratings", (ReferenceCatalog x) => x.Ratings, x => x.Board + ":" + x.Code);
    }
    private static void MapSnapshotPage<T>(IEndpointRouteBuilder app, string prefix, string family, string name,
        Func<ReferenceCatalog, IReadOnlyList<T>> select, Func<T, string> key)
    {
        // A typed local delegate avoids Minimal API's generic method-group inference ambiguity.
        async Task<IResult> Read([FromServices] IReferenceCatalogService service, string revision, string? cursor = null,
            [Range(1, ReferencePageRequest.MaxLimit)] int limit = ReferencePageRequest.DefaultLimit, CancellationToken ct = default)
        {
            var request = ReferenceSnapshotPageRequest.Create(prefix + ":catalog-snapshot:" + family, revision, cursor, limit);
            if (request.IsError)
                return request.Errors.ToProblem();
            // One stored JSON row supplies the identity AND every fact. Never mix a pin with live tables.
            var catalog = await service.GetCurrentAsync(ct);
            if (catalog is null)
                return ProblemResults.Problem(503, ProblemCodes.ReferenceCatalogUnavailable, "No reference catalog has been published yet.");
            if (catalog.Revision != request.Value.Revision)
                return ProblemResults.Problem(409, "ReferenceCatalog.SnapshotChanged", "The published catalog changed. Discard staged pages and fetch a new descriptor.");
            var page = request.Value.Read(select(catalog), key);
            return page.IsError ? page.Errors.ToProblem() : Results.Ok(page.Value);
        }
        var handler = (Func<IReferenceCatalogService, string, string?, int, CancellationToken, Task<IResult>>)Read;
        app.MapGet("catalog-snapshot/" + family, handler).WithTags("Catalog snapshots")
            .WithName("List" + prefix + "CatalogSnapshot" + name).RequireAuthorization(AuthorizationPolicies.RequireUser)
            .Produces<Page<T>>().ProducesProblems(409, 503)
            .WithDescription("Members of one published schema-v1 catalog in its exact published sequence. Required revision is the descriptor catalogRevision. Default/max limit 100. The current-only publication may change between requests: 409 discards the entire staged traversal. Successful pages are 200 and have no ETag or conditional-read behavior.");
    }
    private static async Task<IResult> Asset(string hash, [FromServices] IReferenceCatalogService service, HttpContext context, CancellationToken ct)
    {
        var asset = await service.GetAssetAsync(hash, ct);
        if (asset is null)
            return ProblemResults.Problem(404, ProblemCodes.ReferenceAssetNotFound, "Unknown reference asset.");
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(asset.Bytes, asset.ContentType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{hash}\""));
    }
    private static async Task<IResult> UploadAsset(HttpRequest request, [FromServices] ICommandHandler<UploadReferenceAssetCommand, string> handler, CancellationToken ct)
    {
        const int max = 2097152;
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int count;
        while ((count = await request.Body.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + count > max)
                return ProblemResults.Problem(StatusCodes.Status413PayloadTooLarge, ProblemCodes.RequestPayloadTooLarge,
                    "The request body exceeds the allowed size.");
            buffer.Write(chunk, 0, count);
        }
        var result = await handler.HandleAsync(new UploadReferenceAssetCommand(buffer.ToArray(), request.ContentType ?? ""), ct);
        return result.IsError ? result.Errors.ToProblem() : Results.Created("/api/assets/" + result.Value, result.Value);
    }
}
