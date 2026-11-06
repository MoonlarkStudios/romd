using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Domain.Identity;
using Romd.Infrastructure.Identity;
using Romd.Persistence;
using Romd.Persistence.Identity;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Identity;

public sealed class AdminSeederTests
{
    [Fact]
    public async Task StartAsync_EmptyDatabaseAndConcurrentStarts_CreatesOneAdministrator()
    {
        await using var fixture = Fixture.Create();

        await Task.WhenAll(fixture.SeedAsync(), fixture.SeedAsync());

        await using var scope = fixture.Provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();
        var admin = (await users.FindByEmailAsync(Fixture.Email))!;
        (await users.IsInRoleAsync(admin, "Admin")).ShouldBeTrue();
        (await users.CheckPasswordAsync(admin, Fixture.Password)).ShouldBeTrue();
        admin.IsSuspended.ShouldBeFalse();
        admin.RequiresActivation.ShouldBeFalse();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        (await db.Users.CountAsync(user => user.Id != SystemActor.UserId)).ShouldBe(1);
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("suspend")]
    [InlineData("configuration")]
    public async Task StartAsync_InitializedInstallation_DoesNotRecreateOrReactivateAdministrator(string change)
    {
        await using var fixture = Fixture.Create();
        await fixture.SeedAsync();
        Guid originalId;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();
            var original = (await users.FindByEmailAsync(Fixture.Email))!;
            originalId = original.Id;
            if (change == "rename")
            {
                (await users.SetEmailAsync(original, "renamed@example.com")).Succeeded.ShouldBeTrue();
                (await users.SetUserNameAsync(original, "renamed@example.com")).Succeeded.ShouldBeTrue();
            }
            else if (change == "delete")
            {
                var replacement = new RomdUser { UserName = "replacement@example.com", Email = "replacement@example.com" };
                (await users.CreateAsync(replacement, Fixture.Password)).Succeeded.ShouldBeTrue();
                (await users.AddToRoleAsync(replacement, "Admin")).Succeeded.ShouldBeTrue();
                (await users.DeleteAsync(original)).Succeeded.ShouldBeTrue();
            }
            else if (change == "suspend")
            {
                original.IsSuspended = true;
                (await users.UpdateAsync(original)).Succeeded.ShouldBeTrue();
            }
            else
            {
                fixture.Options.DefaultAdminEmail.Returns("changed-bootstrap@example.com");
            }
        }

        await fixture.SeedAsync();

        await using var verify = fixture.Database.CreateContext();
        var humans = await verify.Users.Where(user => user.Id != SystemActor.UserId).ToListAsync();
        humans.Count.ShouldBe(1);
        if (change == "delete")
            humans[0].Id.ShouldNotBe(originalId);
        else
            humans[0].Id.ShouldBe(originalId);
        if (change == "suspend")
            humans[0].IsSuspended.ShouldBeTrue();
        if (change is "rename" or "delete")
            humans.ShouldAllBe(user => user.Email != Fixture.Email);
        if (change == "configuration")
            humans[0].Email.ShouldBe(Fixture.Email);
    }

    [Fact]
    public async Task StartAsync_ExistingNonAdministrator_DoesNotGrantBootstrapPrivileges()
    {
        await using var fixture = Fixture.Create();
        await using (var db = fixture.Database.CreateContext())
        {
            db.Users.Add(new RomdUser { UserName = "existing", Email = Fixture.Email, IsSuspended = true });
            await db.SaveChangesAsync();
        }

        await fixture.SeedAsync();

        await using var verify = fixture.Database.CreateContext();
        (await verify.Users.CountAsync(user => user.Id != SystemActor.UserId)).ShouldBe(1);
        (await verify.UserRoles.AnyAsync()).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_InvalidPasswordOrRoleFailure_RollsBackAndAllowsCleanRetry(bool failRole)
    {
        var validator = new FailRoleUpdateValidator { Enabled = failRole };
        await using var fixture = Fixture.Create(validator);
        if (!failRole)
            fixture.Options.DefaultAdminPassword.Returns("short");

        await Should.ThrowAsync<InvalidOperationException>(() => fixture.SeedAsync());

        await using (var verify = fixture.Database.CreateContext())
        {
            (await verify.Users.AnyAsync()).ShouldBeFalse();
            (await verify.UserRoles.AnyAsync()).ShouldBeFalse();
            (await verify.Roles.AnyAsync()).ShouldBeFalse();
        }
        validator.Enabled = false;
        fixture.Options.DefaultAdminPassword.Returns(Fixture.Password);
        await fixture.SeedAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();
        (await users.IsInRoleAsync((await users.FindByEmailAsync(Fixture.Email))!, "Admin")).ShouldBeTrue();
    }

    private sealed class FailRoleUpdateValidator : IUserValidator<RomdUser>
    {
        private int _adminValidations;
        public bool Enabled { get; set; }
        public Task<IdentityResult> ValidateAsync(UserManager<RomdUser> manager, RomdUser user) =>
            Task.FromResult(Enabled && user.Email == Fixture.Email && ++_adminValidations > 1
                ? IdentityResult.Failed(new IdentityError { Code = "InjectedRoleFailure", Description = "Injected role update failure." })
                : IdentityResult.Success);
    }

    private sealed class Fixture(PostgreSqlTestDatabase database, ServiceProvider provider, IRomdOptions options) : IAsyncDisposable
    {
        public const string Email = "bootstrap@example.com";
        public const string Password = "BootstrapPassword123!";
        public PostgreSqlTestDatabase Database => database;
        public ServiceProvider Provider => provider;
        public IRomdOptions Options => options;

        public static Fixture Create(IUserValidator<RomdUser>? validator = null)
        {
            var database = PostgreSqlTestDatabase.Create();
            var options = Substitute.For<IRomdOptions>();
            options.DefaultAdminEmail.Returns(Email);
            options.DefaultAdminPassword.Returns(Password);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(options);
            services.AddDbContext<RomdDbContext>(builder => builder.UseNpgsql(database.ConnectionString).UseOpenIddict());
            services.AddIdentityCore<RomdUser>(RomdIdentityOptions.Configure).AddRoles<RomdIdentityRole>()
                .AddEntityFrameworkStores<RomdDbContext>().AddPasswordValidator<RomdPasswordLengthValidator>();
            if (validator is not null)
                services.AddSingleton(validator);
            return new Fixture(database, services.BuildServiceProvider(), options);
        }

        public Task SeedAsync() => new AdminSeeder(provider, TimeProvider.System, NullLogger<AdminSeeder>.Instance).StartAsync(default);
        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
