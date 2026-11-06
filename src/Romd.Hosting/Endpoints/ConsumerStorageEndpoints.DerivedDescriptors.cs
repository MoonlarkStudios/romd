using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Delivery;

namespace Romd.Host.Endpoints;

public static partial class ConsumerStorageEndpoints
{
    private static async Task<IResult> RedeemDerivedDescriptorGrant(string token, HttpContext context,
        IConsumerDerivedDescriptorGrantIssuer issuer, IConsumerDerivedDescriptorSource retained,
        IConsumerContentArtifactResolver artifacts, CancellationToken ct)
    {
        var validated = issuer.Validate(token);
        if (validated.IsError)
            return validated.Errors.ToProblem();
        var captured = validated.Value.Grant.Source;
        if (!await retained.IsRetainedAsync(captured, ct))
            return new[] { ConsumerErrors.ContentNotFound() }.ToProblem();
        await using var source = await artifacts.ResolveAsync(captured.Identity.Sha256, ct);
        if (source is null)
            return new[] { ConsumerErrors.ContentNotFound() }.ToProblem();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > captured.Identity.Size)
                return new[] { ConsumerErrors.ContentNotFound() }.ToProblem();
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }
        byte[] bytes = buffer.ToArray();
        if (bytes.LongLength != captured.Identity.Size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != captured.Identity.Sha256.ToString())
            return new[] { ConsumerErrors.ContentNotFound() }.ToProblem();
        context.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue { Private = true, NoStore = true };
        return Results.File(bytes, "application/octet-stream", SafeDownloadFilename(Path.GetFileName(captured.RelativePath)));
    }
}
