using Romd.Application.Common.ReferenceCatalog;
using System.ComponentModel.DataAnnotations;
using Romd.Contracts.Common.Models;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.ReferenceData.Systems.Commands;
using Romd.Admin.Application.ReferenceData.Systems.Queries.PreviewSystemNameAliases;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Platform.Commands.ImportSystemDat;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Contracts.Management.Models;
using Romd.Host.Authorization;
using Romd.Domain.ReferenceData;

using ContractManagedSystem = Romd.Contracts.Management.Models.ManagedSystem;
using ContractSystemDatPreview = Romd.Contracts.Management.Models.SystemDatPreview;
using ContractSystemOverrides = Romd.Contracts.Common.ReferenceCatalog.SystemOverrides;

namespace Romd.Host.Endpoints;

public static class SystemSetupEndpoints
{
    public static IEndpointRouteBuilder MapSystemSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/system-setup").WithTags("SystemSetup");
        var group = routes.MapGroup("").RequireAuthorization(AuthorizationPolicies.RequireAdmin);
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
                x.Enabled, x.State, x.Message, x.CatalogCount, x.OwnedTitles, x.TrackedTitles, x.KnownTitles, x.LastDatImportedAt)).ToArray(),
                NextCursor = page.NextCursor,
                HasNextPage = page.HasNextPage
            });
        }).WithName("ListManagedSystems").Produces<Page<ContractManagedSystem>>()
            .WithDescription("Registered systems in ascending PostgreSQL C (UTF-8 byte) natural-key order, page selected before status/count assembly in one repeatable-read observation. Query includes normalized names, short names, manufacturers and name aliases; aliases are a separate paged relationship. Default/max limit 100; sortBy supports key:asc.");
        routes.MapGet("/{systemKey}", async (SystemKeyParameter systemKey, ISystemSetupService service, CancellationToken ct) =>
        {
            var details = await service.GetDetailsAsync(systemKey.Key, ct);
            if (details is null)
                return ReferenceCatalogEndpoints.UnknownSystem();
            var hardware = details.Hardware;
            return Results.Ok(new ManagedSystemSetupDetails(details.System,
                hardware is null ? null : new(hardware.Category, hardware.Generation, hardware.ReleaseYear)));
        }).WithName("GetManagedSystemDetails").Produces<ManagedSystemSetupDetails>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser)
            .WithDescription("System reference facts and optional hardware metadata read from the same database snapshot.");
        group.MapPost("", CreateAndEnable).WithName("CreateManagedSystem")
            .WithDescription("Create an installation-owned system and add it to the installation's Systems list in the same transaction.")
            .ProducesCreated<SystemResource>().ProducesProblems(400, 409).ConditionalCreate();
        group.MapPatch("/{systemKey}", UpdatePresentation).WithName("UpdateManagedSystemPresentation")
            .WithDescription("Update permitted system facts and add name aliases atomically. By default, add the system to the Systems list. Pass enableForSetup=false to keep its membership as is. Use the system reference resource's current ETag.")
            .Produces<SystemResource>().ProducesProblems(400, 409).ConditionalWrite();
        group.MapPut("/{systemKey}/enabled", async (SystemKeyParameter systemKey, SetSystemEnabled request, ISystemSetupService service, CancellationToken ct) =>
        {
            var result = await service.SetEnabledAsync(systemKey.SystemId, request.Enabled, ct);
            return result.Match(_ => Results.NoContent(), errors => errors.ToProblem());
        }).WithName("SetSystemEnabled").Produces(204).ProducesProblems(400);
        group.MapPost("/name-aliases/preview", async (PreviewSystemNameAliasesRequest request,
            IQueryHandler<PreviewSystemNameAliasesQuery, SystemNameAliasConflict[]> handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(new(request.NameAliases), ct);
            return result.Match(Results.Ok, errors => errors.ToProblem());
        }).WithName("PreviewSystemNameAliases").Produces<SystemNameAliasConflict[]>().ProducesProblems(400)
            .WithDescription("Check up to 16 proposed name aliases for existing matches using the same case and whitespace normalization as DAT matching.");
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

    private static async Task<IResult> CreateAndEnable(CreateManagedSystemRequest request,
        ICommandHandler<CreateSystemCommand, ReferenceResourceResult<SystemResource, ContractSystemOverrides>> handler, HttpContext context, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(request.Key,
            new(request.Name, request.CompactLabel, request.Description, request.Icon, request.Monochrome), request.ManufacturerKeys, EnableForSetup: true,
            Hardware: new(request.Category, request.Generation, request.ReleaseYear), NameAliases: request.NameAliases), ct);
        return result.Match(
            created => Results.Created("/api/systems/" + Uri.EscapeDataString(created.Resource.Key), created.Resource).WithETag(context, created.Revision),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> UpdatePresentation(SystemKeyParameter systemKey, ManagedSystemPatch patch, HttpContext context,
        ICommandHandler<UpdateSystemCommand, ReferenceResourceResult<SystemResource, ContractSystemOverrides>> handler, CancellationToken ct,
        bool enableForSetup = true)
    {
        var ifMatch = Preconditions.IfMatch(context, ReferenceCatalogEndpoints.PreconditionArea);
        if (ifMatch.IsError)
            return ifMatch.Errors.ToProblem();
        var supplied = patch.Changes;
        if (supplied.ContainsKey("manufacturerKeys") && patch.ManufacturerKeys is null ||
            supplied.ContainsKey("nameAliases") && patch.NameAliases is null)
            return ProblemResults.Problem(400, "ReferenceResource.Invalid", "Manufacturer keys and name aliases cannot be null.");
        var hasCategory = supplied.ContainsKey("category");
        var hasGeneration = supplied.ContainsKey("generation");
        var hasReleaseYear = supplied.ContainsKey("releaseYear");
        SystemHardwareChanges? hardware = hasCategory || hasGeneration || hasReleaseYear
            ? new(new(patch.Category, patch.Generation, patch.ReleaseYear), hasCategory, hasGeneration, hasReleaseYear)
            : null;
        var result = await handler.HandleAsync(new(systemKey.Key,
            new(Name: patch.Name, Description: patch.Description, HasDescription: supplied.ContainsKey("description")),
            ifMatch.Value, patch.ManufacturerKeys, EnableForSetup: enableForSetup, Hardware: hardware, NameAliases: patch.NameAliases), ct);
        return result.Match(updated => Results.Ok(updated.Resource).WithETag(context, updated.Revision), errors => errors.ToProblem());
    }
}
