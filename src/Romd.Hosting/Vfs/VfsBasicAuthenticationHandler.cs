using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Romd.Consumer.Application.Vfs;

namespace Romd.Hosting.Vfs;

public sealed class VfsBasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public VfsBasicAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    public const string SchemeName = "VfsBasic";
    public const string PolicyName = "VfsRead";
    public const string FormatClaim = "romd_vfs_format";
    public const string ConnectionClaim = "romd_vfs_connection_id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => AuthenticateRequestAsync(Context);

    public static async Task<AuthenticateResult> AuthenticateRequestAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/vfs") || !context.RequestServices.GetRequiredService<ConsumerVfsOptions>().Enabled)
            return AuthenticateResult.NoResult();
        string header = context.Request.Headers.Authorization.ToString();
        if (header.Length > 512 || !AuthenticationHeaderValue.TryParse(header, out var value) ||
            !value.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        if (!context.Request.IsHttps)
            return AuthenticateResult.Fail("VFS credentials require HTTPS.");
        string decoded;
        try
        { decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value.Parameter ?? "")); }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        { return AuthenticateResult.Fail("Invalid VFS credentials."); }
        int separator = decoded.IndexOf(':');
        if (separator < 1)
            return AuthenticateResult.Fail("Invalid VFS credentials.");
        string username = decoded[..separator];
        var connections = context.RequestServices.GetRequiredService<IVfsConnections>();
        var authenticated = await connections.AuthenticateAsync(username, decoded[(separator + 1)..], context.RequestAborted);
        if (authenticated is null || !VfsFormats.IsValid(authenticated.Format))
            return AuthenticateResult.Fail("Invalid VFS credentials.");
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, authenticated.UserId.ToString()),
            new Claim(ConnectionClaim, authenticated.ConnectionId.ToString()),
            new Claim(FormatClaim, VfsFormats.ToWire(authenticated.Format))], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append("WWW-Authenticate", "Basic realm=\"ROMD VFS\", charset=\"UTF-8\"");
        Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    }
}
