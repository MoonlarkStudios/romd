using Microsoft.Net.Http.Headers;
using Romd.Application.Common.Artwork;
using Romd.Application.Common.Ids;

namespace Romd.Host.Endpoints;

public static class ArtworkDeliveryEndpoints
{
    public static IEndpointRouteBuilder MapArtworkDeliveryEndpoints(this IEndpointRouteBuilder app)
    {
        // Like existing title media, selected library artwork is publicly cacheable.
        // The exact retained version is checked; a different version cannot alias this URL.
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
        if (artifact is null) return Results.NotFound();
        context.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue
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
