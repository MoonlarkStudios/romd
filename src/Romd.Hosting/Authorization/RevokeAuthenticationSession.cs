using OpenIddict.Abstractions;
using OpenIddict.Server;
using Romd.Application.Common.Security;

namespace Romd.Host.Authorization;

/// <summary>Protocol revocation ends the sign-in, not just one rotating token.</summary>
public sealed class RevokeAuthenticationSession(IAccountSessions sessions)
    : IOpenIddictServerHandler<OpenIddictServerEvents.HandleRevocationRequestContext>
{
    public async ValueTask HandleAsync(OpenIddictServerEvents.HandleRevocationRequestContext context)
    {
        var principal = context.GenericTokenPrincipal;
        if (Guid.TryParse(principal?.GetClaim(OpenIddictConstants.Claims.Subject), out var userId)
            && Guid.TryParse(principal?.GetClaim(SessionClaims.Id), out var sessionId))
            await sessions.RevokeAsync(userId, sessionId, null, userId, "Signed out", context.CancellationToken);
    }
}
