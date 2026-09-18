using System.Reflection;
using ErrorOr;
using Romd.Admin.Application.Users.Commands.AccountSecurity;
using Romd.Admin.Application.Users.Queries.GetAccountSecurity;
using Romd.Admin.Application.Users.Queries.GetAdminAudit;
using Romd.Admin.Application.Users.Queries.GetUserDirectory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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
using Romd.Host.Endpoints;
using Romd.Host.Authorization;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class UserEndpointsTests
{
    private static readonly ManagedUser User = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "user@example.com",
        ["User"],
        7,
        new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero),
        null);

    [Fact]
    public async Task Create_ValidRequest_DelegatesAndPreservesCreatedContract()
    {
        var handler = Substitute.For<ICommandHandler<CreateUserCommand, ManagedUser>>();
        handler.HandleAsync(Arg.Any<CreateUserCommand>(), Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(User));

        var result = await InvokeAsync(
            "Create",
            new CreateUserRequest("user@example.com", "Password123", "User", IdCoder.Encode(7)),
            handler,
            CancellationToken.None);

        var created = result.ShouldBeOfType<Created<UserDto>>();
        created.Location.ShouldBe($"/api/users/{User.Id}");
        created.Value.ShouldNotBeNull();
        created.Value.LibraryId.ShouldBe(IdCoder.Encode(7));
        await handler.Received(1).HandleAsync(
            Arg.Is<CreateUserCommand>(command =>
                command.Email == "user@example.com"
                && command.Password == "Password123"
                && command.Role == "User"
                && command.LibraryId == IdCoder.Encode(7)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_MissingFields_PreservesExactFieldDictionaryWithoutDispatch()
    {
        var handler = Substitute.For<ICommandHandler<CreateUserCommand, ManagedUser>>();

        var result = await InvokeAsync(
            "Create",
            new CreateUserRequest("", "", "User"),
            handler,
            CancellationToken.None);

        var problem = result.ShouldBeAssignableTo<IValueHttpResult<Microsoft.AspNetCore.Mvc.ProblemDetails>>();
        problem.Value.ShouldNotBeNull();
        problem.Value!.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Users.MissingRequiredFields");
        var fields = problem.Value.Extensions[ProblemResults.ErrorsExtensionName]!
            .ShouldBeAssignableTo<IReadOnlyDictionary<string, string[]>>();
        fields.Count.ShouldBe(2);
        fields["email"].ShouldBe(["Email is required."]);
        fields["password"].ShouldBe(["Password is required."]);
        await handler.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
    }

    [Theory]
    [InlineData("Update")]
    [InlineData("AssignRole")]
    [InlineData("AssignLibrary")]
    public async Task UserMutation_NotFound_RemainsBodyless404(string endpoint)
    {
        IResult result = endpoint switch
        {
            "Update" => await InvokeWithNotFoundAsync<UpdateUserCommand>(
                endpoint,
                User.Id,
                new UpdateUserRequest("new@example.com", null)),
            "AssignRole" => await InvokeWithNotFoundAsync<AssignUserRoleCommand>(
                endpoint,
                User.Id,
                new AssignRoleRequest("Manager")),
            _ => await InvokeWithNotFoundAsync<AssignUserLibraryCommand>(
                endpoint,
                User.Id,
                new AssignLibraryRequest(null))
        };

        result.ShouldBeOfType<NotFound>();
        result.ShouldNotBeAssignableTo<IValueHttpResult>();
    }

    [Fact]
    public async Task Delete_NotFound_RemainsBodyless404()
    {
        var handler = Substitute.For<ICommandHandler<DeleteUserCommand, Deleted>>();
        handler.HandleAsync(Arg.Any<DeleteUserCommand>(), Arg.Any<CancellationToken>())
            .Returns(UserErrors.NotFound());

        var result = await InvokeAsync("Delete", User.Id, handler, CancellationToken.None);

        result.ShouldBeOfType<NotFound>();
        result.ShouldNotBeAssignableTo<IValueHttpResult>();
    }

    [Fact]
    public async Task GetById_NotFound_RemainsBodyless404()
    {
        var handler = Substitute.For<IQueryHandler<GetUserByIdQuery, ManagedUser>>();
        handler.HandleAsync(Arg.Any<GetUserByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(UserErrors.NotFound());

        var result = await InvokeAsync("GetById", User.Id, handler, CancellationToken.None);

        result.ShouldBeOfType<NotFound>();
        result.ShouldNotBeAssignableTo<IValueHttpResult>();
    }

    [Fact]
    public async Task AssignLibrary_InvalidId_PreservesLibraryFieldKey()
    {
        var handler = Substitute.For<ICommandHandler<AssignUserLibraryCommand, ManagedUser>>();
        handler.HandleAsync(Arg.Any<AssignUserLibraryCommand>(), Arg.Any<CancellationToken>())
            .Returns(UserErrors.InvalidLibraryId("bad"));

        var result = await InvokeAsync(
            "AssignLibrary",
            User.Id,
            new AssignLibraryRequest("bad"),
            handler,
            CancellationToken.None);

        var problem = result.ShouldBeAssignableTo<IValueHttpResult<Microsoft.AspNetCore.Mvc.ProblemDetails>>();
        problem.Value.ShouldNotBeNull();
        var fields = problem.Value!.Extensions[ProblemResults.ErrorsExtensionName]!
            .ShouldBeAssignableTo<IReadOnlyDictionary<string, string[]>>();
        fields.Keys.ShouldBe(["libraryId"]);
    }

    [Fact]
    public async Task AssignRole_InvalidRole_PreservesRoleFieldKey()
    {
        var handler = Substitute.For<ICommandHandler<AssignUserRoleCommand, ManagedUser>>();
        handler.HandleAsync(Arg.Any<AssignUserRoleCommand>(), Arg.Any<CancellationToken>())
            .Returns(UserErrors.InvalidRole("bad"));

        var result = await InvokeAsync(
            "AssignRole",
            User.Id,
            new AssignRoleRequest("bad"),
            handler,
            CancellationToken.None);

        var problem = result.ShouldBeAssignableTo<IValueHttpResult<Microsoft.AspNetCore.Mvc.ProblemDetails>>();
        problem.Value.ShouldNotBeNull();
        var fields = problem.Value!.Extensions[ProblemResults.ErrorsExtensionName]!
            .ShouldBeAssignableTo<IReadOnlyDictionary<string, string[]>>();
        fields.Keys.ShouldBe(["role"]);
    }

    [Fact]
    public async Task RouteMetadata_PreservesAllEightMethodsNamesAuthorizationStatusesAndBodyless404s()
    {
        var builder = WebApplication.CreateBuilder();
        RegisterRouteDependencies(builder.Services);
        await using var app = builder.Build();
        app.MapUserEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        EndpointExpectation[] expected =
        [
            new("", "GET", "ListUsers", [200]),
            new("{userId}", "GET", "GetUserById", [200, 404]),
            new("", "POST", "CreateUser", [201, 400, 409]),
            new("{userId}", "PUT", "UpdateUser", [200, 400, 404]),
            new("{userId}", "DELETE", "DeleteUser", [204, 404, 409]),
            new("{userId}/role", "PUT", "AssignUserRole", [200, 400, 404]),
            new("{userId}/library", "PUT", "AssignUserLibrary", [200, 400, 404, 409]),
            new("assign-default-library", "POST", "AssignDefaultLibraryToUsers", [200, 409])
        ];

        endpoints.Count.ShouldBe(expected.Length + 8);
        foreach (var name in new[] { "GetUserDirectory", "GetAccountSecurity", "IssueAccountLink", "RevokeAccountLink", "SetAccountSuspension", "RevokeAccountSessions", "GetAdminAudit" })
        {
            endpoints.Single(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name)
                .Metadata.GetOrderedMetadata<IAuthorizeData>().Select(metadata => metadata.Policy)
                .ShouldContain(AuthorizationPolicies.RequireAdmin);
        }
        endpoints.Single(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "RedeemAccountLink")
            .Metadata.GetMetadata<IAllowAnonymous>().ShouldNotBeNull();
        foreach (var item in expected)
        {
            var endpoint = endpoints.Single(candidate =>
                NormalizeRoute(candidate.RoutePattern.RawText) == NormalizeRoute($"/users/{item.Route}")
                && candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(item.HttpMethod) == true);
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName.ShouldBe(item.Name);
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(metadata => metadata.Policy)
                .ShouldContain(AuthorizationPolicies.RequireAdmin);
            endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Select(metadata => metadata.StatusCode)
                .Order()
                .ShouldBe(item.StatusCodes.Order());
        }

        foreach (var endpoint in endpoints.Where(endpoint => endpoint.Metadata
                     .GetOrderedMetadata<IProducesResponseTypeMetadata>()
                     .Any(metadata => metadata.StatusCode == StatusCodes.Status404NotFound)))
        {
            var metadata = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Single(item => item.StatusCode == StatusCodes.Status404NotFound);
            metadata.Type.ShouldBe(typeof(void));
            metadata.ContentTypes.ShouldBeEmpty();
        }
    }

    [Fact]
    public void EndpointDelegates_HaveOnlyTransportValuesMatchingHandlerAndCancellationToken()
    {
        AssertParameters("GetAll", typeof(IQueryHandler<ListUsersQuery, IReadOnlyList<ManagedUser>>), typeof(CancellationToken));
        AssertParameters("GetById", typeof(Guid), typeof(IQueryHandler<GetUserByIdQuery, ManagedUser>), typeof(CancellationToken));
        AssertParameters("Create", typeof(CreateUserRequest), typeof(ICommandHandler<CreateUserCommand, ManagedUser>), typeof(CancellationToken));
        AssertParameters("Update", typeof(Guid), typeof(UpdateUserRequest), typeof(ICommandHandler<UpdateUserCommand, ManagedUser>), typeof(CancellationToken));
        AssertParameters("Delete", typeof(Guid), typeof(ICommandHandler<DeleteUserCommand, Deleted>), typeof(CancellationToken));
        AssertParameters("AssignRole", typeof(Guid), typeof(AssignRoleRequest), typeof(ICommandHandler<AssignUserRoleCommand, ManagedUser>), typeof(CancellationToken));
        AssertParameters("AssignLibrary", typeof(Guid), typeof(AssignLibraryRequest), typeof(ICommandHandler<AssignUserLibraryCommand, ManagedUser>), typeof(CancellationToken));
        AssertParameters("AssignDefaultLibrary", typeof(AssignDefaultLibraryRequest), typeof(ICommandHandler<AssignDefaultLibraryToUsersCommand, int>), typeof(CancellationToken));
    }

    private static async Task<IResult> InvokeWithNotFoundAsync<TCommand>(
        string method,
        Guid userId,
        object request)
        where TCommand : ICommand<ManagedUser>
    {
        var handler = Substitute.For<ICommandHandler<TCommand, ManagedUser>>();
        handler.HandleAsync(Arg.Any<TCommand>(), Arg.Any<CancellationToken>())
            .Returns(UserErrors.NotFound());
        return await InvokeAsync(method, userId, request, handler, CancellationToken.None);
    }

    private static void AssertParameters(string methodName, params Type[] expected)
    {
        var method = typeof(UserEndpoints).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();
        method.GetParameters().Select(parameter => parameter.ParameterType).ShouldBe(expected);
    }

    private static async Task<IResult> InvokeAsync(string methodName, params object?[] arguments)
    {
        var method = typeof(UserEndpoints).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();
        var task = method.Invoke(null, arguments);
        return await task.ShouldBeOfType<Task<IResult>>();
    }

    private static string NormalizeRoute(string? route) =>
        "/" + (route ?? string.Empty).Trim('/');

    private static void RegisterRouteDependencies(IServiceCollection services)
    {
        services.AddScoped(_ => Substitute.For<IQueryHandler<GetUserDirectoryQuery, UserDirectoryPageDto>>());
        services.AddScoped(_ => Substitute.For<IQueryHandler<GetAccountSecurityQuery, AccountSecurityDto>>());
        services.AddScoped(_ => Substitute.For<IQueryHandler<GetAdminAuditQuery, AdminAuditPageDto>>());
        services.AddScoped(_ => Substitute.For<ICommandHandler<IssueAccountLinkCommand, IssuedAccountLinkDto>>());
        services.AddScoped(_ => Substitute.For<ICommandHandler<RedeemAccountLinkCommand, Success>>());
        services.AddScoped(_ => Substitute.For<ICommandHandler<RevokeAccountLinkCommand, Success>>());
        services.AddScoped(_ => Substitute.For<ICommandHandler<RevokeAccountSessionsCommand, Success>>());
        services.AddScoped(_ => Substitute.For<ICommandHandler<SetAccountSuspensionCommand, Success>>());
        services.AddScoped(_ => Substitute.For<IQueryHandler<ListUsersQuery, IReadOnlyList<ManagedUser>>>());
        services.AddScoped(_ => Substitute.For<IQueryHandler<GetUserByIdQuery, ManagedUser>>());
        services.AddScoped(_ => Substitute.For<ICommandHandler<CreateUserCommand, ManagedUser>>());
        services.AddScoped(_ => Substitute.For<ICommandHandler<UpdateUserCommand, ManagedUser>>());
        services.AddScoped(_ => Substitute.For<ICommandHandler<DeleteUserCommand, Deleted>>());
        services.AddScoped(_ => Substitute.For<ICommandHandler<AssignUserRoleCommand, ManagedUser>>());
        services.AddScoped(_ => Substitute.For<ICommandHandler<AssignUserLibraryCommand, ManagedUser>>());
        services.AddScoped(_ => Substitute.For<ICommandHandler<AssignDefaultLibraryToUsersCommand, int>>());
    }

    private sealed record EndpointExpectation(
        string Route,
        string HttpMethod,
        string Name,
        int[] StatusCodes);
}
