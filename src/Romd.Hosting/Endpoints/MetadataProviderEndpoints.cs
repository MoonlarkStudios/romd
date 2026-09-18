using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.MetadataProviders;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class MetadataProviderEndpoints
{
    public static IEndpointRouteBuilder MapMetadataProviderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/metadata-providers/igdb")
            .WithTags("MetadataProviders")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("", async (IIgdbProviderSettingsService service, CancellationToken ct) =>
                Results.Ok(await service.GetAsync(ct)))
            .WithName("GetIgdbProviderSettings")
            .Produces<IgdbProviderSettingsDto>();

        group.MapPut("", Update)
            .WithName("UpdateIgdbProviderSettings")
            .Produces<IgdbProviderSettingsDto>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);

        group.MapPost("/test-connection", async (IIgdbProviderSettingsService service, CancellationToken ct) =>
                Results.Ok(await service.TestConnectionAsync(ct)))
            .WithName("TestIgdbProviderConnection")
            .Produces<IgdbProviderSettingsDto>();
        return app;
    }

    private static async Task<IResult> Update(UpdateIgdbProviderSettingsRequest request,
        IIgdbProviderSettingsService service, CancellationToken ct)
    {
        var result = await service.UpdateAsync(request, ct);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }
}
