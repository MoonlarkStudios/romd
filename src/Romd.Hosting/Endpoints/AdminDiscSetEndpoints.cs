using System.ComponentModel.DataAnnotations;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Commands.DecideDiscSet;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Models;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class AdminDiscSetEndpoints
{
    public static IEndpointRouteBuilder MapAdminDiscSetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("disc-sets").WithTags("Disc Sets").RequireAuthorization(AuthorizationPolicies.RequireAdmin);
        group.MapGet("", async (IDiscSetService service, Sqid? titleId = null, string? cursor = null,
            [Range(1, 100)] int limit = 50, CancellationToken ct = default) =>
        {
            var result = await service.ListAsync(titleId?.Value, cursor, limit, ct);
            return result.Match<IResult>(Results.Ok, errors => errors.ToProblem());
        }).WithName("ListAdminDiscSets").Produces<Page<DiscSetDto>>().ProducesProblems(400, 404);
        group.MapGet("{discSetId}", async (Sqid discSetId, IDiscSetService service, HttpContext context, CancellationToken ct) =>
        {
            var result = await service.GetAsync(discSetId, ct);
            return result.Match(value => Results.Ok(value).WithETag(context, value.Revision.ToString()), errors => errors.ToProblem());
        }).WithName("GetAdminDiscSet").Produces<DiscSetDto>().ProducesProblems(400, 404).ETagOnlyRead();
        group.MapPatch("{discSetId}", async (Sqid discSetId, DecideDiscSetRequest request, HttpContext context,
            ICommandHandler<DecideDiscSetCommand, DiscSetDto> handler, ICurrentUser user, CancellationToken ct) =>
        {
            var precondition = Preconditions.IfMatch(context, "DiscSet");
            if (precondition.IsError)
                return precondition.Errors.ToProblem();
            var result = await handler.HandleAsync(new(discSetId, request, precondition.Value, user.UserId!.Value), ct);
            return result.Match(value => Results.Ok(value).WithETag(context, value.Revision.ToString()), errors => errors.ToProblem());
        }).WithName("UpdateAdminDiscSet").Produces<DiscSetDto>().ProducesProblems(400, 404, 409).ConditionalWrite();
        return app;
    }
}
