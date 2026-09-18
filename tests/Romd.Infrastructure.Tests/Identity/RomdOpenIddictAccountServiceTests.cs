using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
