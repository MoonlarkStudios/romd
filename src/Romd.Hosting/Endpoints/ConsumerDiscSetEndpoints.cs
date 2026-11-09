using System.ComponentModel.DataAnnotations;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Pagination;
using Romd.Contracts.Common.Models;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Delivery.Commands.CheckConsumerDiscSetAccess;
using Romd.Consumer.Application.Delivery.Commands.IssueConsumerDiscSetManifest;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Releases;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ConsumerDiscSetEndpoints
{
    public static IEndpointRouteBuilder MapConsumerDiscSetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("disc-sets", ListDiscSets).WithName("ListConsumerDiscSets")
            .WithTags("Consumer Delivery").RequireAuthorization(AuthorizationPolicies.RequireUser)
            .Produces<Page<ConsumerDiscSetStatus>>().Produces(401).ProducesProblems(400, 404);
        app.MapGet("disc-sets/{discSetId}/status", async (Sqid discSetId, ICurrentUser user, IConsumerDiscSetRepository repository,
            IServerInstanceIdentity server, CancellationToken ct) =>
        {
            if (user.UserId is not { } userId)
                return new[] { ConsumerErrors.CurrentUserRequired() }.ToProblem();
            var result = await repository.ReadStatusAsync(new(userId), discSetId, ct);
            return result switch
            {
                ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found found => Results.Ok(DiscSetContracts.Status(found.Value, server.InstanceId)),
                ConsumerLibraryReadResult<ConsumerDiscSetPlan>.LibraryUnavailable or ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ProjectionInconsistent
                    => new[] { ConsumerErrors.CurrentLibraryUnavailable() }.ToProblem(),
                _ => new[] { ConsumerErrors.ContentNotFound() }.ToProblem()
            };
        }).WithName("GetConsumerDiscSetStatus").WithTags("Consumer Delivery").RequireAuthorization(AuthorizationPolicies.RequireUser)
            .Produces<ConsumerDiscSetStatus>().Produces(401).ProducesProblems(400, 404, 409);
        app.MapPost("disc-sets/{discSetId}/selection-manifest", async (Sqid discSetId, ConsumerDiscSetSelectionRequest request,
            ICommandHandler<IssueConsumerDiscSetManifestCommand, ConsumerDiscSetManifest> handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(new(discSetId, request.Revision, request), ct);
            return result.Match<IResult>(Results.Ok, errors => errors.ToProblem());
        }).WithName("IssueConsumerDiscSetSelectionManifest").WithTags("Consumer Delivery").RequireAuthorization(AuthorizationPolicies.RequireUser)
            .Produces<ConsumerDiscSetManifest>().Produces(401).ProducesProblems(400, 404, 409);
        app.MapPost("disc-sets/{discSetId}/access/preview", async (Sqid discSetId, ConsumerDiscSetAccessRequest request,
            ICommandHandler<CheckConsumerDiscSetAccessCommand, ConsumerDiscSetAccess> handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(new(discSetId, request), ct);
            return result.Match<IResult>(Results.Ok, errors => errors.ToProblem());
        }).WithName("CheckConsumerDiscSetAccess").WithTags("Consumer Delivery").RequireAuthorization(AuthorizationPolicies.RequireUser)
            .Produces<ConsumerDiscSetAccess>().Produces(401).ProducesProblems(400, 409);
        app.MapPost("disc-sets/{discSetId}/manifest", async (Sqid discSetId, Guid revision,
            ICommandHandler<IssueConsumerDiscSetManifestCommand, ConsumerDiscSetManifest> handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(new(discSetId, revision), ct);
            return result.Match<IResult>(Results.Ok, errors => errors.ToProblem());
        }).WithName("IssueConsumerDiscSetManifest").WithTags("Consumer Delivery").RequireAuthorization(AuthorizationPolicies.RequireUser)
            .Produces<ConsumerDiscSetManifest>().Produces(401).ProducesProblems(400, 404, 409);
        return app;
    }
    private static async Task<IResult> ListDiscSets(ICurrentUser user, IConsumerDiscSetRepository repository,
        IServerInstanceIdentity server, Sqid? titleId = null, string? cursor = null,
        [Range(1, 100)] int limit = 50, CancellationToken ct = default)
    {
        if (user.UserId is not { } userId)
            return new[] { ConsumerErrors.CurrentUserRequired() }.ToProblem();
        var result = await repository.ListPageAsync(new(userId), titleId, cursor, limit, ct);
        return result switch
        {
            ConsumerLibraryReadResult<PagedList<ConsumerDiscSetPlan>>.Found found => Results.Ok(new Page<ConsumerDiscSetStatus>
            {
                Items = found.Value.Items.Select(plan => DiscSetContracts.Status(plan, server.InstanceId)).ToArray(),
                NextCursor = found.Value.NextCursor,
                HasNextPage = found.Value.HasNextPage
            }),
            ConsumerLibraryReadResult<PagedList<ConsumerDiscSetPlan>>.Rejected rejected => new[] { rejected.Error }.ToProblem(),
            _ => new[] { ConsumerErrors.LibraryNotFound() }.ToProblem()
        };
    }

}
