using ErrorOr;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;
using Romd.Contracts.Management.Models;

namespace Romd.Host.Endpoints;

/// <summary>The shared runtime result and declaration for accepted background work.</summary>
public static class JobAccepted
{
    public static string Location(Guid jobId) => $"/api/jobs/{jobId}";

    public static IResult Result(Guid jobId) => Results.Accepted(Location(jobId), new JobReference(jobId));

    public static IResult ToAccepted(this ErrorOr<Guid> result) => result.Match(Result, errors => errors.ToProblem());

    /// <summary>Declares a job reference and required Location for 202; acceptance carries no ETag.</summary>
    public static RouteHandlerBuilder ProducesAcceptedJob(this RouteHandlerBuilder builder) =>
        builder.Produces<JobReference>(StatusCodes.Status202Accepted)
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                if (operation.Responses?["202"] is OpenApiResponse response)
                {
                    response.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
                    response.Headers[HeaderNames.Location] = new OpenApiHeader
                    {
                        Required = true,
                        Description = "The accepted job's resource, /api/jobs/{jobId}.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    };
                }
                return Task.CompletedTask;
            });
}
