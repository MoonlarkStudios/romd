using Romd.Application.Common.ReferenceCatalog;
using System.ComponentModel.DataAnnotations;
using Romd.Contracts.Common.Models;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Platform.Commands.ImportSystemDat;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models;
using Romd.Host.Authorization;

using ContractManagedSystem = Romd.Contracts.Management.Models.ManagedSystem;
using ContractSystemDatPreview = Romd.Contracts.Management.Models.SystemDatPreview;

namespace Romd.Host.Endpoints;

public static class SystemSetupEndpoints
{
    public static IEndpointRouteBuilder MapSystemSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/system-setup").WithTags("SystemSetup")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
        group.MapGet("", async (ISystemSetupService service, string? query = null, string? cursor = null,
            [Range(1, ReferencePageRequest.MaxLimit)] int limit = ReferencePageRequest.DefaultLimit, string? sortBy = null, CancellationToken ct = default) =>
        {
            var request = ReferencePageRequest.Create("Admin:system-setup", query, cursor, limit, sortBy);
            if (request.IsError)
                return request.Errors.ToProblem();
            var page = await service.ListAsync(request.Value, ct);
            return Results.Ok(new Page<ContractManagedSystem>
            {
                Items = page.Items.Select(x => new ContractManagedSystem(x.SystemId!, x.Name, x.ShortName, x.Manufacturer,
                x.Enabled, x.State, x.Message, x.CatalogCount, x.OwnedTitles, x.TrackedTitles)).ToArray(),
                NextCursor = page.NextCursor,
                HasNextPage = page.HasNextPage
            });
        }).WithName("ListManagedSystems").Produces<Page<ContractManagedSystem>>()
            .WithDescription("Registered systems in ascending PostgreSQL C (UTF-8 byte) natural-key order, page selected before status/count assembly in one repeatable-read observation. Query includes normalized names, short names, manufacturers and name aliases; aliases are a separate paged relationship. Default/max limit 100; sortBy supports key:asc.");
        group.MapPut("/{systemKey}/enabled", async (SystemKeyParameter systemKey, SetSystemEnabled request, ISystemSetupService service, CancellationToken ct) =>
        {
            var result = await service.SetEnabledAsync(systemKey.SystemId, request.Enabled, ct);
            return result.Match(_ => Results.NoContent(), errors => errors.ToProblem());
        }).WithName("SetSystemEnabled").Produces(204).ProducesProblems(400);
        group.MapPost("/dat-preview", async ([FromForm] IFormFile file, ISystemSetupService service, [FromServices] IReferenceCatalogService referenceCatalog, CancellationToken ct) =>
        {
            if (file.Length is <= 0 or > 33554432)
                return ProblemResults.ValidationProblem("SystemSetup.InvalidFile", "file", "Choose a non-empty DAT document no larger than 32 MiB.");
            await using var stream = file.OpenReadStream();
            var result = await service.PreviewAsync(stream, ct);
            var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
            return result.Match(x => Results.Ok(new ContractSystemDatPreview(x.Document.Name, x.Document.Bytes,
                systemKeys.Optional(x.SuggestedPlatformId),
                x.ExistingDatIds.Select(id => new Sqid(id).ToString()).ToArray(), DatEndpoints.ReviewContract(x.Document.Preview))), errors => errors.ToProblem());
        }).WithName("PreviewSystemDat").Produces<ContractSystemDatPreview>().ProducesProblem(400)
            .WithMetadata(new RequestSizeLimitAttribute(34 * 1024 * 1024)).DisableAntiforgery();
        group.MapPost("/{systemKey}/import", async (SystemKeyParameter systemKey, [FromForm] IFormFile file,
            [FromQuery] string sha256, ICommandHandler<ImportSystemDatCommand, Guid> handler, CancellationToken ct) =>
        {
            if (file.Length is <= 0 or > 33554432)
                return ProblemResults.ValidationProblem("SystemSetup.InvalidFile", "file", "Choose a non-empty DAT document no larger than 32 MiB.");
            await using var stream = file.OpenReadStream();
            var result = await handler.HandleAsync(new(systemKey.SystemId, stream, sha256), ct);
            return result.ToAccepted();
        }).WithName("ImportSystemDat")
            .WithDescription("Import the reviewed document. The required sha256 query parameter is a snapshot binding " +
                "to the preview's exact candidate bytes; a changed document is a 409.")
            .ProducesAcceptedJob().ProducesProblem(400).ProducesProblem(409)
            .WithMetadata(new RequestSizeLimitAttribute(34 * 1024 * 1024)).DisableAntiforgery();
        return app;
    }
}
