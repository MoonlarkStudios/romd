using Romd.Admin.Application.Settings;
using Romd.Admin.Application.Settings.Commands.ReplaceSignInMessage;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Models;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class SignInMessageEndpoints
{
    public static void MapSignInMessageEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/server/sign-in-message").WithTags("Server")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("", async (ISignInMessageStore store, HttpContext context, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct);
            return Preconditions.ConditionalRead(context, settings, settings.Revision.ToString());
        }).WithName("GetSignInMessageSettings").Produces<SignInMessageSettings>().ConditionalRead();

        group.MapPut("", async (ReplaceSignInMessageRequest request, HttpContext context,
            ICommandHandler<ReplaceSignInMessageCommand, SignInMessageSettings> handler, CancellationToken ct) =>
        {
            var precondition = Preconditions.IfMatch(context, "SignInMessage");
            if (precondition.IsError)
                return precondition.Errors.ToProblem();

            var result = await handler.HandleAsync(new(request.Message, precondition.Value), ct);
            return result.Match(settings => Results.Ok(settings).WithETag(context, settings.Revision.ToString()), errors => errors.ToProblem());
        }).WithName("ReplaceSignInMessage").Produces<SignInMessageSettings>().ConditionalWrite();
    }
}
