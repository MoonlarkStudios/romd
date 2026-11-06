using Romd.Infrastructure.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore;
using Romd.Domain.Identity;
using Romd.Infrastructure.Identity;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Romd.Infrastructure.Tests.Identity;

public sealed class RomdOpenIddictAccountServiceTests
{
    [Theory]
    [InlineData(PasswordHasherCompatibilityMode.IdentityV2)]
    [InlineData(PasswordHasherCompatibilityMode.IdentityV3)]
    public async Task ValidateCredentialsAsync_OlderHash_UpgradesWithoutEndingSessions(PasswordHasherCompatibilityMode mode)
    {
        await using var database = await CreateDatabaseAsync();
        var user = await SeedCredentialUserAsync(database.Context);
        var olderHasher = new PasswordHasher<RomdUser>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = mode,
            IterationCount = 1
        }));
        string oldHash = olderHasher.HashPassword(user, "Password123");
        await database.Context.Users.Where(item => item.Id == user.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.PasswordHash, oldHash));
        var sessions = new AccountSessionStore(database.Context, TimeProvider.System);
        var session = await sessions.StartCommittedAsync(database.Context, TimeProvider.System, user.Id, RomdOpenIddictClients.Console, "Console", user.SecurityStamp!, default);
        var service = CreateService(database.Context);

        var signedIn = await service.ValidateCredentialsAsync(await service.FindByLoginAsync(user.Email!), "Password123");

        signedIn.ShouldNotBeNull();
        var persisted = await database.Context.Users.AsNoTracking().SingleAsync();
        persisted.PasswordHash.ShouldNotBe(oldHash);
        new PasswordHasher<RomdUser>().VerifyHashedPassword(persisted, persisted.PasswordHash!, "Password123")
            .ShouldBe(PasswordVerificationResult.Success);
        persisted.SecurityStamp.ShouldBe(user.SecurityStamp);
        persisted.ConcurrencyStamp.ShouldNotBe(user.ConcurrencyStamp);
        persisted.LastSignedInAt.ShouldNotBeNull();
        signedIn.PasswordHash.ShouldBe(persisted.PasswordHash);
        (await sessions.ValidateAsync(user.Id, session.Id, user.SecurityStamp!, default)).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("password", true)]
    [InlineData("password", false)]
    [InlineData("suspension", true)]
    [InlineData("stamp", true)]
    [InlineData("activation", true)]
    [InlineData("lockout", true)]
    public async Task ValidateCredentialsAsync_AccountChangesDuringVerification_DoesNotOverwriteOrSignIn(string change, bool rehash)
    {
        await using var database = await CreateDatabaseAsync();
        var user = await SeedCredentialUserAsync(database.Context);
        await using var competing = database.CreateContext();
        var hasher = Substitute.For<IPasswordHasher<RomdUser>>();
        hasher.HashPassword(Arg.Any<RomdUser>(), "Password123").Returns("upgraded-hash");
        hasher.VerifyHashedPassword(Arg.Any<RomdUser>(), Arg.Any<string>(), "Password123").Returns(_ =>
        {
            var query = competing.Users.Where(item => item.Id == user.Id);
            switch (change)
            {
                case "password":
                    query.ExecuteUpdate(set => set.SetProperty(item => item.PasswordHash, "reset-hash")
                        .SetProperty(item => item.SecurityStamp, "reset-stamp").SetProperty(item => item.ConcurrencyStamp, "reset-version"));
                    break;
                case "suspension":
                    query.ExecuteUpdate(set => set.SetProperty(item => item.IsSuspended, true));
                    break;
                case "stamp":
                    query.ExecuteUpdate(set => set.SetProperty(item => item.SecurityStamp, "changed-stamp"));
                    break;
                case "activation":
                    query.ExecuteUpdate(set => set.SetProperty(item => item.RequiresActivation, true));
                    break;
                case "lockout":
                    query.ExecuteUpdate(set => set.SetProperty(item => item.LockoutEnabled, true)
                        .SetProperty(item => item.LockoutEnd, DateTimeOffset.UtcNow.AddHours(1)));
                    break;
            }
            return rehash ? PasswordVerificationResult.SuccessRehashNeeded : PasswordVerificationResult.Success;
        });
        var service = new RomdOpenIddictAccountService(database.Context, hasher, Options.Create(new IdentityOptions()));

        (await service.ValidateCredentialsAsync(await service.FindByLoginAsync(user.Email!), "Password123")).ShouldBeNull();

        var persisted = await database.Context.Users.AsNoTracking().SingleAsync();
        persisted.PasswordHash.ShouldBe(change == "password" ? "reset-hash" : user.PasswordHash);
        persisted.ConcurrencyStamp.ShouldBe(change == "password" ? "reset-version" : user.ConcurrencyStamp);
        persisted.LastSignedInAt.ShouldBeNull();
    }

    [Fact]
    public async Task ValidateCredentialsAsync_WrongPassword_DoesNotUpgradeHashOrRecordSignIn()
    {
        await using var database = await CreateDatabaseAsync();
        var user = await SeedCredentialUserAsync(database.Context);
        var service = CreateService(database.Context);

        (await service.ValidateCredentialsAsync(await service.FindByLoginAsync(user.Email!), "WrongPassword123")).ShouldBeNull();

        var persisted = await database.Context.Users.AsNoTracking().SingleAsync();
        persisted.PasswordHash.ShouldBe(user.PasswordHash);
        persisted.LastSignedInAt.ShouldBeNull();
    }

    private static async Task<RomdUser> SeedCredentialUserAsync(RomdDbContext db)
    {
        var user = RomdUser.Create("account", "account@example.com");
        user.NormalizedUserName = "ACCOUNT";
        user.NormalizedEmail = "ACCOUNT@EXAMPLE.COM";
        user.SecurityStamp = "account-stamp";
        user.ConcurrencyStamp = "account-version";
        user.PasswordHash = new PasswordHasher<RomdUser>().HashPassword(user, "Password123");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task CreatePrincipalAsync_ConsumerUserOnAdminClient_ReturnsNull()
    {
        await using var database = await CreateDatabaseAsync();
        var user = await SeedUserAsync(database.Context, nameof(RomdRoleType.User));
        var service = CreateService(database.Context);

        var principal = await service.CreatePrincipalAsync(
            user,
            ["openid"],
            "romd-admin",
            RomdOpenIddictClients.AdminSpa);

        principal.ShouldBeNull();
    }

    [Fact]
    public async Task CreatePrincipalAsync_StaffUserOnAdminClient_ReturnsAdminAudiencePrincipal()
    {
        await using var database = await CreateDatabaseAsync();
        var user = await SeedUserAsync(database.Context, nameof(RomdRoleType.Contributor));
        var service = CreateService(database.Context);

        var principal = await service.CreatePrincipalAsync(
            user,
            ["openid"],
            "romd-admin",
            RomdOpenIddictClients.AdminSpa);

        principal.ShouldNotBeNull();
        principal.GetClaim(Claims.Subject).ShouldBe(user.Id.ToString());
        principal.GetResources().ShouldContain("romd-admin");
    }

    [Fact]
    public async Task CreatePrincipalAsync_ConsumerUserOnConsumerClient_ReturnsConsumerAudiencePrincipal()
    {
        await using var database = await CreateDatabaseAsync();
        var user = await SeedUserAsync(database.Context, nameof(RomdRoleType.User));
        var service = CreateService(database.Context);

        var principal = await service.CreatePrincipalAsync(
            user,
            ["openid"],
            "romd-consumer",
            RomdOpenIddictClients.ConsumerSpa);

        principal.ShouldNotBeNull();
        principal.GetResources().ShouldContain("romd-consumer");
    }

    [Fact]
    public async Task CreatePrincipalAsync_StaffUserOnConsumerClient_IsAdmitted()
    {
        await using var database = await CreateDatabaseAsync();
        var user = await SeedUserAsync(database.Context, nameof(RomdRoleType.Admin));
        var service = CreateService(database.Context);

        // Staff may use the consumer surface (one-way crossover); a plain User may never use admin.
        var principal = await service.CreatePrincipalAsync(
            user,
            ["openid"],
            "romd-consumer",
            RomdOpenIddictClients.ConsumerSpa);

        principal.ShouldNotBeNull();
    }

    private static RomdOpenIddictAccountService CreateService(RomdDbContext context) =>
        new(context, new PasswordHasher<RomdUser>(), Options.Create(new IdentityOptions()));

    private static async Task<RomdUser> SeedUserAsync(RomdDbContext context, string roleName)
    {
        var role = new RomdIdentityRole
        {
            Id = Guid.NewGuid(),
            Name = roleName,
            NormalizedName = roleName.ToUpperInvariant()
        };
        var user = RomdUser.Create($"{roleName}-user", $"{roleName}@localhost");
        user.NormalizedUserName = user.UserName!.ToUpperInvariant();
        user.NormalizedEmail = user.Email!.ToUpperInvariant();

        context.Set<RomdIdentityRole>().Add(role);
        context.Users.Add(user);
        context.Set<IdentityUserRole<Guid>>().Add(new IdentityUserRole<Guid>
        {
            UserId = user.Id,
            RoleId = role.Id
        });
        await context.SaveChangesAsync();

        return user;
    }

    private static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var connection = PostgreSqlTestDatabase.Create();

        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(connection.ConnectionString)
            .UseOpenIddict()
            .Options;

        var context = new RomdDbContext(options);
        await context.Database.MigrateAsync();

        return new TestDatabase(connection, context);
    }

    private sealed class TestDatabase(PostgreSqlTestDatabase connection, RomdDbContext context) : IAsyncDisposable
    {
        public RomdDbContext Context { get; } = context;
        public RomdDbContext CreateContext() => connection.CreateContext();

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
