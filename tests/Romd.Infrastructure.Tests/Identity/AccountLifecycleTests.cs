using Romd.Infrastructure.Tests.Helpers;
using ErrorOr;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Users;
using Romd.Admin.Application.Users.Commands.AssignUserRole;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Users;
using Romd.Domain.Identity;
using Romd.Infrastructure.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Identity;

public sealed class AccountLifecycleTests
{
    [Fact]
    public async Task Activation_SingleUse_StoresOnlyHashAndEnablesSignIn()
    {
        await using var fixture = await Fixture.CreateAsync();
        Guid id = await fixture.CreateAccountAsync("pending@example.com", "User", true);
        var issued = await fixture.RunAsync(service => service.IssueLinkAsync(id, "activation", default));
        issued.IsError.ShouldBeFalse();
        await using (var read = fixture.Database.CreateContext())
        {
            var link = await read.AccountLinks.SingleAsync();
            link.TokenHash.ShouldNotBe(issued.Value.Token);
            (await read.Users.SingleAsync(user => user.Id == id)).RequiresActivation.ShouldBeTrue();
            (await read.AdminAuditEvents.Select(item => item.Changes).ToListAsync()).ShouldAllBe(change => !change.Contains(issued.Value.Token));
        }
        var accepted = await fixture.RunAsync(service => service.RedeemLinkAsync(issued.Value.Token, "NewPassword123!", default));
        accepted.IsError.ShouldBeFalse();
        var replay = await fixture.RunAsync(service => service.RedeemLinkAsync(issued.Value.Token, "OtherPassword123!", default));
        replay.FirstError.Code.ShouldBe("Account.InvalidLink");
        await using var scope = fixture.Provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();
        var account = await users.FindByIdAsync(id.ToString());
        account!.RequiresActivation.ShouldBeFalse();
        account.EmailConfirmed.ShouldBeTrue();
        (await users.CheckPasswordAsync(account, "NewPassword123!")).ShouldBeTrue();
    }

    [Fact]
    public async Task Recovery_NewLinkRevokesOld_ExpiredAndRevokedLinksFail()
    {
        await using var fixture = await Fixture.CreateAsync();
        Guid id = await fixture.CreateAccountAsync("member@example.com", "User");
        var first = await fixture.RunAsync(service => service.IssueLinkAsync(id, "recovery", default));
        var second = await fixture.RunAsync(service => service.IssueLinkAsync(id, "recovery", default));
        (await fixture.RunAsync(service => service.RedeemLinkAsync(first.Value.Token, "NewPassword123!", default))).IsError.ShouldBeTrue();
        fixture.Clock.Now += TimeSpan.FromHours(2);
        (await fixture.RunAsync(service => service.RedeemLinkAsync(second.Value.Token, "NewPassword123!", default))).IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Suspension_RevokesTokensAndLinks_RecoveryCannotReactivate()
    {
        await using var fixture = await Fixture.CreateAsync();
        Guid id = await fixture.CreateAccountAsync("member@example.com", "User");
        var issued = await fixture.RunAsync(service => service.IssueLinkAsync(id, "recovery", default));
        string tokenId;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            var token = await tokens.CreateAsync(new OpenIddictTokenDescriptor
            {
                Subject = id.ToString(),
                Status = OpenIddictConstants.Statuses.Valid,
                Type = OpenIddictConstants.TokenTypeIdentifiers.RefreshToken,
                CreationDate = fixture.Clock.Now,
                ExpirationDate = fixture.Clock.Now.AddDays(1)
            });
            tokenId = (await tokens.GetIdAsync(token))!;
        }
        (await fixture.RunAsync(service => service.SetSuspensionAsync(id, true, default))).IsError.ShouldBeFalse();
        (await fixture.RunAsync(service => service.RedeemLinkAsync(issued.Value.Token, "NewPassword123!", default))).IsError.ShouldBeTrue();
        await using var verify = fixture.Provider.CreateAsyncScope();
        var manager = verify.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        (await manager.GetStatusAsync((await manager.FindByIdAsync(tokenId))!)).ShouldBe(OpenIddictConstants.Statuses.Revoked);
        (await verify.ServiceProvider.GetRequiredService<RomdDbContext>().Users.SingleAsync(user => user.Id == id)).IsSuspended.ShouldBeTrue();
    }

