using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;

namespace Romd.Host.Authorization;

/// <summary>Explicit opt-in for a consumer route exposed to third-party OAuth grants.</summary>
public sealed record ThirdPartyConsumerScope(string Name);

internal sealed class ThirdPartyConsumerAccess : IAuthorizationRequirement;

internal sealed class ThirdPartyConsumerAccessHandler : AuthorizationHandler<ThirdPartyConsumerAccess>
{
    // This marker is attached during validation from the persisted session's client identity.
    public const string ScopedGrantClaim = "romd_scoped_consumer_session";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ThirdPartyConsumerAccess requirement)
    {
        var endpoint = (context.Resource as HttpContext)?.GetEndpoint();
        if (!context.User.HasClaim(ScopedGrantClaim, "true") ||
            endpoint?.Metadata.GetMetadata<ThirdPartyConsumerScope>() is { } scope &&
            context.User.HasScope(scope.Name))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
