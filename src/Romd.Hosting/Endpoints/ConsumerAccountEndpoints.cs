using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.Cqrs;
using Romd.Consumer.Application.Account.Commands.ChangeConsumerPassword;
using Romd.Consumer.Application.Account.Commands.UpdateConsumerUserSettings;
using Romd.Consumer.Application.Account.Queries.GetConsumerUserSettings;
using Romd.Contracts.Consumer.Account;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ConsumerAccountEndpoints
{
    public static IEndpointRouteBuilder MapConsumerAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("account")
            .WithTags("Consumer Account");

        group.MapPost("password", ChangePassword)
            .WithName("ChangeConsumerPassword")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("settings", GetSettings)
            .WithName("GetConsumerUserSettings")
            .Produces<ConsumerUserSettingsDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPut("settings", UpdateSettings)
            .WithName("UpdateConsumerUserSettings")
            .Produces<ConsumerUserSettingsDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        return app;
    }

    private static async Task<IResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        ICommandHandler<ChangeConsumerPasswordCommand, Updated> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new ChangeConsumerPasswordCommand(request.CurrentPassword, request.NewPassword),
            cancellationToken);

        return result.IsError ? result.Errors.ToProblem() : Results.NoContent();
    }

    private static async Task<IResult> GetSettings(
        IQueryHandler<GetConsumerUserSettingsQuery, ConsumerUserSettingsDto> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetConsumerUserSettingsQuery(), cancellationToken);

        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> UpdateSettings(
        [FromBody] UpdateConsumerUserSettingsRequest request,
        ICommandHandler<UpdateConsumerUserSettingsCommand, ConsumerUserSettingsDto> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new UpdateConsumerUserSettingsCommand(request.Theme, request.ReleasePreference),
            cancellationToken);

        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }
}