    [Fact]
    public async Task RoleChanges_ConcurrentDemotions_PreserveOneViableAdmin()
    {
        await using var fixture = await Fixture.CreateAsync();
        Guid first = await fixture.CreateAccountAsync("first@example.com", "Admin");
        Guid second = await fixture.CreateAccountAsync("second@example.com", "Admin");
        async Task<ErrorOr<ManagedUser>> Demote(Guid id)
        {
            await using var scope = fixture.Provider.CreateAsyncScope();
            var handler = new AssignUserRoleCommandHandler(scope.ServiceProvider.GetRequiredService<IUserAdministration>(),
                new EfUnitOfWork(scope.ServiceProvider.GetRequiredService<RomdDbContext>()), fixture.Clock,
                NullLogger<AssignUserRoleCommandHandler>.Instance);
            return await handler.HandleAsync(new(id, "User"));
        }
        var results = await Task.WhenAll(Demote(first), Demote(second));
        results.Count(result => !result.IsError).ShouldBe(1);
        results.Single(result => result.IsError).FirstError.Code.ShouldBe("Users.ProtectedUser");
        await using var read = fixture.Database.CreateContext();
        (await (from membership in read.UserRoles
                join role in read.Roles on membership.RoleId equals role.Id
                where role.Name == "Admin"
                select membership.UserId).CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task SystemActor_RejectsLifecycleChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        (await fixture.RunAsync(service => service.IssueLinkAsync(SystemActor.UserId, "recovery", default))).FirstError.Code.ShouldBe("Users.ProtectedUser");
        (await fixture.RunAsync(service => service.SetSuspensionAsync(SystemActor.UserId, false, default))).FirstError.Code.ShouldBe("Users.ProtectedUser");
        (await fixture.RunAsync(service => service.RevokeSessionsAsync(SystemActor.UserId, null, default))).FirstError.Code.ShouldBe("Users.ProtectedUser");
    }

    [Fact]
    public async Task FailedPasswordPolicy_DoesNotConsumeLinkOrPersistAudit()
    {
        await using var fixture = await Fixture.CreateAsync();
        Guid id = await fixture.CreateAccountAsync("member@example.com", "User", true);
        var issued = await fixture.RunAsync(service => service.IssueLinkAsync(id, "activation", default));
        var failed = await fixture.RunAsync(service => service.RedeemLinkAsync(issued.Value.Token, "x", default));
        failed.IsError.ShouldBeTrue();
        await using var read = fixture.Database.CreateContext();
        (await read.AccountLinks.SingleAsync()).RedeemedAt.ShouldBeNull();
        (await read.Users.SingleAsync(user => user.Id == id)).RequiresActivation.ShouldBeTrue();
        (await read.AdminAuditEvents.AnyAsync(item => item.Action == "Account activated")).ShouldBeFalse();
    }

    [Fact]
    public async Task LocalRecovery_SoleAdmin_RevokesCredentialsAndIssuesAuditedSingleUseLink()
    {
        await using var fixture = await Fixture.CreateAsync(systemActor: true);
        Guid id = await fixture.CreateAccountAsync("owner@example.com", "Admin");
        var previous = await fixture.RunAsync(service => service.IssueLinkAsync(id, "recovery", default));
        string originalStamp;
        string tokenId;
        string authorizationId;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            originalStamp = (await db.Users.SingleAsync(user => user.Id == id)).SecurityStamp!;
            await scope.ServiceProvider.GetRequiredService<IAccountSessions>().ShouldBeOfType<AccountSessionStore>()
                .StartCommittedAsync(db, fixture.Clock, id, "romd-admin-spa", "Test browser", originalStamp, default);
            var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var authorization = await authorizations.CreateAsync(new OpenIddictAuthorizationDescriptor
            {
                Subject = id.ToString(),
                Status = OpenIddictConstants.Statuses.Valid,
                Type = OpenIddictConstants.AuthorizationTypes.Permanent
            });
            authorizationId = (await authorizations.GetIdAsync(authorization))!;
            var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            var token = await tokens.CreateAsync(new OpenIddictTokenDescriptor
            {
                Subject = id.ToString(),
                AuthorizationId = authorizationId,
                Status = OpenIddictConstants.Statuses.Valid,
                Type = OpenIddictConstants.TokenTypeIdentifiers.RefreshToken,
                CreationDate = fixture.Clock.Now,
                ExpirationDate = fixture.Clock.Now.AddDays(1)
            });
            tokenId = (await tokens.GetIdAsync(token))!;
        }

        var issued = await fixture.RecoverAsync("OWNER@example.com");
        issued.IsError.ShouldBeFalse();
        issued.Value.UserId.ShouldBe(id);
        issued.Value.ExpiresAt.ShouldBe(fixture.Clock.Now.AddHours(1));
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            (await db.Users.SingleAsync(user => user.Id == id)).SecurityStamp.ShouldNotBe(originalStamp);
            var link = await db.AccountLinks.SingleAsync(item => item.Id == issued.Value.Id);
            link.TokenHash.ShouldNotBe(issued.Value.Token);
            (await db.AccountLinks.SingleAsync(item => item.Id == previous.Value.Id)).RevokedAt.ShouldNotBeNull();
            var sessions = scope.ServiceProvider.GetRequiredService<IAccountSessions>();
            (await sessions.ListAsync(id, default)).ShouldAllBe(session => session.Status == "Revoked");
            var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            (await tokens.GetStatusAsync((await tokens.FindByIdAsync(tokenId))!)).ShouldBe(OpenIddictConstants.Statuses.Revoked);
            var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            (await authorizations.GetStatusAsync((await authorizations.FindByIdAsync(authorizationId))!)).ShouldBe(OpenIddictConstants.Statuses.Revoked);
            var evidence = await db.AdminAuditEvents.SingleAsync(item => item.Action == "Local administrator recovery initiated");
            evidence.ActorId.ShouldBe(SystemActor.UserId);
            evidence.TargetId.ShouldBe(id.ToString());
            (await db.AdminAuditEvents.Select(item => item.Changes).ToListAsync())
                .ShouldAllBe(change => !change.Contains(issued.Value.Token));
            (await db.UserRoles.CountAsync(item => item.UserId == id)).ShouldBe(1);
        }

