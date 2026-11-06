using System.ComponentModel.DataAnnotations;
using Romd.Admin.Application.Source.Dat.Commands.CreateDatSubscription;
using Romd.Admin.Application.Source.Dat.Commands.CheckDatSubscription;
using Romd.Admin.Application.Source.Dat.Commands.ApplyDatSubscription;
using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Systems;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.StopDatSubscription;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models;
using Romd.Host.Authorization;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Host.Endpoints;

/// <summary>
///     The publisher directory (<c>/dat-catalogs</c>) and the one canonical DAT subscription collection
///     (<c>/dat-subscriptions/{subscriptionId}</c>). The review actions are snapshot-bound: their
///     <c>activeSha256</c> and <c>candidateSha256</c> pin the reviewed document pair, and a stale pin is a 409.
/// </summary>
public static class DatCatalogEnrollmentEndpoints
{
    private const int MaxCatalogIdLength = 100;
    private const string ServerHeldDescription = "The saved candidate and installed document are held by the server, " +
        "so one that fails validation is a 409 (DatReview.InvalidDocument or DatReview.Identity), not a 400.";

    public static IEndpointRouteBuilder MapDatCatalogEnrollmentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dat-catalogs", async (IDatCatalogEnrollmentService service, CancellationToken ct) =>
        {
            var value = await service.ListCatalogsAsync(ct);
            return Results.Ok(new DatCatalogDirectoryDto(value.Enabled, value.Message,
                value.Catalogs.Select(x => new PublishedDatCatalogDto(x.CatalogId, x.SystemId, x.Name, x.Provider, x.Health,
                    x.DocumentHash, x.EntryCount, x.FileCount, x.LastChangedAt)).ToArray()));
        }).WithTags("DatSubscriptions").WithName("ListDatCatalogs")
            .WithDescription("The publisher's DAT catalog directory. Bounded by the publisher: at most one entry per published " +
                "system catalog, read from its signed index. Answers 200 when subscriptions are disabled or the publisher " +
                "is unavailable; message then says why the directory is empty.")
            .Produces<DatCatalogDirectoryDto>()
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        var group = app.MapGroup("/dat-subscriptions").WithTags("DatSubscriptions")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
        group.MapGet("", async (IDatCatalogEnrollmentService service, [FromServices] IReferenceCatalogService referenceCatalog, CancellationToken ct) =>
        {
            var subscriptions = await service.ListAsync(ct);
            var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
            return Results.Ok(subscriptions.Select(item => Contract(item, systemKeys)).ToArray());
        }).WithName("ListDatSubscriptions")
            .WithDescription("Every local DAT subscription. Bounded by the publisher directory: one subscription per published catalog.")
            .Produces<DatCatalogSubscriptionDto[]>();
        group.MapPost("", async (CreateDatSubscriptionRequest request, ICommandHandler<CreateDatSubscriptionCommand, DatSubscriptionEnrollment> handler, [FromServices] IReferenceCatalogService referenceCatalog, CancellationToken ct) =>
        {
            var failures = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.CatalogId) || request.CatalogId.Length > MaxCatalogIdLength)
                failures["catalogId"] = [$"Use a published catalog id of at most {MaxCatalogIdLength} characters."];
            int? datId = RequestBodyIds.CollectOptional(request.DatId, "datId", failures);
            if (failures.Count > 0)
                return ProblemResults.ValidationProblem("DatEnrollment.InvalidRequest", failures);
            var result = await handler.HandleAsync(new(request.CatalogId, datId), ct);
            if (result.IsError)
                return result.Errors.ToProblem();
            var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
            var subscription = Contract(result.Value.Subscription, systemKeys);
            return result.Value.Created
                ? Results.Created($"/api/dat-subscriptions/{subscription.Id}", subscription)
                : Results.Ok(subscription);
        }).WithName("CreateDatSubscription")
            .WithDescription("Enrolls a publisher catalog and runs its first check. Idempotent on catalogId: an existing " +
                "subscription answers 200 with its current state and is not checked again. datId binds one installed " +
                "active source with the catalog's system and name; without it, enrollment binds only a unique name match.")
            .ProducesCreated<DatCatalogSubscriptionDto>()
            .Produces<DatCatalogSubscriptionDto>()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict);
        group.MapGet("/{subscriptionId}", async (Sqid subscriptionId, IDatCatalogEnrollmentService service, [FromServices] IReferenceCatalogService referenceCatalog, CancellationToken ct) =>
        {
            var result = await service.GetAsync(subscriptionId.Value, ct);
            var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
            return result.Match(value => Results.Ok(Contract(value, systemKeys)), errors => errors.ToProblem());
        }).WithName("GetDatSubscription").Produces<DatCatalogSubscriptionDto>().ProducesProblems(StatusCodes.Status404NotFound);
        group.MapDelete("/{subscriptionId}", async (Sqid subscriptionId, ICommandHandler<StopDatSubscriptionCommand, ErrorOr.Success> handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(new StopDatSubscriptionCommand(subscriptionId.Value), ct);
            return result.Match(_ => Results.NoContent(), errors => errors.ToProblem());
        }).WithName("DeleteDatSubscription")
            .WithDescription("Stops tracking the catalog. Installed sources, DAT versions and files stay.")
            .Produces(StatusCodes.Status204NoContent).ProducesProblems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        group.MapPost("/{subscriptionId}/check", async (Sqid subscriptionId, ICommandHandler<CheckDatSubscriptionCommand, DatCatalogSubscription> handler, [FromServices] IReferenceCatalogService referenceCatalog, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(new(subscriptionId.Value), ct);
            var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
            return result.Match(value => Results.Ok(Contract(value, systemKeys)), errors => errors.ToProblem());
        }).WithName("CheckDatSubscription")
            .WithDescription("Checks the publisher now, without waiting for the next scheduled check. A new candidate waits for review.")
            .Produces<DatCatalogSubscriptionDto>().ProducesProblems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        group.MapGet("/{subscriptionId}/preview", async (Sqid subscriptionId, IDatCatalogEnrollmentService service, CancellationToken ct) =>
        {
            var result = await service.PreviewAsync(subscriptionId.Value, ct);
            return result.Match(value => Results.Ok(DatEndpoints.ReviewContract(value)), errors => errors.ToProblem());
        }).WithName("GetDatSubscriptionPreview")
            .WithDescription("Compares the saved candidate with the installed document. Its activeSha256 and candidateSha256 " +
                "are the snapshot binding that the changes reads and the apply action take. " + ServerHeldDescription)
            .Produces<Models.DatReplacementPreview>().ProducesProblems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        group.MapGet("/{subscriptionId}/changes", async (Sqid subscriptionId,
            [RegularExpression(DatReviewPins.ActivePattern)] string activeSha256,
            [RegularExpression(DatReviewPins.CandidatePattern), Length(DatReviewPins.HashLength, DatReviewPins.HashLength)] string candidateSha256,
            [RegularExpression(DatChangePageRequest.ChangePattern)] string? change,
            [MaxLength(DatChangePageRequest.MaxQueryLength)] string? query, string? cursor,
            [Range(1, DatChangePageRequest.MaxLimit)] int? limit,
            IDatCatalogEnrollmentService service, CancellationToken ct) =>
        {
            var page = DatChangePageRequest.ForEntries(activeSha256, candidateSha256, change, query, cursor, limit);
            if (page.IsError)
                return page.Errors.ToProblem(DatEndpoints.InvalidChangeRequest);
            var result = await service.ListChangesAsync(subscriptionId.Value, page.Value, ct);
            return result.Match(value => Results.Ok(DatEndpoints.ChangesContract(value)), errors => errors.ToProblem());
        }).WithName("ListDatSubscriptionChanges")
            .WithDescription(DatEndpoints.ChangesDescription + " " + DatEndpoints.SnapshotBindingDescription + " " + ServerHeldDescription)
            .Produces<Romd.Contracts.Common.Models.Page<Models.DatEntryChange>>()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        group.MapGet("/{subscriptionId}/changes/{entryId}", async (Sqid subscriptionId, string entryId,
            [RegularExpression(DatReviewPins.ActivePattern)] string activeSha256,
            [RegularExpression(DatReviewPins.CandidatePattern), Length(DatReviewPins.HashLength, DatReviewPins.HashLength)] string candidateSha256, string? cursor,
            [Range(1, DatChangePageRequest.MaxLimit)] int? limit,
            IDatCatalogEnrollmentService service, CancellationToken ct) =>
        {
            var page = DatEndpoints.EntryPage(entryId, activeSha256, candidateSha256, cursor, limit);
            if (page.IsError)
                return page.Errors.ToProblem(DatEndpoints.InvalidChangeRequest);
            var result = await service.GetChangeAsync(subscriptionId.Value, page.Value.EntryName, page.Value.Page, ct);
            return result.Match(value => Results.Ok(DatEndpoints.EntryChangeContract(value)), errors => errors.ToProblem());
        }).WithName("GetDatSubscriptionChange")
            .WithDescription(DatEndpoints.EntryChangeDescription + " " + DatEndpoints.SnapshotBindingDescription + " " + ServerHeldDescription)
            .Produces<Models.DatEntryChangeDetail>()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        group.MapPost("/{subscriptionId}/apply", async (Sqid subscriptionId, ApplyDatSubscription request, ICommandHandler<ApplyDatSubscriptionCommand, Guid> handler, CancellationToken ct) =>
        {
            var failures = DatReviewPins.Failures(request.ActiveSha256, request.CandidateSha256)
                .ToDictionary(failure => failure.Field, failure => new[] { failure.Message });
            if (failures.Count > 0)
                return ProblemResults.ValidationProblem("DatReview.InvalidFingerprint", failures);
            var result = await handler.HandleAsync(new(subscriptionId.Value, request.ActiveSha256!, request.CandidateSha256), ct);
            return result.ToAccepted();
        }).WithName("ApplyDatSubscription")
            .WithDescription("Approves the reviewed candidate and starts its import or replacement job. Repeating an accepted " +
                "approval returns the same job. " + DatEndpoints.SnapshotBindingDescription)
            .ProducesAcceptedJob()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        return app;
    }
    private static string? Id(int? value) => value is int id ? new Sqid(id).ToString() : null;
    private static DatCatalogSubscriptionDto Contract(DatCatalogSubscription value, SystemKeys systemKeys) => new(new Sqid(value.Id).ToString(),
        value.CatalogId, value.SystemId, value.Name, systemKeys.Optional(value.PlatformId), Id(value.ActiveDatId), value.State,
        value.LastCheckedAt, value.Message, value.CandidateSha256, value.JobId, value.NextCheckAt, value.AutomaticChecksPaused);
}
