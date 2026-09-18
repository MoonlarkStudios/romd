using Romd.Application.Common.Readiness;
using Romd.Host.Hubs;

namespace Romd.Hosting;

public static class RomdEndpointRouteExtensions
{
    public static IEndpointRouteBuilder MapRomdRealtimeHubs(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<JobHub>("/hubs/jobs");
        endpoints.MapHub<SystemHub>("/hubs/system");

        return endpoints;
    }

    public static IEndpointRouteBuilder MapRomdHealthCheck(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", (TimeProvider timeProvider) =>
                Results.Ok(new { Status = "Healthy", Timestamp = timeProvider.GetUtcNow().UtcDateTime }))
            .WithName("HealthCheck")
            .WithTags("Health")
            // Intentionally public: liveness probes must reach this without credentials. The
            // authorization-inventory test lists it as an explicit anonymous decision.
            .AllowAnonymous();

        return endpoints;
    }

    public static IEndpointRouteBuilder MapRomdReadinessCheck(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/ready", async (IReadinessEvaluator evaluator, CancellationToken cancellationToken) =>
            {
                var report = await evaluator.EvaluateAsync(cancellationToken);

                // Minimal, opaque body on purpose: overall status plus per-check name/status. No
                // exception text, durations, or internals — details belong to the authenticated
                // diagnostics surface.
                return Results.Json(
                    new
                    {
                        status = ReadinessStatusNames.Of(report.Status),
                        checks = report.Checks
                            .Select(check => new
                            {
                                name = check.Name,
                                status = ReadinessStatusNames.Of(check.Status)
                            })
                            .ToArray()
                    },
                    statusCode: report.Status == ReadinessStatus.Unready
                        ? StatusCodes.Status503ServiceUnavailable
                        : StatusCodes.Status200OK);
            })
            .WithName("ReadinessCheck")
            .WithTags("Health")
            // Intentionally public: readiness probes must reach this without credentials. The
            // authorization-inventory test lists it as an explicit anonymous decision.
            .AllowAnonymous()
            // Operational-only endpoint: excluded from every OpenAPI document so the generated
            // clients and committed schemas are unaffected.
            .ExcludeFromDescription();

        return endpoints;
    }
}
