using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Romd.Hosting.IntegrationTests.Vfs;

/// <summary>
/// Test-only loopback credential adapter. Changes only the request Authorization
/// header; ROMD still validates the issued token and the user's current library.
/// Never registered by a production host.
/// </summary>
internal sealed class NativeWebDavAuthenticationProbe : IStartupFilter
{
    public string Password { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public string? BearerToken { get; set; }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(InvokeAsync);
        next(app);
    };

    internal async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/vfs"))
        {
            await next(context);
            return;
        }
        string authorization = context.Request.Headers.Authorization.ToString();
        // Existing real-client probes still exercise normal bearer authentication.
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }
        string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("davprobe:" + Password));
        if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
            BearerToken is not { } token || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(authorization), Encoding.UTF8.GetBytes(expected)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"ROMD disposable native probe\"";
            return;
        }
        context.Request.Headers.Authorization = "Bearer " + token;
        await next(context);
    }
}
