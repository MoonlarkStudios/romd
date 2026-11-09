using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.MetadataProviders.Commands.UpdateIgdbSettings;
using Romd.Admin.Application.MetadataProviders.Commands.TestIgdbConnection;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class MetadataProviderEndpoints
{
    public static IEndpointRouteBuilder MapMetadataProviderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/metadata-providers/igdb/settings").WithTags("MetadataProviders")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
        group.MapGet("", async (IIgdbProviderSettingsService service, HttpContext context, CancellationToken ct) =>
        {
            var settings = await service.GetAsync(ct);
            return Results.Ok(settings).WithETag(context, settings.Revision.ToString());
        }).WithName("GetIgdbProviderSettings").Produces<IgdbProviderSettings>().ETagOnlyRead();
        group.MapPut("", async (UpdateIgdbProviderSettingsRequest request, HttpContext context,
            ICommandHandler<UpdateIgdbSettingsCommand, IgdbProviderSettings> handler, CancellationToken ct) =>
        {
            var precondition = Preconditions.IfMatch(context, "MetadataProviders");
            if (precondition.IsError)
                return precondition.Errors.ToProblem();
            var result = await handler.HandleAsync(new(request, precondition.Value), ct);
            return result.Match(value => Results.Ok(value).WithETag(context, value.Revision.ToString()), errors => errors.ToProblem());
        }).WithName("UpdateIgdbProviderSettings").Produces<IgdbProviderSettings>().ProducesProblems(400, 409).ConditionalWrite();
        group.MapPost("/test-connection", async (HttpContext context,
            ICommandHandler<TestIgdbConnectionCommand, IgdbProviderSettings> handler, CancellationToken ct) =>
        {
            var precondition = Preconditions.IfMatch(context, "MetadataProviders");
            if (precondition.IsError)
                return precondition.Errors.ToProblem();
            var result = await handler.HandleAsync(new(precondition.Value), ct);
            return result.Match(value => Results.Ok(value).WithETag(context, value.Revision.ToString()), errors => errors.ToProblem());
        }).WithName("TestIgdbProviderConnection").Produces<IgdbProviderSettings>().ConditionalWrite();
        return app;
    }
}
