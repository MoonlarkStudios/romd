using Romd.Application.Common.Security;
using OpenIddict.Abstractions;
using ErrorOr;
using Romd.Admin.Application.Users.Queries.GetUserDirectory;
using Romd.Admin.Application.Users.Queries.GetAdminAudit;
using Romd.Admin.Application.Users.Commands.AccountSecurity;
using Romd.Admin.Application.Users.Queries.GetAccountSecurity;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Users;
using Romd.Admin.Application.Users.Commands.AssignDefaultLibraryToUsers;
using Romd.Admin.Application.Users.Commands.AssignUserLibrary;
using Romd.Admin.Application.Users.Commands.AssignUserRole;
using Romd.Admin.Application.Users.Commands.CreateUser;
using Romd.Admin.Application.Users.Commands.DeleteUser;
using Romd.Admin.Application.Users.Commands.UpdateUser;
using Romd.Admin.Application.Users.Queries.GetUserById;
using Romd.Admin.Application.Users.Queries.ListUsers;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Users;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

/// <summary>
///     User management endpoints (Admin only).
/// </summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/users")
            .WithTags("Users")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("", GetAll)
            .WithName("ListUsers")
            .Produces<IEnumerable<UserDto>>();

        group.MapGet("directory", GetDirectory).WithName("GetUserDirectory").Produces<UserDirectoryPageDto>().ProducesProblem(400);

        group.MapGet("{userId}", GetById)
            .WithName("GetUserById")
            .Produces<UserDto>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("", Create)
            .WithName("CreateUser")
            .Produces<UserDto>(StatusCodes.Status201Created)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);

        group.MapPut("{userId}", Update)
            .WithName("UpdateUser")
            .Produces<UserDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest);

        group.MapDelete("{userId}", Delete)
            .WithName("DeleteUser")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);

        group.MapPut("{userId}/role", AssignRole)
            .WithName("AssignUserRole")
            .Produces<UserDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest);

        group.MapPut("{userId}/library", AssignLibrary)
            .WithName("AssignUserLibrary")
            .Produces<UserDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);

        group.MapPost("assign-default-library", AssignDefaultLibrary)
            .WithName("AssignDefaultLibraryToUsers")
            .Produces<AssignDefaultLibraryResponse>()
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);

        group.MapGet("{userId}/security", GetSecurity).WithName("GetAccountSecurity").Produces<AccountSecurityDto>();
        group.MapPost("{userId}/links", IssueLink).WithName("IssueAccountLink").Produces<IssuedAccountLinkDto>().ProducesProblem(409);
        group.MapDelete("{userId}/links/{linkId}", RevokeLink).WithName("RevokeAccountLink").Produces(204);
        group.MapPut("{userId}/suspension", SetSuspension).WithName("SetAccountSuspension").Produces(204).ProducesProblem(409);
        group.MapDelete("{userId}/sessions", RevokeSessions).WithName("RevokeAccountSessions").Produces(204);
        app.MapPost("/auth/account-link", RedeemLink).AllowAnonymous().WithTags("Auth")
            .WithName("RedeemAccountLink").Produces(204).ProducesProblem(400);
        app.MapGet("/audit", GetAudit).WithName("GetAdminAudit").WithTags("Administration")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin).Produces<AdminAuditPageDto>();
        return app;
    }

    private static async Task<IResult> GetDirectory(string? search, string? role, string? status, string? libraryId, string? cursor,
        IQueryHandler<GetUserDirectoryQuery, UserDirectoryPageDto> handler, CancellationToken ct) =>
        ToResult(await handler.HandleAsync(new(search, role, status, libraryId, cursor), ct), Results.Ok);

    private static async Task<IResult> GetAudit(string? before, string? targetType, string? targetId,
        IQueryHandler<GetAdminAuditQuery, AdminAuditPageDto> handler, CancellationToken ct) =>
        ToResult(await handler.HandleAsync(new(before, targetType, targetId), ct), Results.Ok);

    private static async Task<IResult> GetSecurity(Guid userId, HttpContext context,
        IQueryHandler<GetAccountSecurityQuery, AccountSecurityDto> handler, CancellationToken ct) =>
        ToResult(await handler.HandleAsync(new(userId, Guid.TryParse(context.User.GetClaim(SessionClaims.Id), out var sessionId) ? sessionId : null), ct), Results.Ok);

    private static async Task<IResult> IssueLink(Guid userId, IssueAccountLinkRequest request,
        ICommandHandler<IssueAccountLinkCommand, IssuedAccountLinkDto> handler, HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        return ToResult(await handler.HandleAsync(new(userId, request.Purpose), ct), Results.Ok);
    }

    private static async Task<IResult> RevokeLink(Guid userId, Guid linkId,
        ICommandHandler<RevokeAccountLinkCommand, Success> handler, CancellationToken ct) =>
        ToResult(await handler.HandleAsync(new(userId, linkId), ct), _ => Results.NoContent());

    private static async Task<IResult> SetSuspension(Guid userId, SetAccountSuspensionRequest request,
        ICommandHandler<SetAccountSuspensionCommand, Success> handler, CancellationToken ct) =>
        ToResult(await handler.HandleAsync(new(userId, request.Suspended), ct), _ => Results.NoContent());

    private static async Task<IResult> RevokeSessions(Guid userId, string? sessionId,
        ICommandHandler<RevokeAccountSessionsCommand, Success> handler, CancellationToken ct) =>
        ToResult(await handler.HandleAsync(new(userId, sessionId), ct), _ => Results.NoContent());

    private static async Task<IResult> RedeemLink(RedeemAccountLinkRequest request,
        ICommandHandler<RedeemAccountLinkCommand, Success> handler, CancellationToken ct) =>
        ToResult(await handler.HandleAsync(new(request.Token, request.Password), ct), _ => Results.NoContent());

    private static async Task<IResult> GetAll(
        IQueryHandler<ListUsersQuery, IReadOnlyList<ManagedUser>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListUsersQuery(), cancellationToken);
        return ToResult(result, users => Results.Ok(users.Select(ToDto)));
    }

    private static async Task<IResult> GetById(
        Guid userId,
        IQueryHandler<GetUserByIdQuery, ManagedUser> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetUserByIdQuery(userId), cancellationToken);
        return ToResult(result, user => Results.Ok(ToDto(user)));
    }

    private static async Task<IResult> Create(
        [FromBody] CreateUserRequest request,
        ICommandHandler<CreateUserCommand, ManagedUser> handler,
        CancellationToken cancellationToken)
    {
        var missingFields = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            missingFields["email"] = ["Email is required."];
        }

        if (!request.RequiresActivation && string.IsNullOrWhiteSpace(request.Password))
        {
            missingFields["password"] = ["Password is required."];
        }

        if (missingFields.Count > 0)
        {
            return ProblemResults.ValidationProblem(
                "Users.MissingRequiredFields",
                missingFields,
                "Email and password are required.");
        }

        var result = await handler.HandleAsync(
            new CreateUserCommand(request.Email, request.Password, request.Role, request.LibraryId, request.RequiresActivation),
            cancellationToken);
        return ToResult(
            result,
            user => Results.Created($"/api/users/{user.Id}", ToDto(user)));
    }

    private static async Task<IResult> Update(
        Guid userId,
        [FromBody] UpdateUserRequest request,
        ICommandHandler<UpdateUserCommand, ManagedUser> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new UpdateUserCommand(userId, request.Email, request.NewPassword),
            cancellationToken);
        return ToResult(result, user => Results.Ok(ToDto(user)));
    }

    private static async Task<IResult> Delete(
        Guid userId,
        ICommandHandler<DeleteUserCommand, Deleted> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new DeleteUserCommand(userId), cancellationToken);
        return ToResult(result, _ => Results.NoContent());
    }

    private static async Task<IResult> AssignRole(
        Guid userId,
        [FromBody] AssignRoleRequest request,
        ICommandHandler<AssignUserRoleCommand, ManagedUser> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new AssignUserRoleCommand(userId, request.Role),
            cancellationToken);
        return ToResult(result, user => Results.Ok(ToDto(user)));
    }

    private static async Task<IResult> AssignLibrary(
        Guid userId,
        [FromBody] AssignLibraryRequest request,
        ICommandHandler<AssignUserLibraryCommand, ManagedUser> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new AssignUserLibraryCommand(userId, request.LibraryId),
            cancellationToken);
        return ToResult(result, user => Results.Ok(ToDto(user)));
    }

    private static async Task<IResult> AssignDefaultLibrary(
        AssignDefaultLibraryRequest request,
        ICommandHandler<AssignDefaultLibraryToUsersCommand, int> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new AssignDefaultLibraryToUsersCommand(request.UserIds), cancellationToken);
        return ToResult(result, count => Results.Ok(new AssignDefaultLibraryResponse(count)));
    }

    private static IResult ToResult<T>(ErrorOr<T> result, Func<T, IResult> onSuccess)
    {
        if (!result.IsError)
        {
            return onSuccess(result.Value);
        }

        if (result.Errors.Any(error => error.Type == ErrorType.NotFound))
        {
            return Results.NotFound();
        }

        var first = result.FirstError;
        return first.Code switch
        {
            "Users.InvalidRole" => ProblemResults.ValidationProblem(first.Code, "role", first.Description),
            "Users.InvalidLibraryId" or "Users.LibraryNotFound" =>
                ProblemResults.ValidationProblem(first.Code, "libraryId", first.Description),
            "Users.DuplicateEmail" or
            "Users.CreateFailed" or
            "Users.AssignRoleFailed" or
            "Users.UpdateEmailFailed" or
            "Users.UpdatePasswordFailed" or
            "Users.AssignLibraryFailed" =>
                ProblemResults.Problem(StatusCodes.Status400BadRequest, first.Code, first.Description),
            _ => result.Errors.ToProblem()
        };
    }

    private static UserDto ToDto(ManagedUser user) =>
        new(
            user.Id,
            user.Email,
            user.Roles,
            user.LibraryId.HasValue ? IdCoder.Encode(user.LibraryId.Value) : null,
            user.CreatedAt,
            user.UpdatedAt, user.IsSuspended, user.RequiresActivation, user.LastSignedInAt);
}

public sealed record AssignDefaultLibraryResponse(int UpdatedCount);

public sealed record AssignDefaultLibraryRequest(IReadOnlyList<Guid> UserIds);
