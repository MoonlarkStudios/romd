using System.Collections.Immutable;
using System.Security.Claims;
using OpenIddict.Abstractions;
using Romd.Application.Common.Security;

namespace Romd.Infrastructure.Identity;

public static class DeviceGrantPermissions
{
    public const string ConsentClaim = "romd_device_grant";

    public static ImmutableArray<string> ForPairing(string? clientId, ImmutableArray<string> scopes)
    {
        // Shipped clients request OIDC scopes only. Show their compatibility permissions on
        // the approval page and store them in the grant just like explicitly requested scopes.
        if (scopes.Any(scope => ConsumerGrantScopes.ThirdParty.Contains(scope) || scope is ConsumerGrantScopes.PlaySessionSync or ConsumerGrantScopes.VfsRead))
            return scopes;

        return clientId switch
        {
            RomdOpenIddictClients.Console => scopes.AddRange(ConsumerGrantScopes.ThirdParty)
                .Add(ConsumerGrantScopes.PlaySessionSync).Add(ConsumerGrantScopes.VfsRead),
            RomdOpenIddictClients.Mount => scopes.Add(ConsumerGrantScopes.Discovery).Add(ConsumerGrantScopes.VfsRead),
            _ => scopes
        };
    }

    // Sessions issued before explicit permissions existed retain only the bounded compatibility grant.
    // The marker keeps a later downscoped token from acquiring those permissions again.
    public static ImmutableArray<string> RestoreApprovedScopes(string? clientId, ClaimsPrincipal principal) =>
        principal.HasClaim(ConsentClaim) ? principal.GetScopes() : ForPairing(clientId, principal.GetScopes());
}
