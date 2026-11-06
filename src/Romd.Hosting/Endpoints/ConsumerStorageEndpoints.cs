using Microsoft.Net.Http.Headers;
using Romd.Application.Common.Content;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Delivery;

namespace Romd.Host.Endpoints;

public static partial class ConsumerStorageEndpoints
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
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .AllowAnonymous();

        app.MapGet("/delivery/bios/{token}", RedeemConsumerBiosGrant)
            .WithName("RedeemConsumerBiosGrant")
            .WithTags("Consumer Delivery")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .AllowAnonymous();

        app.MapGet("/delivery/derived-descriptor/{token}", RedeemDerivedDescriptorGrant)
            .WithName("RedeemConsumerDerivedDescriptorGrant").WithTags("Consumer Delivery")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status404NotFound).AllowAnonymous();

        app.MapGet("/delivery/original-layout/{token}", RedeemOriginalLayoutGrant)
            .WithName("RedeemConsumerOriginalLayoutGrant").WithTags("Consumer Delivery")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status404NotFound).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> RedeemOriginalLayoutGrant(string token, HttpContext context,
        IConsumerOriginalLayoutGrantIssuer issuer, IConsumerContentArtifactResolver artifacts, CancellationToken ct)
    {
        var validated = issuer.Validate(token);
        if (validated.IsError)
            return validated.Errors.ToProblem();
        var grant = validated.Value.Grant;
        await using var source = await artifacts.ResolveAsync(grant.Sha256, ct);
        if (source is null)
            return new[] { ConsumerErrors.ContentNotFound() }.ToProblem();
        // Descriptors are bounded; verify exact pinned bytes before committing response headers.
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > grant.SizeBytes)
                return new[] { ConsumerErrors.ContentNotFound() }.ToProblem();
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }
        byte[] bytes = buffer.ToArray();
        if (bytes.LongLength != grant.SizeBytes || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != grant.Sha256.ToString())
            return new[] { ConsumerErrors.ContentNotFound() }.ToProblem();
        context.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue { Private = true, NoStore = true };
        return Results.File(bytes, "application/octet-stream", SafeDownloadFilename(grant.Filename));
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
        Stream? stream;
        try
        {
            stream = await artifactResolver.ResolveAsync(grant.Target, cancellationToken);
        }
        catch (ResourceCapacityException) { return ContentCapacityResponse.Unavailable(context); }
        if (stream is null)
        {
            return new[] { ConsumerErrors.ContentNotFound() }.ToProblem();
        }

        context.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue
        {
            Private = true,
            NoStore = true
        };
        context.Response.ContentLength = grant.Target.SizeBytes;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Remove(HeaderNames.AcceptRanges);
            context.Response.Headers.Remove(HeaderNames.ContentRange);
            return Task.CompletedTask;
        });

        return Results.File(stream, "application/octet-stream", fileDownloadName: SafeDownloadFilename(grant.Target.Filename));
    }

    private static string SafeDownloadFilename(string? filename) => string.IsNullOrWhiteSpace(filename)
        ? "romd-content.bin"
        : string.Concat(filename.Select(c => char.IsControl(c) || "/\\:*?\"<>|".Contains(c) ? '_' : c));

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
            return new[] { ConsumerErrors.ContentNotFound() }.ToProblem();
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
