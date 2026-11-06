using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Net.Http.Headers;
using Romd.Consumer.Application.Vfs;

namespace Romd.Hosting.Vfs;

/// <summary>
/// Evaluates HTTP preconditions and ranges against authorized metadata before opening content.
/// Uses the framework's file response semantics for both metadata and body requests.
/// </summary>
internal sealed class VfsFileResponse : FileResultExecutorBase
{
    public VfsFileResponse(ILogger<VfsFileResponse> logger) : base(logger) { }

    public async Task ExecuteAsync(HttpContext context, ConsumerVfsFile file,
        Func<CancellationToken, Task<Stream?>> open)
    {
        // This result supplies headers only; its placeholder stream is never executed.
        var metadata = new FileStreamResult(Stream.Null, "application/octet-stream");
        var action = new ActionContext(context, context.GetRouteData(), new ActionDescriptor());
        // RFC 9110 section 14.2: Range applies only to GET. Advertise support on HEAD,
        // but retain the complete representation length even if the client sends Range.
        if (HttpMethods.IsHead(context.Request.Method))
            context.Response.Headers.AcceptRanges = "bytes";
        var (range, rangeLength, serveBody) = SetHeadersAndLog(action, metadata, file.Length,
            enableRangeProcessing: HttpMethods.IsGet(context.Request.Method), lastModified: null,
            etag: new EntityTagHeaderValue("\"" + file.Sha256 + "\""));
        if (!serveBody)
            return;

        try
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            await using Stream? stream = await open(context.RequestAborted);
            if (stream is null)
            {
                ClearFileHeaders(context.Response);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (!stream.CanSeek || stream.Length != file.Length)
                throw new InvalidDataException("VFS content disagrees with authorized file metadata.");
            await WriteFileAsync(context, stream, range, rangeLength);
        }
        catch (Romd.Application.Common.Content.ResourceCapacityException) when (!context.Response.HasStarted)
        {
            ClearFileHeaders(context.Response);
            context.Response.Headers.RetryAfter = "15";
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
        catch when (!context.Response.HasStarted)
        {
            // Header calculation precedes opening storage. Do not leave a successful file's
            // length/range/validators on an error response from a missing or corrupt artifact.
            ClearFileHeaders(context.Response);
            throw;
        }
    }

    private static void ClearFileHeaders(HttpResponse response)
    {
        response.ContentLength = null;
        response.ContentType = null;
        response.Headers.Remove(HeaderNames.ContentRange);
        response.Headers.Remove(HeaderNames.AcceptRanges);
        response.Headers.Remove(HeaderNames.ETag);
        response.Headers.Remove(HeaderNames.LastModified);
    }
}
