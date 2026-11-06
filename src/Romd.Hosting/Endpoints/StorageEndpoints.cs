using ErrorOr;
using Microsoft.Net.Http.Headers;
using Romd.Admin.Application.Titles.Queries.OpenTitleMedia;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;

namespace Romd.Host.Endpoints;

/// <summary>
///     Endpoint definitions for serving stored media files.
/// </summary>
public static class StorageEndpoints
{
    public static IEndpointRouteBuilder MapStorageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/media/{mediaId}", ServeMedia)
            .WithName("ServeMedia")
            .WithTags("Storage")
            .AllowAnonymous()
            .CacheOutput(policy => policy.Expire(TimeSpan.FromDays(365)));

        return app;
    }

    private static async Task<IResult> ServeMedia(
        Sqid mediaId,
        HttpContext context,
        IQueryHandler<OpenTitleMediaQuery, TitleMediaContent> openMedia,
        CancellationToken cancellationToken)
    {
        var opened = await openMedia.HandleAsync(new OpenTitleMediaQuery(mediaId.Value), cancellationToken);
        // Anonymous media answers the delivery 404 that /artwork uses, without the internal id the
        // error describes.
        if (opened.IsError)
        {
            return opened.FirstError.Type == ErrorType.NotFound
                ? ProblemResults.Problem(StatusCodes.Status404NotFound, ProblemCodes.DeliveryContentNotFound,
                    "The media is unavailable.")
                : opened.Errors.ToProblem();
        }

        var media = opened.Value;
        try
        {
            context.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue
            {
                Public = true,
                MaxAge = TimeSpan.FromDays(365),
                Extensions = { new NameValueHeaderValue("immutable") }
            };

            // Results.File owns the stream from here and disposes it when the response completes or fails.
            return Results.File(media.Content, media.ContentType);
        }
        catch
        {
            await media.Content.DisposeAsync();
            throw;
        }
    }
}