        (await fixture.RunAsync(service => service.RedeemLinkAsync(issued.Value.Token, "x", default))).IsError.ShouldBeTrue();
        (await fixture.RunAsync(service => service.RedeemLinkAsync(issued.Value.Token, "RecoveredPassword123!", default))).IsError.ShouldBeFalse();
        (await fixture.RunAsync(service => service.RedeemLinkAsync(issued.Value.Token, "ReplayPassword123!", default))).IsError.ShouldBeTrue();
        await using var verify = fixture.Provider.CreateAsyncScope();
        var users = verify.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();
        (await users.CheckPasswordAsync((await users.FindByIdAsync(id.ToString()))!, "RecoveredPassword123!")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("User", false, false, "Account.AdminNotFound")]
    [InlineData("Admin", true, false, "Account.PendingActivation")]
    [InlineData("Admin", false, true, "Account.Suspended")]
    public async Task LocalRecovery_IneligibleAccount_PreservesCredentialsAndDoesNotIssueLink(
        string role, bool pending, bool suspended, string code)
    {
        await using var fixture = await Fixture.CreateAsync(systemActor: true);
        Guid id = await fixture.CreateAccountAsync("ineligible@example.com", role, pending);
        await using (var db = fixture.Database.CreateContext())
            await db.Users.Where(user => user.Id == id).ExecuteUpdateAsync(set => set.SetProperty(user => user.IsSuspended, suspended));
        await using var before = fixture.Database.CreateContext();
        string? stamp = (await before.Users.SingleAsync(user => user.Id == id)).SecurityStamp;

        (await fixture.RecoverAsync("ineligible@example.com")).FirstError.Code.ShouldBe(code);

        await using var read = fixture.Database.CreateContext();
        (await read.AccountLinks.AnyAsync()).ShouldBeFalse();
        (await read.Users.SingleAsync(user => user.Id == id)).SecurityStamp.ShouldBe(stamp);
        (await read.AdminAuditEvents.AnyAsync(item => item.Action == "Local administrator recovery initiated")).ShouldBeFalse();
    }

    [Fact]
    public async Task LocalRecovery_UnknownAccountAndHumanActor_RefuseWithoutCreatingAccounts()
    {
        await using var system = await Fixture.CreateAsync(systemActor: true);
        (await system.RecoverAsync("missing@example.com")).FirstError.Code.ShouldBe("Account.AdminNotFound");
        await using var read = system.Database.CreateContext();
        (await read.Users.AnyAsync()).ShouldBeFalse();

        await using var human = await Fixture.CreateAsync();
        await human.CreateAccountAsync("owner@example.com", "Admin");
        (await human.RecoverAsync("owner@example.com")).FirstError.Code.ShouldBe("Account.LocalRecoveryOnly");
    }

