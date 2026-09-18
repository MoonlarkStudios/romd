using Romd.Application.Common.ReferenceCatalog;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Source.Platform;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class SystemSetupEndpoints
{
    public static IEndpointRouteBuilder MapSystemSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/system-setup").WithTags("SystemSetup")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
        group.MapGet("", async (ISystemSetupService service, CancellationToken ct) =>
            Results.Ok((await service.ListAsync(ct)).Select(x => new ManagedSystemDto(x.SystemId ?? throw new InvalidOperationException("Unregistered system"), x.Name, x.ShortName, x.Manufacturer, x.Aliases, x.Enabled, x.State, x.Message, x.CatalogCount, x.OwnedTitles, x.TrackedTitles))))
            .WithName("ListManagedSystems").Produces<IReadOnlyList<ManagedSystemDto>>();
        group.MapPut("/{systemKey}/enabled", async (string systemKey, [FromServices] IReferenceCatalogService referenceCatalog, SetSystemEnabled request, ISystemSetupService service, CancellationToken ct) =>
        {
            var result = await service.SetEnabledAsync(await referenceCatalog.RequireSystemAsync(systemKey, ct), request.Enabled, ct);
            return result.Match(_ => Results.NoContent(), errors => errors.ToProblem());
        }).WithName("SetSystemEnabled").Produces(204).ProducesProblem(404);
        group.MapPost("/dat-preview", async ([FromForm] IFormFile file, ISystemSetupService service, [FromServices] IReferenceCatalogService referenceCatalog, CancellationToken ct) =>
        {
            if (file.Length is <= 0 or > 33554432) return Results.BadRequest();
            await using var stream = file.OpenReadStream();
            var result = await service.PreviewAsync(stream, ct);
            var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
            return result.Match(x => Results.Ok(new SystemDatPreviewDto(x.Document.Name, x.Document.Bytes,
                systemKeys.Optional(x.SuggestedPlatformId),
                x.ExistingDatIds.Select(id => new Sqid(id).ToString()).ToArray(), DatEndpoints.ReviewContract(x.Document.Preview))), errors => errors.ToProblem());
        }).WithName("PreviewSystemDat").Produces<SystemDatPreviewDto>().ProducesProblem(400)
            .WithMetadata(new RequestSizeLimitAttribute(34 * 1024 * 1024)).DisableAntiforgery();
        group.MapPost("/{systemKey}/import", async (string systemKey, [FromServices] IReferenceCatalogService referenceCatalog, [FromForm] IFormFile file,
            [FromQuery] string sha256, ISystemSetupService service, CancellationToken ct) =>
        {
            if (file.Length is <= 0 or > 33554432) return Results.BadRequest();
            await using var stream = file.OpenReadStream();
            var result = await service.ImportAsync(await referenceCatalog.RequireSystemAsync(systemKey, ct), stream, sha256, ct);
            return result.Match(x => Results.Accepted(x.StatusUrl, new UploadAccepted(x.JobId, x.BackgroundJobId, x.StatusUrl)), errors => errors.ToProblem());
        }).WithName("ImportSystemDat").Produces<UploadAccepted>(202).ProducesProblem(400).ProducesProblem(409)
            .WithMetadata(new RequestSizeLimitAttribute(34 * 1024 * 1024)).DisableAntiforgery();
        return app;
    }
}
