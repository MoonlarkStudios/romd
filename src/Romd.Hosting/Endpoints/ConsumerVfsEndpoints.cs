using System.Diagnostics;
using System.Security.Claims;
using System.Xml;
using ErrorOr;
using Romd.Domain.Vfs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.Vfs;
using Romd.Host.Authorization;
using Romd.Hosting.Vfs;

namespace Romd.Host.Endpoints;

public static class ConsumerVfsEndpoints
{
    private const string Allow = "OPTIONS, PROPFIND, GET, HEAD";

    public static IEndpointRouteBuilder MapConsumerVfsEndpoints(this IEndpointRouteBuilder app)
    {
        // Validate keys at startup while keeping layouts/repositories scoped per request.
        // Layouts need the application database, so only an enabled VFS resolves them.
        if (app.ServiceProvider.GetRequiredService<ConsumerVfsOptions>().Enabled)
        {
            using var scope = app.ServiceProvider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<VfsLayoutRegistry>();
        }

        app.Map("/vfs/{**path}", HandleAsync)
            .RequireAuthorization(VfsBasicAuthenticationHandler.PolicyName)
            .ExcludeFromDescription();
        return app;
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<ConsumerVfsOptions>();
        if (!options.Enabled)
        { context.Response.StatusCode = 404; return; }
        var user = context.RequestServices.GetRequiredService<ICurrentUser>();
        if (!user.IsAuthenticated || user.UserId is not { } userId)
        {
            context.Response.StatusCode = 401;
            return;
        }
        var admission = context.RequestServices.GetRequiredService<ConsumerVfsAdmission>();
        string? connectionClaim = context.User.FindFirstValue(VfsBasicAuthenticationHandler.ConnectionClaim);
        Guid? connectionId = Guid.TryParse(connectionClaim, out var parsedConnection) ? parsedConnection : null;
        context.Response.Headers.CacheControl = "private, no-store";
        if (connectionId is null && !options.AllowDevelopmentBearer)
        {
            context.Response.StatusCode = 401;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"ROMD VFS\", charset=\"UTF-8\"";
            return;
        }
        using var lease = context.Request.Method is "PROPFIND" or "HEAD" or "OPTIONS"
            ? await context.RequestServices.GetRequiredService<ConsumerVfsMetadataAdmission>()
                .EnterAsync(userId, connectionId, context.RequestAborted)
            : await admission.EnterAsync(userId, connectionId, context.RequestAborted);
        if (lease is null)
        {
            context.Response.Headers.RetryAfter = "1";
            context.Response.StatusCode = 503;
            return;
        }

        Stream originalBody = context.Response.Body;
        using var counter = new VfsResponseCounter(originalBody);
        context.Response.Body = counter;
        long started = Stopwatch.GetTimestamp();
        bool failed = false;
        ConsumerVfsDiagnostics.ActiveRequests.Add(1);
        try
        {
            context.Response.Headers.CacheControl = "private, no-store";
            context.RequestAborted.ThrowIfCancellationRequested();
            // Authentication middleware caches its result. Explicitly verify the presented secret
            // again after admission so rotation/revocation during a wait cannot grant access.
            VfsFormat format = VfsFormat.Stored;
            if (connectionClaim is not null)
            {
                var current = await VfsBasicAuthenticationHandler.AuthenticateRequestAsync(context);
                if (!current.Succeeded || current.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) != userId.ToString() ||
                    !Guid.TryParse(current.Principal?.FindFirstValue(VfsBasicAuthenticationHandler.ConnectionClaim), out var refreshedConnection) ||
                    refreshedConnection != connectionId)
                {
                    ConsumerVfsDiagnostics.Failures.Add(1, new KeyValuePair<string, object?>("reason", "credential_invalidated"));
                    context.Response.StatusCode = 401;
                    context.Response.Headers.WWWAuthenticate = "Basic realm=\"ROMD VFS\", charset=\"UTF-8\"";
                    return;
                }
                var selection = VfsFormats.Parse(current.Principal?.FindFirstValue(VfsBasicAuthenticationHandler.FormatClaim));
                if (selection.IsError)
                { context.Response.StatusCode = 401; return; }
                format = selection.Value;
            }
            // Layout resolution and content opening query current library authorization here.
            await ServeAsync(context, options, userId, format);
        }
        catch (InvalidDataException error) when (!context.Response.HasStarted)
        {
            ConsumerVfsDiagnostics.Failures.Add(1, new KeyValuePair<string, object?>("reason", "content_integrity"));
            context.RequestServices.GetRequiredService<ILogger<ConsumerVfsService>>()
                .LogError(error, "VFS content integrity failure for connection {ConnectionId}", connectionId);
            context.Response.StatusCode = 500;
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            context.Response.Body = originalBody;
            ConsumerVfsDiagnostics.ActiveRequests.Add(-1);
            ConsumerVfsDiagnostics.ResponseBytes.Add(counter.BytesWritten,
                new KeyValuePair<string, object?>("method", context.Request.Method is "GET" or "PROPFIND" ? context.Request.Method : "OTHER"));
            // Connection and range identifiers belong in request logs, not unbounded metric labels.
            context.RequestServices.GetRequiredService<ILogger<ConsumerVfsService>>().LogDebug(
                "VFS transfer connection {ConnectionId}, validator {Validator}, range {ContentRange}, body bytes {Bytes}, status {Status}",
                connectionId, context.Response.Headers.ETag.ToString(), context.Response.Headers.ContentRange.ToString(),
                counter.BytesWritten, context.RequestAborted.IsCancellationRequested ? 499 : failed ? 500 : context.Response.StatusCode);
            string method = context.Request.Method is "GET" or "HEAD" or "PROPFIND" or "OPTIONS"
                ? context.Request.Method : "OTHER";
            ConsumerVfsDiagnostics.Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("method", method),
                new KeyValuePair<string, object?>("status", context.RequestAborted.IsCancellationRequested ? 499 :
                    failed ? 500 : context.Response.StatusCode));
        }
    }

    private static async Task ServeAsync(HttpContext context, ConsumerVfsOptions options, Guid userId, VfsFormat format)
    {
        CancellationToken ct = context.RequestAborted;
        string method = context.Request.Method;
        if (method is not ("OPTIONS" or "PROPFIND" or "GET" or "HEAD"))
        {
            context.Response.Headers.Allow = Allow;
            context.Response.StatusCode = 405;
            return;
        }

        // ASP.NET route values are decoded once. Never URL-decode this value again.
        string path = (context.Request.RouteValues["path"] as string ?? "").TrimEnd('/');
        if (path.Length > 8192)
        { context.Response.StatusCode = 414; return; }
        WebDavProperties? properties = null;
        bool children = false;
        if (method == "PROPFIND")
        {
            string depth = context.Request.Headers["Depth"].ToString();
            if (depth is "" or "infinity")
            {
                context.Response.StatusCode = 403;
                context.Response.ContentType = "application/xml";
                await context.Response.WriteAsync("<D:error xmlns:D=\"DAV:\"><D:propfind-finite-depth/></D:error>", ct);
                return;
            }

            if (depth is not ("0" or "1"))
            { context.Response.StatusCode = 400; return; }
            children = depth == "1";
            try
            { properties = await WebDavProperties.ReadAsync(context.Request.Body, ct); }
            catch (InvalidDataException) { context.Response.StatusCode = 413; return; }
            catch (XmlException) { context.Response.StatusCode = 400; return; }
        }

        var service = context.RequestServices.GetRequiredService<ConsumerVfsService>();
        string prefix = context.Request.PathBase + "/vfs/";
        int separator = path.IndexOf('/');
        string key = separator < 0 ? path : path[..separator];
        prefix += key + "/";
        path = separator < 0 ? "" : path[(separator + 1)..];

        var read = await service.ReadAsync(new ConsumerLibraryScope(userId), key, path, children,
            options.MaximumDirectoryEntries, ct, format);
        if (read.IsError)
        {
            ConsumerVfsDiagnostics.Failures.Add(1, new KeyValuePair<string, object?>("reason", read.FirstError.Code));
            context.Response.StatusCode = ReadStatus(read.FirstError);
            if (context.Response.StatusCode == 503)
                context.Response.Headers.RetryAfter = "1";
            return;
        }

        var resources = new List<WebDavResource> { Resource(prefix, read.Value.Listing.Entry) };
        resources.AddRange(read.Value.Listing.Children.Select(entry => Resource(prefix, entry)));
        ConsumerVfsFile? file = read.Value.Listing.Entry.File;

        if (method == "OPTIONS")
        {
            context.Response.Headers.Allow = Allow;
            context.Response.Headers["DAV"] = "1";
            context.Response.StatusCode = 204;
        }
        else if (properties is not null)
        {
            await properties.WriteAsync(context.Response, Enumerate(resources), ct);
        }
        else if (file is null)
        {
            context.Response.Headers.Allow = "OPTIONS, PROPFIND";
            context.Response.StatusCode = 405;
        }
        else if (read.Value.Content is not { } content)
        {
            // Layout-supplied file metadata is never an access grant.
            context.Response.StatusCode = 404;
        }
        else
        {
            var response = new VfsFileResponse(context.RequestServices.GetRequiredService<ILogger<VfsFileResponse>>());
            await response.ExecuteAsync(context, content, token => service.OpenAsync(read.Value, token));
        }
    }

    private static int ReadStatus(Error error) => error.Type switch
    {
        ErrorType.NotFound => 404,
        _ when error.Code == ConsumerVfsErrors.InconsistentProjection().Code => 503,
        ErrorType.Conflict => 409,
        _ when error.Code == ConsumerVfsErrors.EnumerationLimit().Code => 507,
        _ => 500
    };

    private static string ETag(ConsumerVfsFile file) => "\"" + file.Sha256 + "\"";

    private static WebDavResource Resource(string prefix, VfsLayoutEntry entry) =>
        new(prefix + string.Join('/', entry.Path.Split('/').Select(Uri.EscapeDataString)) +
            (entry.IsDirectory && entry.Path.Length != 0 ? "/" : ""),
            entry.Name, entry.File?.Length,
            entry.File is { } file ? ETag(file) : null, entry.File?.ModifiedAt,
            entry.File is { Md5.IsEmpty: false } md5 ? md5.Md5.ToString() : null,
            entry.File is { Sha1.IsEmpty: false } sha1 ? sha1.Sha1.ToString() : null);

    private static async IAsyncEnumerable<WebDavResource> Enumerate(IEnumerable<WebDavResource> resources)
    {
        foreach (var resource in resources)
            yield return resource;
        await Task.CompletedTask;
    }
}
