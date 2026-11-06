using NSubstitute;
using Romd.Admin.Application.Users;
using Romd.Admin.Application.Users.Queries.GetUserById;
using Romd.Admin.Application.Users.Queries.ListUsers;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Users;

public sealed class UserQueryHandlerTests
{
    private static readonly ManagedUser User = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "user@example.com",
        ["User"],
        null,
        DateTimeOffset.UtcNow,
        null);

    [Fact]
    public async Task ListUsers_DelegatesToAdministrationPort()
    {
        var users = Substitute.For<IUserAdministration>();
        users.ListAsync(Arg.Any<CancellationToken>()).Returns([User]);
        var handler = new ListUsersQueryHandler(users);

        var result = await handler.HandleAsync(new ListUsersQuery());

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe([User]);
    }

    [Fact]
    public async Task GetUserById_Found_ReturnsManagedUser()
    {
        var users = Substitute.For<IUserAdministration>();
        users.GetByIdAsync(User.Id, Arg.Any<CancellationToken>()).Returns(User);
        var handler = new GetUserByIdQueryHandler(users);

        var result = await handler.HandleAsync(new GetUserByIdQuery(User.Id));

        result.Value.ShouldBe(User);
    }

    [Fact]
    public async Task GetUserById_Missing_ReturnsNotFound()
    {
        var users = Substitute.For<IUserAdministration>();
        users.GetByIdAsync(User.Id, Arg.Any<CancellationToken>()).Returns((ManagedUser?)null);
        var handler = new GetUserByIdQueryHandler(users);

        var result = await handler.HandleAsync(new GetUserByIdQuery(User.Id));

        result.FirstError.Code.ShouldBe("Users.NotFound");
    }
}
