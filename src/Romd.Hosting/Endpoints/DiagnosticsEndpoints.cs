using Romd.Admin.Application.Diagnostics.Queries.GetOperationalDiagnostics;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Diagnostics;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class DiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/diagnostics")
            .WithTags("Diagnostics");

        group.MapGet("operational", GetOperationalDiagnostics)
            .WithName("GetOperationalDiagnostics")
            .WithDescription("Get bounded operational diagnostics for catalog, jobs, queues, and storage")
            .Produces<OperationalDiagnosticsDto>()
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        return app;
    }

    private static async Task<IResult> GetOperationalDiagnostics(
        IQueryHandler<GetOperationalDiagnosticsQuery, OperationalDiagnosticsDto> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetOperationalDiagnosticsQuery(), ct);
        return result.Match(
            diagnostics => Results.Ok(diagnostics),
            errors => errors.ToProblem());
    }
}