    [Fact]
    public async Task LocalRecovery_UncommittedTransaction_RollsBackLinkStampAndAudit()
    {
        await using var fixture = await Fixture.CreateAsync(systemActor: true);
        Guid id = await fixture.CreateAccountAsync("owner@example.com", "Admin");
        await using var before = fixture.Database.CreateContext();
        string? stamp = (await before.Users.SingleAsync(user => user.Id == id)).SecurityStamp;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();
            (await scope.ServiceProvider.GetRequiredService<LocalAdminRecovery>().IssueAsync("owner@example.com", default))
                .IsError.ShouldBeFalse();
            await db.SaveChangesAsync();
        }
        await using var read = fixture.Database.CreateContext();
        (await read.AccountLinks.AnyAsync()).ShouldBeFalse();
        (await read.Users.SingleAsync(user => user.Id == id)).SecurityStamp.ShouldBe(stamp);
        (await read.AdminAuditEvents.AnyAsync(item => item.Action == "Local administrator recovery initiated")).ShouldBeFalse();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Actor : IAuditContext
    {
        public Guid ActorId => Guid.Parse("10000000-0000-0000-0000-000000000001");
        public bool IsSystem => false;
    }
    private sealed class SystemAudit : IAuditContext
    {
        public Guid ActorId => SystemActor.UserId;
        public bool IsSystem => true;
    }
    private sealed class Fixture(PostgreSqlTestDatabase database, ServiceProvider provider, Clock clock) : IAsyncDisposable
    {
        public PostgreSqlTestDatabase Database => database;
        public ServiceProvider Provider => provider;
        public Clock Clock => clock;
        public static async Task<Fixture> CreateAsync(bool systemActor = false)
        {
            var database = PostgreSqlTestDatabase.Create();
            var services = new ServiceCollection();
            var clock = new Clock();
            services.AddLogging();
            services.AddDataProtection();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IAuditContext>(systemActor ? new SystemAudit() : new Actor());
            services.AddScoped<AuditInterceptor>();
            services.AddDbContext<RomdDbContext>((sp, options) => options.UseNpgsql(database.ConnectionString)
                .UseOpenIddict().AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
            services.AddIdentityCore<RomdUser>(RomdIdentityOptions.Configure).AddRoles<RomdIdentityRole>()
                .AddEntityFrameworkStores<RomdDbContext>().AddDefaultTokenProviders();
            services.AddOpenIddict().AddCore(options => { options.DisableEntityCaching(); options.UseEntityFrameworkCore().UseDbContext<RomdDbContext>(); });
            services.AddScoped<Romd.Application.Common.Security.IAccountSessions, Romd.Persistence.Identity.AccountSessionStore>();
            services.AddScoped<IUserAdministration, UserAdministration>();
            services.AddScoped<IAccountLifecycle, AccountLifecycle>();
            services.AddScoped<LocalAdminRecovery>();
            var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            db.Roles.AddRange(Enum.GetNames<RomdRoleType>().Select(name => new RomdIdentityRole(name)));
            await db.SaveChangesAsync();
            return new(database, provider, clock);
        }
        public async Task<Guid> CreateAccountAsync(string email, string role, bool pending = false)
        {
            await using var scope = provider.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserAdministration>();
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();
            var created = pending ? await users.CreatePendingAsync(email, null, clock.Now) : await users.CreateAsync(email, "InitialPassword123!", null, clock.Now);
            created.IsError.ShouldBeFalse();
            (await users.AddRoleAsync(created.Value.Id, Enum.Parse<RomdRoleType>(role))).IsError.ShouldBeFalse();
            await transaction.CommitAsync();
            return created.Value.Id;
        }
        public async Task<ErrorOr<T>> RunAsync<T>(Func<IAccountLifecycle, Task<ErrorOr<T>>> operation)
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            return await new EfUnitOfWork(db).ExecuteInTransactionAsync<T>(_ => operation(scope.ServiceProvider.GetRequiredService<IAccountLifecycle>()), NullLogger.Instance);
        }
        public async Task<ErrorOr<IssuedAccountLinkDto>> RecoverAsync(string email)
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            return await new EfUnitOfWork(db).ExecuteInTransactionAsync<IssuedAccountLinkDto>(
                ct => scope.ServiceProvider.GetRequiredService<LocalAdminRecovery>().IssueAsync(email, ct), NullLogger.Instance);
        }
        public async ValueTask DisposeAsync() { await provider.DisposeAsync(); await database.DisposeAsync(); }
    }
}
