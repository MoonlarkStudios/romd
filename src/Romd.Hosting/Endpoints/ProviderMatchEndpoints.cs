using Romd.Admin.Application.MetadataProviders;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ProviderMatchEndpoints
{
    public static IEndpointRouteBuilder MapProviderMatchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/provider-matches").WithTags("ProviderMatches")
            .RequireAuthorization(AuthorizationPolicies.RequireManager);
        group.MapGet("/titles/{titleId}", async (Sqid titleId, ITitleProviderMatchService service, CancellationToken ct) =>
            (await service.GetAsync(titleId.Value, ct)).Match(Results.Ok, errors => errors.ToProblem()))
            .WithName("GetTitleProviderMatches").Produces<IReadOnlyList<TitleProviderMatchDto>>();
        group.MapPost("/{providerId}/search", async (string providerId, ProviderMatchSearchRequest request,
            ITitleProviderMatchService service, CancellationToken ct) =>
            (await service.SearchAsync(providerId, request, ct)).Match(Results.Ok, errors => errors.ToProblem()))
            .WithName("SearchProviderMatches").Produces<IReadOnlyList<ProviderGameDto>>();
        group.MapPut("/titles/{titleId}/{providerId}", async (Sqid titleId, string providerId,
            SetProviderMatchRequest request, ITitleProviderMatchService service, CancellationToken ct) =>
            (await service.SetAsync(titleId.Value, providerId, request, ct)).Match(_ => Results.NoContent(), errors => errors.ToProblem()))
            .WithName("SetTitleProviderMatch").Produces(204);
        group.MapPost("/titles/{titleId}/{providerId}/confirm", async (Sqid titleId, string providerId,
            ChangeProviderMatchRequest request, ITitleProviderMatchService service, CancellationToken ct) =>
            (await service.ConfirmAsync(titleId.Value, providerId, request.ExpectedRevision, ct)).Match(_ => Results.NoContent(), errors => errors.ToProblem()))
            .WithName("ConfirmTitleProviderMatch").Produces(204);
        group.MapPost("/titles/{titleId}/{providerId}/unlink", async (Sqid titleId, string providerId,
            ChangeProviderMatchRequest request, ITitleProviderMatchService service, CancellationToken ct) =>
            (await service.UnlinkAsync(titleId.Value, providerId, request.ExpectedRevision, ct)).Match(_ => Results.NoContent(), errors => errors.ToProblem()))
            .WithName("UnlinkTitleProviderMatch").Produces(204);
        return app;
    }
}
