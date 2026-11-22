using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;

namespace Romd.Host.Authorization;

/// <summary>Explicit opt-in for a consumer route exposed to third-party OAuth grants.</summary>
public sealed record ThirdPartyConsumerScope(string Name);

internal sealed class ThirdPartyConsumerAccess : IAuthorizationRequirement;

internal sealed class BrowserAccountOnly;

internal sealed class ThirdPartyConsumerAccessHandler : AuthorizationHandler<ThirdPartyConsumerAccess>
{
    // This marker is attached during validation from the persisted session's client identity.
    public const string DeviceClaim = "romd_device_session";
    public const string ThirdPartyClaim = "romd_third_party_session";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ThirdPartyConsumerAccess requirement)
    {
        var endpoint = (context.Resource as HttpContext)?.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<BrowserAccountOnly>() is not null && context.User.HasClaim(DeviceClaim, "true"))
            return Task.CompletedTask;

        if (!context.User.HasClaim(ThirdPartyClaim, "true") ||
            endpoint?.Metadata.GetMetadata<ThirdPartyConsumerScope>() is { } scope &&
            context.User.HasScope(scope.Name))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
