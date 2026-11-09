using ErrorOr;
using Romd.Admin.Application.Source.Rom.Commands.RetryMediaReconciliation;
using Romd.Admin.Application.Source.Rom.Commands.StartMediaReconciliationSelection;
using Romd.Admin.Application.Source.Rom.Commands.StartMediaReconciliationSubject;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Content;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models.Media;
using Romd.Domain.Source.Rom;
using Romd.Host.Authorization;

using ContractMediaReconciliationPolicy = Romd.Contracts.Management.Models.Media.MediaReconciliationPolicy;

namespace Romd.Host.Endpoints;

public static class MediaReconciliationEndpoints
{
    public static IEndpointRouteBuilder MapMediaReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("media/reconciliation").WithTags("Media")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
        group.MapGet("policies", Policies).WithName("GetMediaReconciliationPolicies")
            .WithDescription("Current registered media recipes and read capabilities for this API host")
            .Produces<IReadOnlyList<ContractMediaReconciliationPolicy>>().ProducesProblems(401, 403);
        group.MapPost("subjects/{subjectKind}/{subjectId}", StartSubject).WithName("StartMediaReconciliationSubject")
            .WithDescription("Request current reconciliation for one logical ROM, original layout, catalog CD layout or retained representation")
            .ProducesAcceptedJob().ProducesProblems(400, 401, 403, 404, 409);
        group.MapPost("selections", StartSelection).WithName("StartMediaReconciliationSelection")
            .WithDescription("Accept a finite selection; current kind and eligibility are evaluated by the worker")
            .ProducesAcceptedJob().ProducesProblems(400, 401, 403, 409);
        group.MapPost("{jobId:guid}/retry", Retry).WithName("RetryMediaReconciliation")
            .WithDescription("Retry terminal media work, retaining its original subject intent or finite selection bounds")
            .ProducesAcceptedJob().ProducesProblems(400, 401, 403, 404, 409);
        return app;
    }

    private static IResult Policies(MediaKindCatalog kinds, IMediaHostCapabilities capabilities, RomDeliveryPolicy delivery) =>
        Results.Ok(kinds.Kinds.Select(kind => new ContractMediaReconciliationPolicy(kind.Kind, kind.CurrentRecipeId!, kind.ConversionMode.ToString(),
            kind.PlatformKeys, kind.ManifestType.ToString(), delivery.Formats.Entry(RomDeliveryPolicy.StoredFormat(kind.StoredEncoding)).Label,
            capabilities.CanDecode(kind.Kind), capabilities.QualifiedRuntime(kind.Kind))).ToArray());

    private static async Task<IResult> StartSubject(string subjectKind, Sqid subjectId, StartMediaReconciliationSubjectRequest request,
        ICommandHandler<StartMediaReconciliationSubjectCommand, Guid> handler, CancellationToken ct) =>
        ToResult(await handler.HandleAsync(new(request.RequestId, subjectKind, subjectId.Value, request.ConversionRequested), ct));

    private static async Task<IResult> StartSelection(StartMediaReconciliationSelectionRequest request,
        ICommandHandler<StartMediaReconciliationSelectionCommand, Guid> handler, CancellationToken ct) =>
        ToResult(await handler.HandleAsync(new(request.RequestId, request.Filter, request.KindId, request.SystemKey, request.Limit, request.ConversionRequested), ct));

    private static async Task<IResult> Retry(Guid jobId, RetryMediaReconciliationRequest request,
        ICommandHandler<RetryMediaReconciliationCommand, Guid> handler, CancellationToken ct) =>
        ToResult(await handler.HandleAsync(new(request.RequestId, jobId), ct));

    private static IResult ToResult(ErrorOr<Guid> result) => result.Match(JobAccepted.Result, errors =>
        // Preserve this surface's existing request rejection envelope without adding field errors.
        errors is [{ Code: MediaReconciliationErrors.InvalidRequestCode } error]
            ? ProblemResults.Problem(400, error.Code, error.Description)
            : errors.ToProblem());
}
