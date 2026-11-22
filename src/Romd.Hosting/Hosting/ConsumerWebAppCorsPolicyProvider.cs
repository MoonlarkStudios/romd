using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using Romd.Application.Common.Security;
using Romd.Host.Authorization;

namespace Romd.Hosting;

/// <summary>Extends the consumer CORS policy using live, worker-provisioned registrations.</summary>
public sealed class ConsumerWebAppCorsPolicyProvider(IOptions<CorsOptions> options) : ICorsPolicyProvider
{
    private readonly DefaultCorsPolicyProvider _baseline = new(options);

    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var baseline = await _baseline.GetPolicyAsync(context, policyName);
        if (policyName != RomdCorsPolicyNames.Consumer ||
            context.Request.Headers.Origin is not { Count: 1 } origins ||
            origins[0] is not { } origin || !origin.StartsWith("https://", StringComparison.Ordinal) ||
            baseline?.IsOriginAllowed(origin) == true)
        {
            return baseline;
        }

        var path = context.Request.Path;
        bool supported = context.GetEndpoint()?.Metadata.GetMetadata<ThirdPartyConsumerScope>() is not null
            || path.StartsWithSegments("/delivery") || path.StartsWithSegments("/artwork") || path.StartsWithSegments("/media")
            || path.StartsWithSegments("/.well-known") || path == "/connect/token" || path == "/connect/revocation";
        // A preflight may have no matching endpoint because its HTTP method is OPTIONS.
        supported |= HttpMethods.IsOptions(context.Request.Method) && path.StartsWithSegments("/api");
        if (!supported)
            return baseline;

        var registrations = context.RequestServices.GetRequiredService<IConsumerWebAppRegistrations>();
        if (!await registrations.IsActiveOriginAsync(origin, context.RequestAborted))
            return baseline;

        return new CorsPolicyBuilder(origin)
            .SetIsOriginAllowed(candidate => string.Equals(candidate, origin, StringComparison.Ordinal))
            .AllowAnyMethod().AllowAnyHeader()
            .WithExposedHeaders("ETag", "Location").Build();
    }
}
