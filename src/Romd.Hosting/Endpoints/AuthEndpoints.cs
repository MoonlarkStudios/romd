using OpenIddict.Abstractions;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Users.Commands.AccountSecurity;
using Romd.Admin.Application.Users.Queries.GetAccountSessions;
using Romd.Contracts.Common.Models;
using System.ComponentModel.DataAnnotations;
using Romd.Contracts.Management.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Auth;
using Romd.Host.Authorization;
using Romd.Persistence.Identity;
using Romd.Infrastructure.Identity;

using ContractAccountSession = Romd.Contracts.Management.Users.AccountSession;

namespace Romd.Host.Endpoints;

/// <summary>
///     Authentication endpoints for login and user info.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/me")
            .WithTags("Auth");

        group.MapGet("", GetCurrentUser)
            .WithName("GetMe")
            .WithDescription("Get information about the currently authenticated user")
            .Produces<CurrentUserResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPut("password", ChangePassword)
            .WithName("ChangeMyPassword")
            .WithDescription("Change the current user's password")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("sessions", GetSessions).WithName("ListMySessions")
            .RequireAuthorization(AuthorizationPolicies.RequireUser).Produces<Page<ContractAccountSession>>().ProducesProblems(400, 404);
        group.MapDelete("sessions", RevokeSessions).WithName("RevokeMySessions")
            .RequireAuthorization(AuthorizationPolicies.RequireUser).Produces(204).ProducesProblems(400, 404);
        return app;
    }

    private static async Task<IResult> GetSessions(ICurrentUser currentUser, HttpContext context,
        IQueryHandler<GetAccountSessionsQuery, Page<ContractAccountSession>> handler,
        string? cursor = null, [Range(1, 100)] int limit = 50, CancellationToken ct = default)
    {
        if (currentUser.UserId is not { } id)
            return ProblemResults.Problem(401, "Auth.Unauthorized", "A valid authenticated account is required.");
        var result = await handler.HandleAsync(new(id, cursor, limit,
            Guid.TryParse(context.User.GetClaim(SessionClaims.Id), out var sessionId) ? sessionId : null,
            "admin.me.sessions"), ct);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }

    private static async Task<IResult> RevokeSessions(ICurrentUser currentUser, HttpContext context, string? sessionId, bool? othersOnly,
        ICommandHandler<RevokeAccountSessionsCommand, Success> handler, CancellationToken ct)
    {
        if (currentUser.UserId is not { } id)
            return ProblemResults.Problem(401, "Auth.Unauthorized", "A valid authenticated account is required.");
        Guid? except = null;
        if (othersOnly == true)
        {
            if (sessionId is not null || !Guid.TryParse(context.User.GetClaim(SessionClaims.Id), out var current))
                return ProblemResults.ValidationProblem("Auth.InvalidSessionSelection",
                new Dictionary<string, string[]> { ["sessionId"] = ["Choose a single session or other sessions, with a current session identity."] });
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
            return ProblemResults.Problem(401, "Auth.Unauthorized", "A valid authenticated account is required.");
        }

        var user = await userManager.FindByIdAsync(currentUser.UserId.Value.ToString());
        if (user is null)
        {
            return ProblemResults.Problem(401, "Auth.Unauthorized", "A valid authenticated account is required.");
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
            return ProblemResults.Problem(401, "Auth.Unauthorized", "A valid authenticated account is required.");
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

        var currentLength = RomdPasswordLengthValidator.ValidateLength(request.CurrentPassword);
        if (!currentLength.Succeeded)
            return IdentityErrors("Auth.ChangePasswordFailed", currentLength);

        var user = await userManager.FindByIdAsync(currentUser.UserId.Value.ToString());
        if (user is null)
        {
            return ProblemResults.Problem(401, "Auth.Unauthorized", "A valid authenticated account is required.");
        }

        var changeResult = await userManager.ChangePasswordAsync(
            user,
            request.CurrentPassword,
            request.NewPassword);
        if (!changeResult.Succeeded)
        {
            return IdentityErrors("Auth.ChangePasswordFailed", changeResult);
        }

        user.UpdatedAt = timeProvider.GetUtcNow();
        var updateResult = await userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            return IdentityErrors("Auth.UpdateUserFailed", updateResult);
        }

        return Results.NoContent();
    }

    private static IResult IdentityErrors(string code, IdentityResult result) =>
        ProblemResults.ValidationProblem(code, result.Errors.GroupBy(error => error.Code)
            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray()));
}
