using Microsoft.AspNetCore.Http.Features;

namespace Romd.Hosting.Security;

public static class InteractiveAuthenticationForms
{
    // Leave room for the allowed password after UTF-8 percent encoding, plus the
    // login, antiforgery token and protocol return URL. Character and body limits
    // must not make an accepted password impossible to submit.
    public const long MaxBodyBytes = 64 * 1024;

    /// <summary>Bound and parse browser forms before authentication/protocol handlers read them.</summary>
    public static WebApplication UseRomdInteractiveAuthenticationForms(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            string path = context.Request.Path.Value?.TrimEnd('/') ?? string.Empty;
            if (!HttpMethods.IsPost(context.Request.Method)
                || !(path.Equals("/connect/login", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("/connect/verify", StringComparison.OrdinalIgnoreCase)))
            {
                await next(context);
                return;
            }

            if (context.Request.ContentLength > MaxBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                size.MaxRequestBodySize = MaxBodyBytes;
            if (!context.Request.HasFormContentType)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            try
            {
                await context.Request.ReadFormAsync(context.RequestAborted);
            }
            catch (InvalidDataException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            catch (BadHttpRequestException error)
            {
                context.Response.StatusCode = error.StatusCode;
                return;
            }
            await next(context);
        });
        return app;
    }
}
