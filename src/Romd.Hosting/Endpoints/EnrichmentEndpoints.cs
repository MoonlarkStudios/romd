using Romd.Admin.Application.Titles.Commands.QueueBulkEnrichmentJob;
using System.ComponentModel.DataAnnotations;
using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Commands.ReplaceArtworkEnrichmentSettings;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Source.Platform.Commands.SetMetadataPolicy;
using Romd.Admin.Application.Source.Platform.Queries.GetMetadataPolicy;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.SetEnrichmentOmission;
using Romd.Admin.Application.Titles.Commands.SetFieldOverrides;
using Romd.Admin.Application.Titles.Commands.TriggerTitleEnrichment;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.Queries.GetTitleEnrichmentState;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Artwork;
using Romd.Contracts.Management.Enrichment;
using Romd.Contracts.Management.Titles;
using Romd.Domain.Catalog;
using Romd.Host.Authorization;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Host.Endpoints;

public static class EnrichmentEndpoints
{
    public static IEndpointRouteBuilder MapEnrichmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/enrichment")
            .WithTags("Enrichment");

        group.MapGet("workbench", async ([FromServices] Romd.Admin.Application.Enrichment.IEnrichmentWorkbench workbench, CancellationToken ct,
            [ClosedValues("NeedsReview", "Automatic", "Processing", "Blocked", "All")] string view = "NeedsReview", string? cursor = null,
            [Range(1, Romd.Admin.Application.Enrichment.IEnrichmentWorkbench.MaxLimit)] int limit = 50, [MaxLength(200)] string? query = null) =>
            (await workbench.ReadAsync(view, cursor, limit, query, ct)).Match(Results.Ok, errors => errors.ToProblem()))
            .WithDescription("Current tracked titles in id ascending order, with query/view-bound cursors. Limit defaults to 50 and is at most 100; counts are read independently from workbench/stats.")
            .WithName("ListEnrichmentWorkbench").Produces<Page<EnrichmentAssessmentDto>>().ProducesProblems(400)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);
        group.MapGet("workbench/stats", async ([FromServices] Romd.Admin.Application.Enrichment.IEnrichmentWorkbench workbench,
            CancellationToken ct, [MaxLength(200)] string? query = null) =>
            (await workbench.ReadCountsAsync(query, ct)).Match(Results.Ok, errors => errors.ToProblem()))
            .WithName("GetEnrichmentWorkbenchStats").Produces<EnrichmentWorkbenchCountsDto>().ProducesProblems(400)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapGet("artwork/settings", GetArtworkSettings).WithName("GetArtworkEnrichmentSettings")
            .Produces<ArtworkEnrichmentSettingsDto>().ETagOnlyRead().RequireAuthorization(AuthorizationPolicies.RequireAdmin);
        group.MapPut("artwork/settings", UpdateArtworkSettings)
            .WithName("ReplaceArtworkEnrichmentSettings")
            .Produces<ArtworkEnrichmentSettingsDto>().ConditionalWrite().ProducesProblems(400).RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("stats", GetStats)
            .WithName("GetEnrichmentStats")
            .WithDescription("Get enrichment statistics across all titles")
            .Produces<EnrichmentStatsResponse>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        // Title enrichment state
        var titleGroup = app.MapGroup("/titles")
            .WithTags("Titles");

        // The enrichment state is a member of the title family: its writes rotate Titles.Revision, so it returns
        // the title's validator (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency").
        titleGroup.MapGet("{titleId}/enrichment", GetTitleEnrichmentState)
            .WithName("GetTitleEnrichmentState")
            .WithDescription("Get full enrichment state for a title including all provider data per field")
            .Produces<TitleEnrichmentStateResponse>()
            .ETagOnlyRead()
            .ProducesProblems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        titleGroup.MapPatch("{titleId}/enrichment", SetFieldOverrides)
            .WithName("UpdateTitleEnrichment")
            .WithDescription("Set or clear per-field source overrides for a title")
            .Produces<Models.TitleDetail>()
            .ConditionalWrite()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        titleGroup.MapPut("{titleId}/enrichment-omissions/{field}", SetOmission)
            .WithName("ReplaceTitleEnrichmentOmission")
            .WithDescription("Accept a core metadata field as absent, or revisit that decision")
            .Produces<Models.TitleDetail>()
            .ConditionalWrite()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        // Artwork acquisition is the title's automatic artwork state. Filling it writes no title row and no role, so
        // it takes no precondition.
        titleGroup.MapGet("{titleId}/artwork-acquisition", GetArtworkOutcomes)
            .WithName("GetTitleArtworkAcquisition")
            .Produces<ArtworkAcquisitionStateDto>()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);
        titleGroup.MapPost("{titleId}/artwork-acquisition/fill", FillMissingArtwork)
            .WithName("FillMissingTitleArtwork")
            .WithDescription("Queue automatic acquisition of the title's missing artwork")
            .ProducesAcceptedJob()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        // The derived requirement assessment, the same row the workbench lists. It is not a family member: it also
        // reflects provider availability and active work, which do not rotate the title's revision.
        titleGroup.MapGet("{titleId}/enrichment-assessment", async (Sqid titleId,
                [FromServices] Romd.Admin.Application.Enrichment.IEnrichmentWorkbench workbench, CancellationToken ct) =>
            (await workbench.GetAsync(titleId, ct)).Match(Results.Ok, errors => errors.ToProblem()))
            .WithName("GetTitleEnrichmentAssessment").Produces<EnrichmentAssessmentDto>().ProducesProblems(404)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        // Platform enrichment defaults
        var platformGroup = app.MapGroup("/systems")
            .WithTags("Systems");

        platformGroup.MapGet("{systemKey}/enrichment/defaults", GetPlatformFieldDefaults)
            .WithName("GetSystemEnrichmentDefaults")
            .Produces<PlatformMetadataPolicyDto>()
            .ETagOnlyRead()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        platformGroup.MapPatch("{systemKey}/enrichment/defaults", SetPlatformFieldDefaults)
            .WithName("UpdateSystemEnrichmentDefaults")
            .WithDescription("Set or clear platform-level field defaults for metadata resolution")
            .Produces<PlatformMetadataPolicyDto>()
            .ConditionalWrite()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        platformGroup.MapPost("{systemKey}/enrichment/bulk", TriggerBulkEnrichment)
            .WithName("TriggerSystemBulkEnrichment")
            .WithDescription("Trigger bulk enrichment for a platform; scope=Tracked (default) or All")
            .ProducesAcceptedJob()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        return app;
    }

    private static async Task<IResult> GetArtworkSettings(IArtworkAcquisitionStore store,
        IEnumerable<IProviderIdentityAdapter> adapters, HttpContext context, CancellationToken ct)
    {
        var settings = await store.GetSettingsAsync(ct);
        var enabled = new List<string>();
        foreach (var adapter in adapters.Where(x => x.Capabilities.Contains("artwork")))
        {
            var state = await adapter.GetAvailabilityAsync(ct);
            if (state.Enabled && state.Configured)
                enabled.Add(adapter.Id);
        }
        return Results.Ok(settings with { Providers = enabled }).WithETag(context, settings.Revision.ToString());
    }

    private static async Task<IResult> UpdateArtworkSettings(UpdateArtworkEnrichmentSettingsRequest request,
        ICommandHandler<ReplaceArtworkEnrichmentSettingsCommand, ArtworkEnrichmentSettingsDto> handler,
        HttpContext context, CancellationToken ct)
    {
        var precondition = Preconditions.IfMatch(context, "ArtworkEnrichment");
        if (precondition.IsError)
            return precondition.Errors.ToProblem();
        return (await handler.HandleAsync(new(request, precondition.Value), ct))
            .Match(settings => Results.Ok(settings).WithETag(context, settings.Revision.ToString()), errors => errors.ToProblem());
    }

    private static async Task<IResult> GetArtworkOutcomes(Sqid titleId, ITitleReader titles,
        IArtworkAcquisitionStore store, CancellationToken ct) =>
        await titles.GetSummaryAsync(titleId, ct) is null ? TitleEndpoints.TitleNotFound() :
            Results.Ok(await store.GetOutcomesAsync(titleId, ct));

    private static async Task<IResult> FillMissingArtwork(Sqid titleId,
        ICommandHandler<TriggerTitleEnrichmentCommand, Guid> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(titleId, ArtworkOnly: true), ct);
        return result.ToAccepted();
    }

    private static async Task<IResult> GetStats(
        ITitleEnrichmentStatusReader enrichmentStatus,
        CancellationToken ct)
    {
        var stats = await enrichmentStatus.GetStatsAsync(ct);

        return Results.Ok(new EnrichmentStatsResponse
        {
            Pending = stats.Pending,
            Completed = stats.Completed,
            NotFound = stats.NotFound,
            Failed = stats.Failed,
            LowConfidence = stats.LowConfidence,
            Total = stats.Pending + stats.Completed + stats.NotFound + stats.Failed + stats.LowConfidence
        });
    }

    private static async Task<IResult> GetTitleEnrichmentState(
        Sqid titleId,
        IQueryHandler<GetTitleEnrichmentStateQuery, TitleEnrichmentStateResponse> handler,
        HttpContext context,
        CancellationToken ct) =>
        (await handler.HandleAsync(new(titleId), ct)).Match(
            state => Results.Ok(state).WithETag(context, state.Revision), errors => errors.ToProblem());

    private static Task<IResult> SetFieldOverrides(
        Sqid titleId,
        SetFieldOverridesRequest request,
        HttpContext context,
        ICommandHandler<SetFieldOverridesCommand, Models.TitleDetail> handler,
        CancellationToken ct) =>
        TitleEndpoints.WriteAsync(context, titleId, ifMatch => handler.HandleAsync(
            new SetFieldOverridesCommand(titleId, request.Overrides, ifMatch),
            ct));

    private static Task<IResult> SetOmission(
        Sqid titleId,
        string field,
        SetEnrichmentOmissionRequest request,
        HttpContext context,
        ICommandHandler<SetEnrichmentOmissionCommand, Models.TitleDetail> handler,
        CancellationToken ct) =>
        TitleEndpoints.WriteAsync(context, titleId, ifMatch => handler.HandleAsync(
            new SetEnrichmentOmissionCommand(titleId, field, request.Accepted, ifMatch),
            ct));

    private static async Task<IResult> GetPlatformFieldDefaults(SystemKeyParameter systemKey, HttpContext context,
        IQueryHandler<GetMetadataPolicyQuery, PlatformMetadataPolicyDto> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(systemKey.SystemId), ct);
        return result.Match(policy => Results.Ok(policy).WithETag(context, policy.Revision), errors => errors.ToProblem());
    }

    private static async Task<IResult> SetPlatformFieldDefaults(SystemKeyParameter systemKey,
        SetPlatformFieldDefaultsRequest request,
        HttpContext context,
        ICommandHandler<SetMetadataPolicyCommand, PlatformMetadataPolicyDto> handler, CancellationToken ct)
    {
        var precondition = Preconditions.IfMatch(context, "MetadataPolicy");
        if (precondition.IsError)
            return precondition.Errors.ToProblem();
        var result = await handler.HandleAsync(new(systemKey.SystemId, precondition.Value, request.Defaults), ct);
        return result.Match(policy => Results.Ok(policy).WithETag(context, policy.Revision), errors => errors.ToProblem());
    }
    // The shared declared-parameter stage refuses a scope that is not a member name, numbers included.
    private static async Task<IResult> TriggerBulkEnrichment(
        SystemKeyParameter systemKey,
        ICommandHandler<QueueBulkEnrichmentJobCommand, Guid> handler,
        ICurrentUser currentUser,
        CancellationToken ct,
        EnrichmentScope scope = EnrichmentScope.Tracked)
    {
        var jobId = await handler.HandleAsync(new(systemKey.SystemId, scope, currentUser.UserId), ct);
        return jobId.ToAccepted();
    }
}

public sealed record EnrichmentStatsResponse
{
    public int Pending { get; init; }
    public int Completed { get; init; }
    public int NotFound { get; init; }
    public int Failed { get; init; }
    public int LowConfidence { get; init; }
    public int Total { get; init; }
}
