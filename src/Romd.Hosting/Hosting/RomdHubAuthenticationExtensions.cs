using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Romd.Hosting;

public static class RomdHubAuthenticationExtensions
{
    /// <summary>
    ///     SignalR WebSocket connections cannot send an Authorization header, so the client sends the
    ///     access token as a <c>?access_token=</c> query parameter. For <c>/hubs</c> paths only, promote
    ///     that token into the Authorization header before authentication runs. This feeds both the
    ///     JwtBearer and the OpenIddict validation handlers from the header (OpenIddict access tokens are
    ///     JWE and cannot be read by JwtBearer), while global query-string token extraction stays disabled
    ///     so a query token is never honored on any other endpoint.
    /// </summary>
    public static IApplicationBuilder UseRomdHubQueryStringToken(this IApplicationBuilder app) =>
        app.Use(static (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/hubs")
                && !context.Request.Headers.ContainsKey("Authorization")
                && context.Request.Query.TryGetValue("access_token", out var token)
                && !string.IsNullOrEmpty(token))
            {
                context.Request.Headers.Authorization = $"Bearer {token}";
            }

            return next();
        });
}
