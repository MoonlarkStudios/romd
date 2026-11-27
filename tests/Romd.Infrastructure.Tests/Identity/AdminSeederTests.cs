using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Domain.Identity;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Identity;

public sealed class AdminSeederTests
{
    [Fact]
    public async Task StartAsync_EmptyDatabaseAndConcurrentStarts_CreatesOneAdministratorWithOneDraft()
    {
        await using var fixture = Fixture.Create();

        await Task.WhenAll(fixture.SeedAsync(), fixture.SeedAsync());
        await fixture.SeedAsync();

        await using var scope = fixture.Provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();
        var admin = (await users.FindByEmailAsync(Fixture.Email))!;
        (await users.IsInRoleAsync(admin, "Admin")).ShouldBeTrue();
        (await users.CheckPasswordAsync(admin, Fixture.Password)).ShouldBeTrue();
        admin.IsSuspended.ShouldBeFalse();
        admin.RequiresActivation.ShouldBeFalse();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        (await db.Users.CountAsync(user => user.Id != SystemActor.UserId)).ShouldBe(1);
        var starter = (await db.Libraries.SingleAsync()).ToDomain();
        starter.Name.ShouldBe("All Titles");
        starter.IsStarter.ShouldBeTrue();
        starter.IsDefault.ShouldBeFalse();
        starter.AwaitsFirstPublication.ShouldBeTrue();
        starter.Configuration.AllowedPlatformIds.ShouldBeEmpty();
        starter.Configuration.IncludeUntrackedTitles.ShouldBeTrue();
        starter.Configuration.ShowMissingGames.ShouldBeFalse();
        starter.Configuration.ContentRatingPolicy.UnknownRatingPolicy.ShouldBe(UnknownMetadataPolicy.Allow);
        admin.LibraryId.ShouldBe(starter.Id);
        (await db.Jobs.OfType<MaterializationJobEntity>().AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task StartAsync_ExistingAudiencesAndExplicitAssignments_PreservesConfigurationAndDefault()
    {
        await using var fixture = Fixture.Create();
        var now = DateTimeOffset.UtcNow;
        var audience = Library.CreateNew("Private collection", new LibraryConfiguration
        {
            IncludeUntrackedTitles = true,
            ContentRatingPolicy = new ContentRatingPolicy { MaxMinimumAge = 16 }
        }, now);
        audience.MarkMaterialized(12, now);
        var family = Library.CreateNew("Family", new LibraryConfiguration
        {
            ContentRatingPolicy = new ContentRatingPolicy { MaxMinimumAge = 8 }
        }, now);
        family.MarkAsDefault(now);
        var audienceRow = LibraryEntity.FromDomain(audience);
        var familyRow = LibraryEntity.FromDomain(family);
        var admin = RomdUser.Create(Fixture.Email, Fixture.Email);
        var familyMember = RomdUser.Create("family@example.com", "family@example.com");
        await using (var db = fixture.Database.CreateContext())
        {
            db.Libraries.AddRange(audienceRow, familyRow);
            await db.SaveChangesAsync();
            admin.LibraryId = audienceRow.Id;
            familyMember.LibraryId = familyRow.Id;
            db.Users.AddRange(admin, familyMember);
            await db.SaveChangesAsync();
        }

        await fixture.SeedAsync();
        await fixture.SeedAsync();

        await using var verify = fixture.Database.CreateContext();
        var libraries = await verify.Libraries.ToDictionaryAsync(library => library.Id);
        libraries.Count.ShouldBe(2);
        libraries[audienceRow.Id].ConfigurationJson.ShouldBe(audienceRow.ConfigurationJson);
        libraries[audienceRow.Id].ItemCount.ShouldBe(12);
        libraries[familyRow.Id].ConfigurationJson.ShouldBe(familyRow.ConfigurationJson);
        libraries.Values.Where(library => library.IsDefault).ShouldHaveSingleItem().Id.ShouldBe(familyRow.Id);
        (await verify.Users.SingleAsync(user => user.Id == admin.Id)).LibraryId.ShouldBe(audienceRow.Id);
        (await verify.Users.SingleAsync(user => user.Id == familyMember.Id)).LibraryId.ShouldBe(familyRow.Id);
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

        // Operators may remove the bootstrap password once the first account exists.
        fixture.Options.DefaultAdminPassword.Returns((string?)null);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ChangeMe123!")]
    [InlineData("LocalOnly123")]
    [InlineData("LocalDev123")]
    public async Task StartAsync_ProductionWithoutSecureBootstrapPassword_RefusesEmptyInstallation(string? password)
    {
        await using var fixture = Fixture.Create();
        fixture.Options.DefaultAdminPassword.Returns(password);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => fixture.SeedAsync());

        error.Message.ShouldContain("initial administrator");
        await using var db = fixture.Database.CreateContext();
        (await db.Users.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task StartAsync_NonProductionWithoutBootstrapPassword_UsesDevelopmentFallback()
    {
        await using var fixture = Fixture.Create(environmentName: "Testing");
        fixture.Options.DefaultAdminPassword.Returns((string?)null);

        await fixture.SeedAsync();

        await using var scope = fixture.Provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();
        var admin = await users.FindByEmailAsync(Fixture.Email);
        admin.ShouldNotBeNull();
        (await users.CheckPasswordAsync(admin, "ChangeMe123!")).ShouldBeTrue();
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
            (await verify.Libraries.AnyAsync()).ShouldBeFalse();
        }
        validator.Enabled = false;
        fixture.Options.DefaultAdminPassword.Returns(Fixture.Password);
        await fixture.SeedAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();
        var admin = (await users.FindByEmailAsync(Fixture.Email))!;
        (await users.IsInRoleAsync(admin, "Admin")).ShouldBeTrue();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        admin.LibraryId.ShouldBe((await db.Libraries.SingleAsync()).Id);
    }

    [Fact]
    public async Task StartAsync_CreatesAdministrator_LogsOpaqueIdentityWithoutEmailOrPassword()
    {
        await using var fixture = Fixture.Create();

        await fixture.SeedAsync();

        await using var scope = fixture.Provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();
        var admin = (await users.FindByEmailAsync(Fixture.Email))!;
        fixture.Logs.ShouldContain(message => message.Contains(admin.Id.ToString(), StringComparison.Ordinal));
        fixture.Logs.ShouldAllBe(message => !message.Contains(Fixture.Email, StringComparison.Ordinal)
            && !message.Contains(Fixture.Password, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_IdentityFailure_ReportsCodesWithoutPrivateDescriptions(bool failRole)
    {
        await using var fixture = Fixture.Create(new PrivateDescriptionValidator(failRole));

        var error = await Should.ThrowAsync<InvalidOperationException>(() => fixture.SeedAsync());

        error.Message.ShouldContain("BootstrapRejected");
        error.Message.ShouldNotContain(Fixture.Email);
        error.Message.ShouldNotContain(Fixture.Password);
        fixture.Logs.ShouldContain(message => message.Contains("BootstrapRejected", StringComparison.Ordinal));
        fixture.Logs.ShouldAllBe(message => !message.Contains(Fixture.Email, StringComparison.Ordinal)
            && !message.Contains(Fixture.Password, StringComparison.Ordinal));
        await using var db = fixture.Database.CreateContext();
        (await db.Users.AnyAsync()).ShouldBeFalse();
    }

    private sealed class PrivateDescriptionValidator(bool failRole) : IUserValidator<RomdUser>
    {
        private int _adminValidations;
        public Task<IdentityResult> ValidateAsync(UserManager<RomdUser> manager, RomdUser user) =>
            Task.FromResult(user.Email == Fixture.Email && ++_adminValidations == (failRole ? 2 : 1)
                ? IdentityResult.Failed(new IdentityError
                {
                    Code = "BootstrapRejected",
                    Description = $"Private validation details: {Fixture.Email}, {Fixture.Password}"
                })
                : IdentityResult.Success);
    }

    private sealed class CapturingLogger(List<string> messages) : ILogger<AdminSeeder>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Add(formatter(state, exception));
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
        public List<string> Logs { get; } = [];

        public static Fixture Create(IUserValidator<RomdUser>? validator = null, string environmentName = "Production")
        {
            var database = PostgreSqlTestDatabase.Create();
            var options = Substitute.For<IRomdOptions>();
            options.DefaultAdminEmail.Returns(Email);
            options.DefaultAdminPassword.Returns(Password);
            var services = new ServiceCollection();
            var environment = Substitute.For<IHostEnvironment>();
            environment.EnvironmentName.Returns(environmentName);
            services.AddSingleton(environment);
            services.AddLogging();
            services.AddSingleton(options);
            services.AddDbContext<RomdDbContext>(builder => builder.UseNpgsql(database.ConnectionString).UseOpenIddict());
            services.AddIdentityCore<RomdUser>(RomdIdentityOptions.Configure).AddRoles<RomdIdentityRole>()
                .AddEntityFrameworkStores<RomdDbContext>().AddPasswordValidator<RomdPasswordLengthValidator>();
            if (validator is not null)
                services.AddSingleton(validator);
            return new Fixture(database, services.BuildServiceProvider(), options);
        }

        public Task SeedAsync() => new AdminSeeder(provider, TimeProvider.System, new CapturingLogger(Logs),
            provider.GetRequiredService<IHostEnvironment>()).StartAsync(default);
        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
