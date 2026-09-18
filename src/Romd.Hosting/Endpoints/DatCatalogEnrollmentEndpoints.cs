using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Systems;
using Romd.Admin.Application.Source.Dat;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class DatCatalogEnrollmentEndpoints
{
    public static IEndpointRouteBuilder MapDatCatalogEnrollmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/dat-subscriptions").WithTags("DatSubscriptions")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
        group.MapGet("", async (IDatCatalogEnrollmentService service, [FromServices] IReferenceCatalogService referenceCatalog, CancellationToken ct) =>
        {
            var result = await service.DiscoverAsync(ct);
            var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
            return result.Match(value => Results.Ok(new DatCatalogDirectoryDto(value.Enabled, value.Message,
                value.Catalogs.Select(x => new PublishedDatCatalogDto(x.CatalogId, x.SystemId, x.Name, x.Provider, x.Health,
                    x.DocumentHash, x.EntryCount, x.FileCount, x.LastChangedAt)).ToArray(),
                value.Subscriptions.Select(item => Contract(item, systemKeys)).ToArray())), errors => errors.ToProblem());
        }).WithName("DiscoverDatCatalogs").Produces<DatCatalogDirectoryDto>();
        group.MapGet("/{subscriptionId}", async (Sqid subscriptionId, IDatCatalogEnrollmentService service, [FromServices] IReferenceCatalogService referenceCatalog, CancellationToken ct) =>
        {
            var result = await service.GetAsync(subscriptionId.Value, ct);
            var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
            return result.Match(value => Results.Ok(Contract(value, systemKeys)), errors => errors.ToProblem());
        }).WithName("GetCatalogSubscription").Produces<DatCatalogSubscriptionDto>().ProducesProblem(404);
        group.MapDelete("/{subscriptionId}", async (Sqid subscriptionId, IDatCatalogEnrollmentService service, [FromServices] IReferenceCatalogService referenceCatalog, CancellationToken ct) =>
        {
            var result = await service.StopAsync(subscriptionId.Value, ct);
            return result.Match(_ => Results.NoContent(), errors => errors.ToProblem());
        }).WithName("StopCatalogSubscription").Produces(204).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("/check", async (CheckDatCatalogRequest request, IDatCatalogEnrollmentService service, [FromServices] IReferenceCatalogService referenceCatalog, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.CatalogId) || request.CatalogId.Length > 100) return Results.BadRequest();
            var result = await service.CheckAsync(request.CatalogId, ct);
            var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
            return result.Match(value => Results.Ok(Contract(value, systemKeys)), errors => errors.ToProblem());
        }).WithName("CheckCatalogSubscription").Produces<DatCatalogSubscriptionDto>().ProducesProblem(400).ProducesProblem(409);
        group.MapPost("/{subscriptionId}/preview", async (Sqid subscriptionId, IDatCatalogEnrollmentService service, [FromServices] IReferenceCatalogService referenceCatalog, CancellationToken ct) =>
        {
            var result = await service.PreviewAsync(subscriptionId.Value, ct);
            var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
            return result.Match(value => Results.Ok(DatEndpoints.ReviewContract(value)), errors => errors.ToProblem());
        }).WithName("PreviewCatalogSubscription").Produces<Romd.Contracts.Management.Models.DatReplacementPreview>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("/{subscriptionId}/changes", async (Sqid subscriptionId, DatChangeRequest request, IDatCatalogEnrollmentService service, [FromServices] IReferenceCatalogService referenceCatalog, CancellationToken ct) =>
        {
            var result = await service.ChangesAsync(subscriptionId.Value, DatEndpoints.ChangeQuery(request), ct);
            var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
            return result.Match(value => Results.Ok(DatEndpoints.ChangeContract(value)), errors => errors.ToProblem());
        }).WithName("GetCatalogSubscriptionChanges").Produces<Romd.Contracts.Management.Models.DatChangePage>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("/{subscriptionId}/apply", async (Sqid subscriptionId, ApplyDatSubscription request, IDatCatalogEnrollmentService service, [FromServices] IReferenceCatalogService referenceCatalog, CancellationToken ct) =>
        {
            if (!Hash(request.CandidateSha256) || (request.ActiveSha256 != "" && !Hash(request.ActiveSha256))) return Results.BadRequest();
            var result = await service.ApplyAsync(subscriptionId.Value, request.ActiveSha256, request.CandidateSha256, ct);
            var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
            return result.Match(value => Results.Accepted(value.StatusUrl, new UploadAccepted(value.JobId, value.BackgroundJobId, value.StatusUrl)), errors => errors.ToProblem());
        }).WithName("ApplyCatalogSubscription").Produces<UploadAccepted>(202).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        return app;
    }
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string? Id(int? value) => value is int id ? new Sqid(id).ToString() : null;
    private static DatCatalogSubscriptionDto Contract(DatCatalogSubscription value, SystemKeys systemKeys) => new(new Sqid(value.Id).ToString(),
        value.CatalogId, value.SystemId, value.Name, systemKeys.Optional(value.PlatformId), Id(value.ActiveDatId), value.State,
        value.LastCheckedAt, value.Message, value.CandidateSha256, value.JobId, value.NextCheckAt, value.AutomaticChecksPaused);
}
