using OpenIddict.Abstractions;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Users.Commands.AccountSecurity;
using Romd.Admin.Application.Users.Queries.GetAccountSecurity;
using Romd.Contracts.Management.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Auth;
using Romd.Host.Authorization;
using Romd.Persistence.Identity;

namespace Romd.Host.Endpoints;

/// <summary>
///     Authentication endpoints for login and user info.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth")
            .WithTags("Auth");

        group.MapGet("me", GetCurrentUser)
            .WithName("GetCurrentUser")
            .WithDescription("Get information about the currently authenticated user")
            .Produces<CurrentUserResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPost("password", ChangePassword)
            .WithName("ChangePassword")
            .WithDescription("Change the current user's password")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("sessions", GetSessions).WithName("GetMyAccountSessions")
            .RequireAuthorization(AuthorizationPolicies.RequireUser).Produces<AccountSecurityDto>();
        group.MapDelete("sessions", RevokeSessions).WithName("RevokeMyAccountSessions")
            .RequireAuthorization(AuthorizationPolicies.RequireUser).Produces(204);
        return app;
    }

    private static async Task<IResult> GetSessions(ICurrentUser currentUser, HttpContext context,
        IQueryHandler<GetAccountSecurityQuery, AccountSecurityDto> handler, CancellationToken ct)
    {
        if (currentUser.UserId is not { } id) return Results.Unauthorized();
        var result = await handler.HandleAsync(new(id, Guid.TryParse(context.User.GetClaim(SessionClaims.Id), out var sessionId) ? sessionId : null), ct);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }

    private static async Task<IResult> RevokeSessions(ICurrentUser currentUser, HttpContext context, string? sessionId, bool? othersOnly,
        ICommandHandler<RevokeAccountSessionsCommand, Success> handler, CancellationToken ct)
    {
        if (currentUser.UserId is not { } id) return Results.Unauthorized();
        Guid? except = null;
        if (othersOnly == true)
        {
            if (sessionId is not null || !Guid.TryParse(context.User.GetClaim(SessionClaims.Id), out var current)) return Results.BadRequest();
            except = current;
        }
        var result = await handler.HandleAsync(new(id, sessionId, except), ct);
        return result.Match(_ => Results.NoContent(), errors => errors.ToProblem());
    }

    private static async Task<IResult> GetCurrentUser(
        ICurrentUser currentUser,
        UserManager<RomdUser> userManager,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is null)
        {
            return Results.Unauthorized();
        }

        var user = await userManager.FindByIdAsync(currentUser.UserId.Value.ToString());
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var roles = await userManager.GetRolesAsync(user);

        return Results.Ok(new CurrentUserResponse(
            Id: user.Id,
            Email: user.Email ?? string.Empty,
            Roles: roles.ToList(),
            LibraryId: user.LibraryId.HasValue ? IdCoder.Encode(user.LibraryId.Value) : null));
    }

    private static async Task<IResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        ICurrentUser currentUser,
        UserManager<RomdUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is null)
        {
            return Results.Unauthorized();
        }

        var missingFields = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            missingFields["currentPassword"] = ["Current password is required."];
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            missingFields["newPassword"] = ["New password is required."];
        }

        if (missingFields.Count > 0)
        {
            return ProblemResults.ValidationProblem(
                "Auth.MissingRequiredFields",
                missingFields,
                "Current password and new password are required.");
        }

        var user = await userManager.FindByIdAsync(currentUser.UserId.Value.ToString());
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var changeResult = await userManager.ChangePasswordAsync(
            user,
            request.CurrentPassword,
            request.NewPassword);
        if (!changeResult.Succeeded)
        {
            return ProblemResults.Problem(
                StatusCodes.Status400BadRequest,
                "Auth.ChangePasswordFailed",
                FormatIdentityErrors(changeResult));
        }

        user.UpdatedAt = timeProvider.GetUtcNow();
        var updateResult = await userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            return ProblemResults.Problem(
                StatusCodes.Status400BadRequest,
                "Auth.UpdateUserFailed",
                FormatIdentityErrors(updateResult));
        }

        return Results.NoContent();
    }

    private static string FormatIdentityErrors(IdentityResult result) =>
        string.Join(", ", result.Errors.Select(error => error.Description));
}
