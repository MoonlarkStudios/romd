using Microsoft.AspNetCore;
using OpenIddict.Server;
using Romd.Application.Common.Security;
using Romd.Hosting;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Romd.Host.Authorization;

/// <summary>Applies the host and live registration boundary before protocol credentials are consumed.</summary>
public sealed class ValidateOAuthClient(RomdSurfaceAuthOptions surface, IConsumerWebAppRegistrations registrations)
    : IOpenIddictServerHandler<ValidateAuthorizationRequestContext>,
        IOpenIddictServerHandler<ValidateTokenRequestContext>,
        IOpenIddictServerHandler<ValidateDeviceAuthorizationRequestContext>,
        IOpenIddictServerHandler<ValidateRevocationRequestContext>
{
    public async ValueTask HandleAsync(ValidateAuthorizationRequestContext context)
    {
        string? clientId = context.Request.ClientId;
        // The existing authorize endpoint rejects built-in clients on the wrong host
        // after OpenIddict has validated the callback. Preserve that protocol response.
        if (!ConsumerWebAppRegistration.IsClientId(clientId))
            return;

        var registration = await surface.FindActiveWebAppAsync(clientId, registrations, context.CancellationToken);
        if (registration is null)
            context.Reject(Errors.UnauthorizedClient, "This client is not allowed on this host.");
        else if (!string.Equals(registration.CallbackUri, context.Request.RedirectUri, StringComparison.Ordinal))
            context.Reject(Errors.InvalidRequest, "The callback must match the registered URL.");
        else if (context.Request.CodeChallengeMethod != CodeChallengeMethods.Sha256)
            context.Reject(Errors.InvalidRequest, "This client requires S256 PKCE.");
    }

    public async ValueTask HandleAsync(ValidateTokenRequestContext context)
    {
        if (!await IsAllowedBackchannelAsync(context.Request.ClientId, context.Transaction, context.CancellationToken))
            context.Reject(Errors.InvalidGrant, "The client or browser origin is not allowed.");
    }

    public ValueTask HandleAsync(ValidateDeviceAuthorizationRequestContext context)
    {
        if (!surface.IsClientAllowed(context.Request.ClientId))
            context.Reject(Errors.UnauthorizedClient, "This client is not allowed to pair on this host.");
        return ValueTask.CompletedTask;
    }

    public async ValueTask HandleAsync(ValidateRevocationRequestContext context)
    {
        if (!await IsAllowedBackchannelAsync(context.Request.ClientId, context.Transaction, context.CancellationToken))
            context.Reject(Errors.UnauthorizedClient, "The client or browser origin is not allowed.");
    }

    private async ValueTask<bool> IsAllowedBackchannelAsync(string? clientId, OpenIddictServerTransaction transaction, CancellationToken ct)
    {
        if (surface.IsClientAllowed(clientId))
            return true;

        var app = await surface.FindActiveWebAppAsync(clientId, registrations, ct);
        return app is not null && MatchesOrigin(transaction.GetHttpRequest(), app.BrowserOrigin);
    }

    internal static bool MatchesOrigin(HttpRequest? request, string browserOrigin) =>
        request?.Headers.Origin is { Count: 1 } origins &&
        string.Equals(origins[0], browserOrigin, StringComparison.Ordinal);
}
