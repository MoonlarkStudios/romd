using ErrorOr;
using Romd.Admin.Application.ConsumerApps.Commands.DisableConsumerWebApp;
using Romd.Admin.Application.ConsumerApps.Commands.RegisterConsumerWebApp;
using Romd.Admin.Application.ConsumerApps.Queries.ListConsumerWebApps;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.ConsumerApps;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ConsumerWebAppRegistrationEndpoints
{
    public static void MapConsumerWebAppRegistrationEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("consumer-web-apps").WithTags("Consumer web apps")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("", List).WithName("ListConsumerWebApps").Produces<IReadOnlyList<ConsumerWebApp>>();
        group.MapPost("", Register).WithName("RegisterConsumerWebApp").ProducesCreated<ConsumerWebApp>().ProducesProblems(400);
        group.MapDelete("{clientId}", Disable).WithName("DisableConsumerWebApp").Produces(204).ProducesProblems(404);
    }

    private static async Task<IResult> List(IQueryHandler<ListConsumerWebAppsQuery, IReadOnlyList<ConsumerWebApp>> handler,
        CancellationToken ct) =>
        (await handler.HandleAsync(new(), ct)).Match(Results.Ok, errors => errors.ToProblem());

    private static async Task<IResult> Register(RegisterConsumerWebAppRequest request,
        ICommandHandler<RegisterConsumerWebAppCommand, ConsumerWebApp> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(request.DisplayName, request.CallbackUri, request.BrowserOrigin), ct);
        return result.Match(app => Results.Created($"/api/consumer-web-apps/{app.ClientId}", app), errors => errors.ToProblem());
    }

    private static async Task<IResult> Disable(string clientId,
        ICommandHandler<DisableConsumerWebAppCommand, Success> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new(clientId), ct)).Match(_ => Results.NoContent(), errors => errors.ToProblem());
}
