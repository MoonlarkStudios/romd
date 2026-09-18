using Microsoft.Net.Http.Headers;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Delivery;

namespace Romd.Host.Endpoints;

public static class ConsumerStorageEndpoints
{
    public static IEndpointRouteBuilder MapConsumerStorageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/media/{mediaId}", ServeConsumerMedia)
            .WithName("ServeMedia")
            .WithTags("Storage")
            .AllowAnonymous()
            .CacheOutput(policy => policy.Expire(TimeSpan.FromDays(365)));

        app.MapGet("/delivery/content/{token}", RedeemConsumerContentGrant)
            .WithName("RedeemConsumerContentGrant")
            .WithTags("Consumer Delivery")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .AllowAnonymous();

        app.MapGet("/delivery/bios/{token}", RedeemConsumerBiosGrant)
            .WithName("RedeemConsumerBiosGrant")
            .WithTags("Consumer Delivery")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .AllowAnonymous();

        return app;
    }

    private static async Task<IResult> RedeemConsumerBiosGrant(
        string token,
        HttpContext context,
        IConsumerBiosGrantIssuer grantIssuer,
        IConsumerContentArtifactResolver artifactResolver,
        CancellationToken cancellationToken)
    {
        var validated = grantIssuer.ValidateBiosDownloadGrant(token);
        if (validated.IsError)
        {
            return validated.Errors.ToProblem();
        }

        var grant = validated.Value.Grant;
        var stream = await artifactResolver.ResolveAsync(grant.Sha256, cancellationToken);
        if (stream is null)
        {
            return new[] { ConsumerErrors.ContentNotFound() }.ToProblem();
        }

        context.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue
        {
            Private = true,
            NoStore = true
        };
        context.Response.ContentLength = grant.SizeBytes;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Remove(HeaderNames.AcceptRanges);
            context.Response.Headers.Remove(HeaderNames.ContentRange);
            return Task.CompletedTask;
        });

        return Results.File(stream, "application/octet-stream", fileDownloadName: "romd-bios.bin");
    }

    private static async Task<IResult> RedeemConsumerContentGrant(
        string token,
        HttpContext context,
        IConsumerContentGrantIssuer grantIssuer,
        IConsumerContentArtifactResolver artifactResolver,
        CancellationToken cancellationToken)
    {
        var validated = grantIssuer.ValidateDownloadGrant(token);
        if (validated.IsError)
        {
            return validated.Errors.ToProblem();
        }

        var grant = validated.Value.Grant;
        var stream = await artifactResolver.ResolveAsync(grant.Sha256, cancellationToken);
        if (stream is null)
        {
            return new[] { ConsumerErrors.ContentNotFound() }.ToProblem();
        }

        context.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue
        {
            Private = true,
            NoStore = true
        };
        context.Response.ContentLength = grant.SizeBytes;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Remove(HeaderNames.AcceptRanges);
            context.Response.Headers.Remove(HeaderNames.ContentRange);
            return Task.CompletedTask;
        });

        return Results.File(stream, "application/octet-stream", fileDownloadName: "romd-content.bin");
    }

    private static async Task<IResult> ServeConsumerMedia(
        Sqid mediaId,
        HttpContext context,
        IConsumerMediaArtifactResolver artifactResolver,
        CancellationToken cancellationToken)
    {
        var artifact = await artifactResolver.ResolveMediaAsync(
            new ConsumerMediaArtifactRequest(mediaId.Value),
            cancellationToken);
        if (artifact is null)
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

        return Results.File(artifact.Content, artifact.ContentType);
    }
}
