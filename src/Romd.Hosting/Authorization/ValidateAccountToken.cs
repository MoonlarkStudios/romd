using OpenIddict.Abstractions;
using OpenIddict.Validation;
using Romd.Application.Common.Security;

namespace Romd.Host.Authorization;

/// <summary>Rejects stale identity snapshots, including tokens issued concurrently with account revocation.</summary>
public sealed class ValidateAccountToken(IAccountSessions sessions)
    : IOpenIddictValidationHandler<OpenIddictValidationEvents.ValidateTokenContext>
{
    public async ValueTask HandleAsync(OpenIddictValidationEvents.ValidateTokenContext context)
    {
        var principal = context.Principal;
        if (principal is null) return;
        if (!Guid.TryParse(principal.GetClaim(OpenIddictConstants.Claims.Subject), out var id))
        {
            context.Reject(OpenIddictConstants.Errors.InvalidToken);
            return;
        }
        if (!Guid.TryParse(principal.GetClaim(SessionClaims.Id), out var sessionId)
            || await sessions.ValidateAsync(id, sessionId, principal.GetClaim(SessionClaims.AccountStamp) ?? "", context.CancellationToken) is null)
            context.Reject(OpenIddictConstants.Errors.InvalidToken, "Sign in again to continue.");
    }
}
