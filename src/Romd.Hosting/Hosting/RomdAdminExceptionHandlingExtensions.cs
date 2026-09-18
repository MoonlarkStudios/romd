using Microsoft.AspNetCore.Diagnostics;
using Romd.Admin.Application.Common.Persistence;

namespace Romd.Hosting;

public static class RomdAdminExceptionHandlingExtensions
{
    /// <summary>
    ///     Converts unhandled exceptions on the admin surface into sanitized ProblemDetails
    ///     envelopes written by the ProblemDetails service registered in AddRomdAdminHttp.
    ///     <see cref="BadHttpRequestException" /> keeps its own status code (endpoint helpers such
    ///     as LibraryEndpoints.DecodeIds throw it for malformed ids to signal 400). Stale title
    ///     writes return 409; other exceptions return 500 without exception details.
    /// </summary>
    public static IApplicationBuilder UseRomdAdminExceptionHandling(this IApplicationBuilder app) =>
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            StatusCodeSelector = static exception => exception switch
            {
                BadHttpRequestException badRequest => badRequest.StatusCode,
                PersistenceConflictException => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status500InternalServerError
            }
        });
}
