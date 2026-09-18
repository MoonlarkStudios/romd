using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Users;
using Romd.Admin.Application.Users.Commands.AssignDefaultLibraryToUsers;
using Romd.Admin.Application.Users.Commands.AssignUserLibrary;
using Romd.Admin.Application.Users.Commands.AssignUserRole;
using Romd.Admin.Application.Users.Commands.CreateUser;
using Romd.Admin.Application.Users.Commands.DeleteUser;
using Romd.Admin.Application.Users.Commands.UpdateUser;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Identity;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Identity;

public sealed class UserAdministrationAtomicityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListAsync_UsesOneFlatQueryAndReturnsAllRoles()
    {
        await using var database = await TestDatabase.CreateAsync();
        var second = await database.SeedUserAsync("b@example.com", RomdRoleType.Manager);
        var first = await database.SeedUserAsync("a@example.com", RomdRoleType.User);
        database.Interceptor.Reset();
        await using var scope = database.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAdministration>();

        var result = await users.ListAsync();

        result.Select(user => user.Id).ShouldBe([first.Id, second.Id]);
        result[0].Roles.ShouldBe(["User"]);
        result[1].Roles.ShouldBe(["Manager"]);
        database.Interceptor.UserSelectCount.ShouldBe(1);
    }

    [Fact]
    public async Task Create_WithRole_CommitsUserAndRoleTogether()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var scope = database.CreateScope();
        var handler = CreateHandler(scope, new EfUnitOfWork(scope.ServiceProvider.GetRequiredService<RomdDbContext>()));

        var result = await handler.HandleAsync(
            new CreateUserCommand("new@example.com", "Password123", "Manager", null));

        result.IsError.ShouldBeFalse();
        await using var read = database.CreateReadContext();
        var user = await read.Users.SingleAsync(entity => entity.Email == "new@example.com");
        (await RolesForAsync(read, user.Id)).ShouldBe(["Manager"]);
    }

    [Fact]
    public async Task Create_DefaultAndExplicitLibrarySelection_PersistsSelectedLibraryIds()
    {
        await using var database = await TestDatabase.CreateAsync();
        int defaultLibraryId = await database.SeedLibraryAsync("Default", isDefault: true);
        int explicitLibraryId = await database.SeedLibraryAsync("Explicit");
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var handler = CreateHandler(scope, new EfUnitOfWork(context));

        var defaultResult = await handler.HandleAsync(
            new CreateUserCommand("default@example.com", "Password123", "User", null));
        var explicitResult = await handler.HandleAsync(
            new CreateUserCommand(
                "explicit@example.com",
                "Password123",
                "User",
                IdCoder.Encode(explicitLibraryId)));

        defaultResult.IsError.ShouldBeFalse();
        explicitResult.IsError.ShouldBeFalse();
        await using var read = database.CreateReadContext();
        (await read.Users.SingleAsync(user => user.Email == "default@example.com")).LibraryId
            .ShouldBe(defaultLibraryId);
        (await read.Users.SingleAsync(user => user.Email == "explicit@example.com")).LibraryId
            .ShouldBe(explicitLibraryId);
    }

    [Fact]
    public async Task Create_RoleFailureAfterUserFlush_RollsBackUserAndRole()
    {
        var validator = new FailOnUserValidationCall(2);
        await using var database = await TestDatabase.CreateAsync(userValidator: validator);
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var handler = CreateHandler(scope, new EfUnitOfWork(context));

        var result = await handler.HandleAsync(
            new CreateUserCommand("new@example.com", "Password123", "User", null));

        result.FirstError.Code.ShouldBe("Users.AssignRoleFailed");
        result.FirstError.Description.ShouldNotContain("provider-secret");
        await using var read = database.CreateReadContext();
        (await read.Users.AnyAsync(entity => entity.Email == "new@example.com")).ShouldBeFalse();
        (await read.UserRoles.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Create_FinalCommitFailureFlushesThenRollsBackUserAndRole()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        IUnitOfWork inner = new EfUnitOfWork(context);
        var handler = CreateHandler(scope, new FailingCommitUnitOfWork(inner));

        var result = await handler.HandleAsync(
            new CreateUserCommand("new@example.com", "Password123", "Admin", null));

        result.FirstError.Code.ShouldBe("Users.PersistenceFailed");
        await using var read = database.CreateReadContext();
        (await read.Users.AnyAsync(entity => entity.Email == "new@example.com")).ShouldBeFalse();
        (await read.UserRoles.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Create_CancelledAfterIdentityInsert_RollsBackUser()
    {
        using var cancellation = new CancellationTokenSource();
        await using var database = await TestDatabase.CreateAsync(
            cancelOnSql: cancellation,
            cancelSqlFragment: "INSERT INTO romd.\"AspNetUsers\"");
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var handler = CreateHandler(scope, new EfUnitOfWork(context));

        await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(
            new CreateUserCommand("new@example.com", "Password123", "User", null),
            cancellation.Token));

        await using var read = database.CreateReadContext();
        (await read.Users.AnyAsync(entity => entity.Email == "new@example.com")).ShouldBeFalse();
    }

    [Fact]
    public async Task ReplaceRole_AddFailureAfterRemoval_RollsBackOriginalRoleAndTimestamp()
    {
        var validator = new FailOnUserValidationCall(2);
        await using var database = await TestDatabase.CreateAsync(userValidator: validator);
        var seeded = await database.SeedUserAsync("role@example.com", RomdRoleType.User);
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<IUserAdministration>();
        var handler = new AssignUserRoleCommandHandler(
            users,
            new EfUnitOfWork(context),
            new FixedTimeProvider(Now),
            NullLogger<AssignUserRoleCommandHandler>.Instance);

        var result = await handler.HandleAsync(new AssignUserRoleCommand(seeded.Id, "Manager"));

        result.FirstError.Code.ShouldBe("Users.AssignRoleFailed");
        await using var read = database.CreateReadContext();
        (await RolesForAsync(read, seeded.Id)).ShouldBe(["User"]);
        (await read.Users.SingleAsync(user => user.Id == seeded.Id)).UpdatedAt.ShouldBeNull();
    }

    [Fact]
    public async Task ReplaceRole_FinalCommitFailure_RollsBackValidReplacement()
    {
        await using var database = await TestDatabase.CreateAsync();
        var seeded = await database.SeedUserAsync("role@example.com", RomdRoleType.User);
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        IUnitOfWork inner = new EfUnitOfWork(context);
        var handler = new AssignUserRoleCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new FailingCommitUnitOfWork(inner),
            new FixedTimeProvider(Now),
            NullLogger<AssignUserRoleCommandHandler>.Instance);

        var result = await handler.HandleAsync(new AssignUserRoleCommand(seeded.Id, "Manager"));

        result.FirstError.Code.ShouldBe("Users.PersistenceFailed");
        await using var read = database.CreateReadContext();
        (await RolesForAsync(read, seeded.Id)).ShouldBe(["User"]);
        (await read.Users.SingleAsync(user => user.Id == seeded.Id)).UpdatedAt.ShouldBeNull();
    }

    [Fact]
    public async Task ReplaceRole_CancelledAfterNewRoleFlush_RollsBackOriginalRole()
    {
        using var cancellation = new CancellationTokenSource();
        await using var database = await TestDatabase.CreateAsync();
        var seeded = await database.SeedUserAsync("role@example.com", RomdRoleType.User);
        database.Interceptor.ArmCancellation(cancellation, "INSERT INTO romd.\"AspNetUserRoles\"");
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var handler = new AssignUserRoleCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new EfUnitOfWork(context),
            new FixedTimeProvider(Now),
            NullLogger<AssignUserRoleCommandHandler>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.HandleAsync(new AssignUserRoleCommand(seeded.Id, "Manager"), cancellation.Token));

        await using var read = database.CreateReadContext();
        (await RolesForAsync(read, seeded.Id)).ShouldBe(["User"]);
        (await read.Users.SingleAsync(user => user.Id == seeded.Id)).UpdatedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Update_UserNameFailureAfterEmailFlush_RollsBackNormalizedValuesStampsAndTimestamp()
    {
        var validator = new FailOnUserValidationCall(2);
        await using var database = await TestDatabase.CreateAsync(userValidator: validator);
        var seeded = await database.SeedUserAsync("old@example.com", RomdRoleType.User);
        var before = await database.ReadUserAsync(seeded.Id);
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var handler = new UpdateUserCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new EfUnitOfWork(context),
            new FixedTimeProvider(Now),
            NullLogger<UpdateUserCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new UpdateUserCommand(seeded.Id, "new@example.com", null));

        result.FirstError.Code.ShouldBe("Users.UpdateEmailFailed");
        var after = await database.ReadUserAsync(seeded.Id);
        AssertIdentityStateEqual(before, after);
    }

    [Fact]
    public async Task Update_PasswordFailureAfterEmailAndUserNameFlushes_RollsBackAllIdentityState()
    {
        await using var database = await TestDatabase.CreateAsync(passwordValidator: new AlwaysFailPasswordValidator());
        var seeded = await database.SeedUserAsync("old@example.com", RomdRoleType.User);
        var before = await database.ReadUserAsync(seeded.Id);
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var handler = new UpdateUserCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new EfUnitOfWork(context),
            new FixedTimeProvider(Now),
            NullLogger<UpdateUserCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new UpdateUserCommand(seeded.Id, "new@example.com", "Password456"));

        result.FirstError.Code.ShouldBe("Users.UpdatePasswordFailed");
        result.FirstError.Description.ShouldNotContain("provider-secret");
        var after = await database.ReadUserAsync(seeded.Id);
        AssertIdentityStateEqual(before, after);
    }

    [Fact]
    public async Task Update_ValidEmailAndPassword_FinalCommitFailure_RollsBackAllIdentityState()
    {
        await using var database = await TestDatabase.CreateAsync();
        var seeded = await database.SeedUserAsync("old@example.com", RomdRoleType.User);
        var before = await database.ReadUserAsync(seeded.Id);
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        IUnitOfWork inner = new EfUnitOfWork(context);
        var handler = new UpdateUserCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new FailingCommitUnitOfWork(inner),
            new FixedTimeProvider(Now),
            NullLogger<UpdateUserCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new UpdateUserCommand(seeded.Id, "new@example.com", "Password456"));

        result.FirstError.Code.ShouldBe("Users.PersistenceFailed");
        AssertIdentityStateEqual(before, await database.ReadUserAsync(seeded.Id));
    }

    [Fact]
    public async Task Update_CancelledAfterPasswordFlush_RollsBackEmailPasswordAndStamps()
    {
        using var cancellation = new CancellationTokenSource();
        await using var database = await TestDatabase.CreateAsync();
        var seeded = await database.SeedUserAsync("old@example.com", RomdRoleType.User);
        var before = await database.ReadUserAsync(seeded.Id);
        database.Interceptor.ArmCancellation(
            cancellation,
            "UPDATE romd.\"AspNetUsers\"",
            matchingCommandNumber: 3);
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var handler = new UpdateUserCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new EfUnitOfWork(context),
            new FixedTimeProvider(Now),
            NullLogger<UpdateUserCommandHandler>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(
            new UpdateUserCommand(seeded.Id, "new@example.com", "Password456"),
            cancellation.Token));

        AssertIdentityStateEqual(before, await database.ReadUserAsync(seeded.Id));
    }

    [Fact]
    public async Task Update_NoEmailOrPassword_StillPersistsUpdatedTimestamp()
    {
        await using var database = await TestDatabase.CreateAsync();
        var seeded = await database.SeedUserAsync("unchanged@example.com", RomdRoleType.User);
        var before = await database.ReadUserAsync(seeded.Id);
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var handler = new UpdateUserCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new EfUnitOfWork(context),
            new FixedTimeProvider(Now),
            NullLogger<UpdateUserCommandHandler>.Instance);

        var result = await handler.HandleAsync(new UpdateUserCommand(seeded.Id, null, null));

        result.IsError.ShouldBeFalse();
        var after = await database.ReadUserAsync(seeded.Id);
        after.Email.ShouldBe(before.Email);
        after.NormalizedEmail.ShouldBe(before.NormalizedEmail);
        after.UserName.ShouldBe(before.UserName);
        after.NormalizedUserName.ShouldBe(before.NormalizedUserName);
        after.PasswordHash.ShouldBe(before.PasswordHash);
        after.SecurityStamp.ShouldBe(before.SecurityStamp);
        after.UpdatedAt.ShouldBe(Now);
        after.ConcurrencyStamp.ShouldNotBe(before.ConcurrencyStamp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_CommitOutcome_KeepsIdentitySettingsAndJobLinkAtomic(bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync();
        var seeded = await database.SeedUserAsync("delete@example.com", RomdRoleType.User);
        Guid jobId = await database.SeedUserDependentsAsync(seeded.Id);
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        IUnitOfWork inner = new EfUnitOfWork(context);
        var currentUser = new CurrentUserStub(Guid.NewGuid());
        var handler = new DeleteUserCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            currentUser,
            failCommit ? new FailingCommitUnitOfWork(inner) : inner,
            NullLogger<DeleteUserCommandHandler>.Instance);

        var result = await handler.HandleAsync(new DeleteUserCommand(seeded.Id));

        result.IsError.ShouldBe(failCommit);
        await using var read = database.CreateReadContext();
        (await read.Users.AnyAsync(user => user.Id == seeded.Id)).ShouldBe(failCommit);
        (await read.UserRoles.AnyAsync(userRole => userRole.UserId == seeded.Id)).ShouldBe(failCommit);
        (await read.ConsumerUserSettings.AnyAsync(settings => settings.UserId == seeded.Id)).ShouldBe(failCommit);
        (await read.Jobs.SingleAsync(job => job.Id == jobId)).CreatedByUserId.ShouldBe(failCommit ? seeded.Id : null);
    }

    [Fact]
    public async Task Delete_CancelledAfterIdentityDelete_RollsBackIdentitySettingsAndJobLink()
    {
        using var cancellation = new CancellationTokenSource();
        await using var database = await TestDatabase.CreateAsync(
            cancelOnSql: cancellation,
            cancelSqlFragment: "DELETE FROM romd.\"AspNetUsers\"");
        var seeded = await database.SeedUserAsync("delete@example.com", RomdRoleType.User);
        Guid jobId = await database.SeedUserDependentsAsync(seeded.Id);
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var handler = new DeleteUserCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new CurrentUserStub(Guid.NewGuid()),
            new EfUnitOfWork(context),
            NullLogger<DeleteUserCommandHandler>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.HandleAsync(new DeleteUserCommand(seeded.Id), cancellation.Token));

        await using var read = database.CreateReadContext();
        (await read.Users.AnyAsync(user => user.Id == seeded.Id)).ShouldBeTrue();
        (await read.UserRoles.AnyAsync(userRole => userRole.UserId == seeded.Id)).ShouldBeTrue();
        (await read.ConsumerUserSettings.AnyAsync(settings => settings.UserId == seeded.Id)).ShouldBeTrue();
        (await read.Jobs.SingleAsync(job => job.Id == jobId)).CreatedByUserId.ShouldBe(seeded.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssignLibrary_CommitOutcome_KeepsLibraryTimestampAndStampAtomic(bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync();
        int libraryId = await database.SeedLibraryAsync("Curated");
        var seeded = await database.SeedUserAsync("library@example.com", RomdRoleType.User);
        var before = await database.ReadUserAsync(seeded.Id);
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        IUnitOfWork inner = new EfUnitOfWork(context);
        var handler = new AssignUserLibraryCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new LibraryRepository(context),
            failCommit ? new FailingCommitUnitOfWork(inner) : inner,
            new FixedTimeProvider(Now),
            NullLogger<AssignUserLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            new AssignUserLibraryCommand(seeded.Id, IdCoder.Encode(libraryId)));

        result.IsError.ShouldBe(failCommit);
        var after = await database.ReadUserAsync(seeded.Id);
        if (failCommit)
        {
            AssertIdentityStateEqual(before, after);
        }
        else
        {
            after.LibraryId.ShouldBe(libraryId);
            after.UpdatedAt.ShouldBe(Now);
            after.ConcurrencyStamp.ShouldNotBe(before.ConcurrencyStamp);
        }
    }

    [Fact]
    public async Task AssignLibrary_CancelledAfterIdentityFlush_RollsBackLibraryTimestampAndStamp()
    {
        using var cancellation = new CancellationTokenSource();
        await using var database = await TestDatabase.CreateAsync();
        int libraryId = await database.SeedLibraryAsync("Curated");
        var seeded = await database.SeedUserAsync("library@example.com", RomdRoleType.User);
        var before = await database.ReadUserAsync(seeded.Id);
        database.Interceptor.ArmCancellation(cancellation, "UPDATE romd.\"AspNetUsers\"");
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var handler = new AssignUserLibraryCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new LibraryRepository(context),
            new EfUnitOfWork(context),
            new FixedTimeProvider(Now),
            NullLogger<AssignUserLibraryCommandHandler>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(
            new AssignUserLibraryCommand(seeded.Id, IdCoder.Encode(libraryId)),
            cancellation.Token));

        AssertIdentityStateEqual(before, await database.ReadUserAsync(seeded.Id));
    }

    [Fact]
    public async Task AssignLibrary_NullLibrary_UnassignsExistingLibrary()
    {
        await using var database = await TestDatabase.CreateAsync();
        int libraryId = await database.SeedLibraryAsync("Curated");
        var seeded = await database.SeedUserAsync("library@example.com", RomdRoleType.User, libraryId);
        var before = await database.ReadUserAsync(seeded.Id);
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var handler = new AssignUserLibraryCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new LibraryRepository(context),
            new EfUnitOfWork(context),
            new FixedTimeProvider(Now),
            NullLogger<AssignUserLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(new AssignUserLibraryCommand(seeded.Id, null));

        result.IsError.ShouldBeFalse();
        var after = await database.ReadUserAsync(seeded.Id);
        after.LibraryId.ShouldBeNull();
        after.UpdatedAt.ShouldBe(Now);
        after.ConcurrencyStamp.ShouldNotBe(before.ConcurrencyStamp);
    }

    [Fact]
    public async Task AssignLibrary_EmptyLibraryId_ReturnsInvalidWithoutMutation()
    {
        await using var database = await TestDatabase.CreateAsync();
        int libraryId = await database.SeedLibraryAsync("Curated");
        var seeded = await database.SeedUserAsync("library@example.com", RomdRoleType.User, libraryId);
        var before = await database.ReadUserAsync(seeded.Id);
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var handler = new AssignUserLibraryCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new LibraryRepository(context),
            new EfUnitOfWork(context),
            new FixedTimeProvider(Now),
            NullLogger<AssignUserLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(new AssignUserLibraryCommand(seeded.Id, string.Empty));

        result.FirstError.Code.ShouldBe("Users.InvalidLibraryId");
        AssertIdentityStateEqual(before, await database.ReadUserAsync(seeded.Id));
    }

    [Fact]
    public async Task AssignDefault_UpdatesOnlySelectedUnassignedHumansAndReturnsExactCount()
    {
        await using var database = await TestDatabase.CreateAsync();
        int defaultLibraryId = await database.SeedLibraryAsync("Default", isDefault: true);
        int otherLibraryId = await database.SeedLibraryAsync("Other");
        var first = await database.SeedUserAsync("a@example.com", RomdRoleType.User);
        var second = await database.SeedUserAsync("b@example.com", RomdRoleType.User);
        var system = await database.SeedUserAsync(
            SystemActor.Email,
            RomdRoleType.Admin,
            userId: SystemActor.UserId);
        var assigned = await database.SeedUserAsync("c@example.com", RomdRoleType.User, otherLibraryId);
        var stamps = new Dictionary<Guid, string?>
        {
            [first.Id] = first.ConcurrencyStamp,
            [second.Id] = second.ConcurrencyStamp,
            [system.Id] = system.ConcurrencyStamp,
            [assigned.Id] = assigned.ConcurrencyStamp
        };
        database.Interceptor.Reset();
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var handler = new AssignDefaultLibraryToUsersCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new LibraryRepository(context),
            new EfUnitOfWork(context),
            new FixedTimeProvider(Now),
            NullLogger<AssignDefaultLibraryToUsersCommandHandler>.Instance);

        var result = await handler.HandleAsync(new AssignDefaultLibraryToUsersCommand([first.Id, second.Id, assigned.Id]));

        result.Value.ShouldBe(2);
        database.Interceptor.UserUpdateCount.ShouldBe(2);
        await using var read = database.CreateReadContext();
        var users = await read.Users.OrderBy(user => user.Email).ToListAsync();
        users[0].LibraryId.ShouldBe(defaultLibraryId);
        users[1].LibraryId.ShouldBe(defaultLibraryId);
        users[0].UpdatedAt.ShouldBe(Now);
        users[1].UpdatedAt.ShouldBe(Now);
        users[0].ConcurrencyStamp.ShouldNotBe(stamps[users[0].Id]);
        users[1].ConcurrencyStamp.ShouldNotBe(stamps[users[1].Id]);
        var systemAfter = users.Single(user => user.Id == SystemActor.UserId);
        systemAfter.LibraryId.ShouldBeNull();
        systemAfter.UpdatedAt.ShouldBeNull();
        systemAfter.ConcurrencyStamp.ShouldBe(stamps[systemAfter.Id]);
        var assignedAfter = users.Single(user => user.Id == assigned.Id);
        assignedAfter.LibraryId.ShouldBe(otherLibraryId);
        assignedAfter.UpdatedAt.ShouldBeNull();
        assignedAfter.ConcurrencyStamp.ShouldBe(stamps[assignedAfter.Id]);
    }

    [Fact]
    public async Task AssignDefault_FinalCommitFailure_RollsBackSetBasedUpdate()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SeedLibraryAsync("Default", isDefault: true);
        var seeded = await database.SeedUserAsync("a@example.com", RomdRoleType.User);
        var before = await database.ReadUserAsync(seeded.Id);
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        IUnitOfWork inner = new EfUnitOfWork(context);
        var handler = new AssignDefaultLibraryToUsersCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new LibraryRepository(context),
            new FailingCommitUnitOfWork(inner),
            new FixedTimeProvider(Now),
            NullLogger<AssignDefaultLibraryToUsersCommandHandler>.Instance);

        var result = await handler.HandleAsync(new AssignDefaultLibraryToUsersCommand([seeded.Id]));

        result.FirstError.Code.ShouldBe("Users.PersistenceFailed");
        AssertIdentityStateEqual(before, await database.ReadUserAsync(seeded.Id));
    }

    [Fact]
    public async Task AssignDefault_CancelledAfterSetBasedUpdate_RollsBackUpdate()
    {
        using var cancellation = new CancellationTokenSource();
        await using var database = await TestDatabase.CreateAsync(
            cancelOnSql: cancellation,
            cancelSqlFragment: "UPDATE romd.\"AspNetUsers\"");
        await database.SeedLibraryAsync("Default", isDefault: true);
        var seeded = await database.SeedUserAsync("a@example.com", RomdRoleType.User);
        var before = await database.ReadUserAsync(seeded.Id);
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var handler = new AssignDefaultLibraryToUsersCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new LibraryRepository(context),
            new EfUnitOfWork(context),
            new FixedTimeProvider(Now),
            NullLogger<AssignDefaultLibraryToUsersCommandHandler>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.HandleAsync(new AssignDefaultLibraryToUsersCommand([seeded.Id]), cancellation.Token));

        AssertIdentityStateEqual(before, await database.ReadUserAsync(seeded.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssignDefault_MissingOrInvalidDefault_DoesNotUpdateUsers(bool invalid)
    {
        await using var database = await TestDatabase.CreateAsync();
        if (invalid)
        {
            await database.SeedLibraryAsync("Invalid", isDefault: true, isValid: false);
        }

        var seeded = await database.SeedUserAsync("a@example.com", RomdRoleType.User);
        var before = await database.ReadUserAsync(seeded.Id);
        database.Interceptor.Reset();
        await using var scope = database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var handler = new AssignDefaultLibraryToUsersCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new LibraryRepository(context),
            new EfUnitOfWork(context),
            new FixedTimeProvider(Now),
            NullLogger<AssignDefaultLibraryToUsersCommandHandler>.Instance);

        var result = await handler.HandleAsync(new AssignDefaultLibraryToUsersCommand([seeded.Id]));

        result.FirstError.Code.ShouldBe(invalid ? "Users.LibraryConfigurationInvalid" : "Users.NoDefaultLibrary");
        database.Interceptor.UserUpdateCount.ShouldBe(0);
        AssertIdentityStateEqual(before, await database.ReadUserAsync(seeded.Id));
    }

    private static CreateUserCommandHandler CreateHandler(IServiceScope scope, IUnitOfWork unitOfWork)
    {
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        return new CreateUserCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
            new LibraryRepository(context),
            unitOfWork,
            new FixedTimeProvider(Now),
            NullLogger<CreateUserCommandHandler>.Instance);
    }

    private static async Task<IReadOnlyList<string>> RolesForAsync(RomdDbContext context, Guid userId) =>
        await (from userRole in context.UserRoles
            join role in context.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == userId
            orderby role.Name
            select role.Name!).ToListAsync();

    private static void AssertIdentityStateEqual(RomdUser expected, RomdUser actual)
    {
        actual.Email.ShouldBe(expected.Email);
        actual.NormalizedEmail.ShouldBe(expected.NormalizedEmail);
        actual.UserName.ShouldBe(expected.UserName);
        actual.NormalizedUserName.ShouldBe(expected.NormalizedUserName);
        actual.PasswordHash.ShouldBe(expected.PasswordHash);
        actual.SecurityStamp.ShouldBe(expected.SecurityStamp);
        actual.ConcurrencyStamp.ShouldBe(expected.ConcurrencyStamp);
        actual.LibraryId.ShouldBe(expected.LibraryId);
        actual.UpdatedAt.ShouldBe(expected.UpdatedAt);
    }

    private sealed class SessionAuditActor : Romd.Application.Common.Security.IAuditContext
    {
        public Guid ActorId => Guid.Empty;
        public bool IsSystem => true;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly PostgreSqlTestDatabase _connection;
        private readonly ServiceProvider _provider;
        private readonly DbContextOptions<RomdDbContext> _readOptions;

        private TestDatabase(
            PostgreSqlTestDatabase connection,
            ServiceProvider provider,
            DbContextOptions<RomdDbContext> readOptions,
            SqlCaptureInterceptor interceptor)
        {
            _connection = connection;
            _provider = provider;
            _readOptions = readOptions;
            Interceptor = interceptor;
        }

        public SqlCaptureInterceptor Interceptor { get; }

        public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

        public RomdDbContext CreateReadContext() => new(_readOptions);

        public static async Task<TestDatabase> CreateAsync(
            IUserValidator<RomdUser>? userValidator = null,
            IPasswordValidator<RomdUser>? passwordValidator = null,
            CancellationTokenSource? cancelOnSql = null,
            string? cancelSqlFragment = null)
        {
            var connection = PostgreSqlTestDatabase.Create();
            var interceptor = new SqlCaptureInterceptor(cancelOnSql, cancelSqlFragment);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            services.AddSingleton<Romd.Application.Common.Security.IAuditContext, SessionAuditActor>();
            services.AddDataProtection();
            services.AddDbContext<RomdDbContext>(options => options
                .UseNpgsql(connection.ConnectionString).UseOpenIddict()
                .AddInterceptors(interceptor));
            var identity = services.AddIdentityCore<RomdUser>(RomdIdentityOptions.Configure)
                .AddRoles<RomdIdentityRole>()
                .AddEntityFrameworkStores<RomdDbContext>()
                .AddDefaultTokenProviders();
            _ = identity;
            if (userValidator is not null)
            {
                services.AddSingleton(userValidator);
            }

            if (passwordValidator is not null)
            {
                services.AddSingleton(passwordValidator);
            }

            services.AddOpenIddict().AddCore(options => options.UseEntityFrameworkCore().UseDbContext<RomdDbContext>());
            services.AddScoped<Romd.Application.Common.Security.IAccountSessions, Romd.Persistence.Identity.AccountSessionStore>();
            services.AddScoped<IUserAdministration, UserAdministration>();
            var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
                context.Roles.AddRange(Enum.GetNames<RomdRoleType>().Select(name => new RomdIdentityRole(name)));
                await context.SaveChangesAsync();
            }

            var readOptions = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(connection.ConnectionString)
                .Options;
            interceptor.Reset();
            return new TestDatabase(connection, provider, readOptions, interceptor);
        }

        public async Task<RomdUser> SeedUserAsync(
            string email,
            RomdRoleType role,
            int? libraryId = null,
            Guid? userId = null)
        {
            await using var context = CreateReadContext();
            var user = new RomdUser
            {
                Id = userId ?? Guid.NewGuid(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                EmailConfirmed = true,
                PasswordHash = new PasswordHasher<RomdUser>().HashPassword(null!, "Password123"),
                SecurityStamp = Guid.NewGuid().ToString(),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
                LibraryId = libraryId,
                CreatedAt = Now.AddDays(-1)
            };
            var roleEntity = await context.Roles.SingleAsync(entity => entity.Name == role.ToString());
            context.Users.Add(user);
            context.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = roleEntity.Id });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            return user;
        }

        public async Task<int> SeedLibraryAsync(string name, bool isDefault = false, bool isValid = true)
        {
            await using var context = CreateReadContext();
            var entity = new LibraryEntity
            {
                Name = name,
                ConfigurationJson = JsonSerializer.Serialize(new LibraryConfiguration()),
                ConfigurationState = (isValid ? LibraryConfigurationState.Valid : LibraryConfigurationState.Invalid).ToString(),
                ConfigurationError = isValid ? null : "Injected invalid configuration.",
                IsDefault = isDefault,
                NeedsMaterialization = false,
                CreatedAt = Now.AddDays(-1),
                CreatedByUserId = Guid.Empty
            };
            context.Libraries.Add(entity);
            await context.SaveChangesAsync();
            return entity.Id;
        }

        public async Task<Guid> SeedUserDependentsAsync(Guid userId)
        {
            await using var context = CreateReadContext();
            context.ConsumerUserSettings.Add(new ConsumerUserSettingsEntity
            {
                UserId = userId,
                CreatedAt = Now.AddDays(-1)
            });
            var job = UploadJob.Create("upload.zip", createdByUserId: userId);
            var entity = UploadJobEntity.FromDomain(job);
            context.Jobs.Add(entity);
            await context.SaveChangesAsync();
            return entity.Id;
        }

        public async Task<RomdUser> ReadUserAsync(Guid userId)
        {
            await using var context = CreateReadContext();
            return await context.Users.SingleAsync(user => user.Id == userId);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FailOnUserValidationCall(int failureCall) : IUserValidator<RomdUser>
    {
        private int _calls;

        public Task<IdentityResult> ValidateAsync(UserManager<RomdUser> manager, RomdUser user)
        {
            _calls++;
            return Task.FromResult(_calls == failureCall
                ? IdentityResult.Failed(new IdentityError { Code = "Injected", Description = "provider-secret" })
                : IdentityResult.Success);
        }
    }

    private sealed class AlwaysFailPasswordValidator : IPasswordValidator<RomdUser>
    {
        public Task<IdentityResult> ValidateAsync(UserManager<RomdUser> manager, RomdUser user, string? password) =>
            Task.FromResult(IdentityResult.Failed(
                new IdentityError { Code = "Injected", Description = "provider-secret" }));
    }

    private sealed class SqlCaptureInterceptor(
        CancellationTokenSource? cancellation,
        string? cancelSqlFragment) : DbCommandInterceptor
    {
        private CancellationTokenSource? _cancellation = cancellation;
        private string? _cancelSqlFragment = cancelSqlFragment;
        private int _cancelMatchingCommandNumber = 1;
        private int _matchingCommands;

        public int UserSelectCount { get; private set; }
        public int UserUpdateCount { get; private set; }

        public void Reset()
        {
            UserSelectCount = 0;
            UserUpdateCount = 0;
            _matchingCommands = 0;
        }

        public void ArmCancellation(
            CancellationTokenSource cancellationSource,
            string sqlFragment,
            int matchingCommandNumber = 1)
        {
            _cancellation = cancellationSource;
            _cancelSqlFragment = sqlFragment;
            _cancelMatchingCommandNumber = matchingCommandNumber;
            _matchingCommands = 0;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            await CancelIfMatchedAsync(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            await CancelIfMatchedAsync(command.CommandText);
            return result;
        }

        private void Count(string sql)
        {
            if (sql.Contains("FROM romd.\"AspNetUsers\"", StringComparison.Ordinal))
            {
                UserSelectCount++;
            }

            if (sql.StartsWith("UPDATE romd.\"AspNetUsers\"", StringComparison.Ordinal))
            {
                UserUpdateCount++;
            }
        }

        private async Task CancelIfMatchedAsync(string sql)
        {
            if (_cancellation is not null
                && _cancelSqlFragment is not null
                && sql.Contains(_cancelSqlFragment, StringComparison.Ordinal))
            {
                _matchingCommands++;
                if (_matchingCommands == _cancelMatchingCommandNumber)
                {
                    await _cancellation.CancelAsync();
                }
            }
        }
    }

    private sealed class FailingCommitUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            inner.FlushAsync(cancellationToken);

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new FailingCommitTransaction(inner, await inner.BeginTransactionAsync(cancellationToken));
    }

    private sealed class FailingCommitTransaction(IUnitOfWork unitOfWork, ITransaction inner) : ITransaction
    {
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await unitOfWork.FlushAsync(cancellationToken);
            await inner.RollbackAsync(CancellationToken.None);
            throw new InvalidOperationException("Injected final commit failure.");
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            inner.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CurrentUserStub(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public string? Email => null;
        public string? UserName => null;
        public IReadOnlyList<string> Roles => [];
        public RomdRoleType Role => RomdRoleType.Admin;
        public int? LibraryId => null;
        public bool IsAuthenticated => true;
        public bool HasRole(RomdRoleType minimumRole) => true;
    }
}
