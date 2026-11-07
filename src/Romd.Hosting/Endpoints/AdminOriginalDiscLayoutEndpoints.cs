using System.ComponentModel.DataAnnotations;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Commands.DecideOriginalDiscLayout;
using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Models;
using Romd.Contracts.Management.Models.Media;
using Romd.Host.Authorization;

using ContractOriginalDiscLayout = Romd.Contracts.Management.Models.OriginalDiscLayout;

namespace Romd.Host.Endpoints;

public static class AdminOriginalDiscLayoutEndpoints
{
    public static IEndpointRouteBuilder MapAdminOriginalDiscLayoutEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("original-disc-layouts", async (IOriginalDiscLayoutService service, SystemKeyParameter? systemKey = null,
            string? cursor = null, [Range(1, 100)] int limit = 50, CancellationToken ct = default) =>
        {
            var result = await service.ListAsync(systemKey?.SystemId, cursor, limit, ct);
            return result.Match<IResult>(Results.Ok, errors => errors.ToProblem());
        }).WithName("ListOriginalDiscLayouts").WithTags("Original Disc Layouts")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin).Produces<Page<ContractOriginalDiscLayout>>().ProducesProblems(400);
        app.MapGet("original-disc-layouts/{layoutId}", async (Sqid layoutId, IOriginalDiscLayoutService service, HttpContext context, CancellationToken ct) =>
        {
            var result = await service.GetAsync(layoutId, ct);
            return result.Match(value => Results.Ok(value).WithETag(context, value.Revision.ToString()), errors => errors.ToProblem());
        }).WithName("GetOriginalDiscLayout").WithTags("Original Disc Layouts")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin).Produces<ContractOriginalDiscLayout>().ProducesProblems(400, 404).ETagOnlyRead();
        app.MapGet("original-disc-layouts/{layoutId}/candidates", async (Sqid layoutId, IOriginalDiscLayoutService service,
            SystemKeyParameter? systemKey = null, string? cursor = null, [Range(1, 100)] int limit = 50, CancellationToken ct = default) =>
        {
            var result = await service.CandidatesAsync(layoutId, systemKey?.SystemId, cursor, limit, ct);
            return result.Match<IResult>(Results.Ok, errors => errors.ToProblem());
        }).WithName("ListOriginalDiscLayoutCandidates").WithTags("Original Disc Layouts")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin).Produces<Page<OriginalDiscLayoutCandidate>>().ProducesProblems(400, 404);
        app.MapGet("original-disc-layouts/{layoutId}/audit", async (Sqid layoutId, IOriginalDiscLayoutService service,
            string? cursor = null, [Range(1, 100)] int limit = 50, CancellationToken ct = default) =>
        {
            var result = await service.AuditAsync(layoutId, cursor, limit, ct);
            return result.Match<IResult>(Results.Ok, errors => errors.ToProblem());
        }).WithName("ListOriginalDiscLayoutAudit").WithTags("Original Disc Layouts")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin).Produces<Page<OriginalDiscLayoutAudit>>().ProducesProblems(400, 404);
        app.MapPatch("original-disc-layouts/{layoutId}", async (Sqid layoutId, OriginalDiscLayoutDecisionRequest request, HttpContext context,
            ICommandHandler<DecideOriginalDiscLayoutCommand, ContractOriginalDiscLayout> handler, ICurrentUser user, CancellationToken ct) =>
        {
            var precondition = Preconditions.IfMatch(context, "OriginalLayout");
            if (precondition.IsError)
                return precondition.Errors.ToProblem();
            var result = await handler.HandleAsync(new(layoutId, request, precondition.Value, user.UserId!.Value), ct);
            return result.Match(value => Results.Ok(value).WithETag(context, value.Revision.ToString()), errors => errors.ToProblem());
        }).WithName("UpdateOriginalDiscLayout").WithTags("Original Disc Layouts")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin).Produces<ContractOriginalDiscLayout>()
            .ProducesProblems(400, 404, 409).ConditionalWrite();
        // The admin host cannot see the worker's encoder, so availability is this host's qualified decode capability; the
        // worker refuses an encode it cannot perform. The composition requires a CD track kind to name its qualified runtime.
        app.MapGet("original-disc-layouts/compression-recipe", (RomCdStorageRecipe recipe, IMediaHostCapabilities capabilities) =>
            Results.Ok(new CdStorageRecipe(capabilities.CanDecode(recipe.Kind), recipe.Id, checked((int)recipe.HunkBytes), recipe.CodecNames,
                capabilities.QualifiedRuntime(recipe.Kind)!)))
            .WithName("GetCdStorageRecipe").WithTags("Original Disc Layouts")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin).Produces<CdStorageRecipe>();
        app.MapGet("original-disc-layouts/{layoutId}/archive", async (Sqid layoutId, IRomCdArchiveDownloadCatalog catalog,
            IRomCdArchiveDownloadReader reader, HttpContext context, CancellationToken ct) =>
            await catalog.FindForLayoutAsync(layoutId, ct) is { } target
                ? await CdArchiveDownloadResults.CreateAsync(target, reader, context, false, ct) : ProblemResults.Problem(404, "OriginalLayout.Missing", "Original layout content not found."))
            .WithName("DownloadOriginalDiscArchive").WithTags("Original Disc Layouts")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin).Produces(200, contentType: "application/octet-stream").ProducesProblems(400, 404);
        app.MapGet("original-disc-layouts/{layoutId}/archive/cue", async (Sqid layoutId, IRomCdArchiveDownloadCatalog catalog,
            IRomCdArchiveDownloadReader reader, HttpContext context, CancellationToken ct) =>
            await catalog.FindForLayoutAsync(layoutId, ct) is { } target
                ? await CdArchiveDownloadResults.CreateAsync(target, reader, context, true, ct) : ProblemResults.Problem(404, "OriginalLayout.Missing", "Original layout content not found."))
            .WithName("DownloadOriginalDiscCue").WithTags("Original Disc Layouts")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin).Produces(200, contentType: "application/octet-stream").ProducesProblems(400, 404);
        return app;
    }
}
