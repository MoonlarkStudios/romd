using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Ids;
using Romd.Admin.Application.Storage.Files;
using Romd.Persistence;

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
        RomdDbContext dbContext,
        IFileStorageService fileStorage,
        CancellationToken cancellationToken)
    {
        var media = await dbContext.TitleMedia
            .AsNoTracking()
            .Where(m => m.Id == mediaId.Value)
            .Select(m => new { m.FileId, m.ContentType })
            .FirstOrDefaultAsync(cancellationToken);

        if (media is null)
        {
            return Results.NotFound();
        }

        var stream = await fileStorage.RetrieveByIdAsync(media.FileId, cancellationToken);
        if (stream is null)
        {
            return Results.NotFound();
        }

        context.Response.GetTypedHeaders().CacheControl =
            new Microsoft.Net.Http.Headers.CacheControlHeaderValue
            {
                Public = true,
                MaxAge = TimeSpan.FromDays(365),
                Extensions = { new Microsoft.Net.Http.Headers.NameValueHeaderValue("immutable") }
            };

        return Results.File(stream, media.ContentType);
    }
}
