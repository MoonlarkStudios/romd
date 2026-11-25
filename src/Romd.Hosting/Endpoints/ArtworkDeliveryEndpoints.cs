using Microsoft.Net.Http.Headers;
using Romd.Application.Common.Artwork;
using Romd.Application.Common.Ids;

namespace Romd.Host.Endpoints;

public static class ArtworkDeliveryEndpoints
{
    public static IEndpointRouteBuilder MapArtworkDeliveryEndpoints(this IEndpointRouteBuilder app)
    {
        // Retained versions use stable URLs. Provisional variants recheck authorization on each request.
        app.MapGet("/artwork/{assetId}/{variantName}/{contentVersion}", ServeArtwork)
            .WithName("ServeArtwork")
            .WithTags("Storage")
            .AllowAnonymous();
        return app;
    }

    private static async Task<IResult> ServeArtwork(Sqid assetId, string variantName, string contentVersion,
        HttpContext context, IArtworkDelivery delivery, CancellationToken ct)
    {
        var artifact = await delivery.OpenAsync(assetId.Value, variantName, contentVersion, ct);
        if (artifact is null)
        {
            context.Response.Headers.CacheControl = "no-store";
            return ProblemResults.Problem(StatusCodes.Status404NotFound, ProblemCodes.DeliveryContentNotFound,
                "The artwork is unavailable.");
        }
        context.Response.GetTypedHeaders().CacheControl = artifact.IsProvisional
            ? new CacheControlHeaderValue { NoStore = true }
            : new CacheControlHeaderValue
            {
                Public = true,
                MaxAge = TimeSpan.FromDays(365),
                Extensions = { new NameValueHeaderValue("immutable") }
            };
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(artifact.Content, artifact.ContentType,
            entityTag: new EntityTagHeaderValue($"\"{artifact.ContentHash}\""));
    }
}
