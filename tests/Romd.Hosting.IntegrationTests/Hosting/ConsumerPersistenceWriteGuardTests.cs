using ErrorOr;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.EntityFrameworkCore.Models;
using Romd.Application.Common.Configuration;
using Romd.Consumer.Application.Account;
using Romd.Host.Configuration;
using Romd.Hosting;
using Romd.Infrastructure;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class ConsumerPersistenceWriteGuardTests : IDisposable
{
    private const string InitialPassword = "Initial123!";
    private const string UpdatedPassword = "Updated123!";

    private readonly string _tempDataDirectory = Path.Combine(
        Path.GetTempPath(),
        $"romd-consumer-write-guard-{Guid.NewGuid():N}");

    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    [Fact]
    public async Task ConsumerPersistence_AllowsSettingsAndPasswordWrites()
    {
        var user = await SeedUserWithAdminPersistenceAsync();

        await using var provider = CreateConsumerProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var settingsStore = scope.ServiceProvider.GetRequiredService<IConsumerUserSettingsStore>();
            var passwordChanger = scope.ServiceProvider.GetRequiredService<IConsumerPasswordChanger>();
            var settings = await settingsStore.SaveSettingsAsync(
                user.Id,
                new ConsumerAccountSettings("dark"));

            settings.Theme.ShouldBe("dark");

            var passwordResult = await passwordChanger.ChangePasswordAsync(
                user.Id,
                InitialPassword,
                UpdatedPassword);

            passwordResult.Value.ShouldBe(Result.Updated);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var settingsStore = scope.ServiceProvider.GetRequiredService<IConsumerUserSettingsStore>();
            var savedSettings = await settingsStore.GetSettingsAsync(user.Id);
            savedSettings.Theme.ShouldBe("dark");

            var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var persistedUser = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == user.Id);
            persistedUser.ShouldNotBeNull();
            var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<RomdUser>>();
            passwordHasher.VerifyHashedPassword(persistedUser, persistedUser.PasswordHash!, UpdatedPassword)
                .ShouldNotBe(PasswordVerificationResult.Failed);
        }
    }

    [Fact]
    public async Task ConsumerPersistence_AllowsOwnedPlaySessionLifecycleWrites()
    {
        var user = await SeedUserWithAdminPersistenceAsync("activity-consumer", "activity-consumer@romd.test");
        await using var provider = CreateConsumerProvider();
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var now = DateTimeOffset.Parse("2026-09-14T10:00:00Z");
        var session = new PlaySessionEntity
        {
            UserId = user.Id,
            SessionId = Guid.NewGuid(),
            ClientId = "org.example.client",
            TitleId = 1,
            ReleaseId = 2,
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.PlaySessions.Add(session);
        await dbContext.SaveChangesAsync();
        session.EndedAt = now.AddMinutes(2);
        session.ActiveDurationSeconds = 100;
        session.UpdatedAt = now.AddMinutes(2);
        await dbContext.SaveChangesAsync();
        dbContext.PlaySessions.Remove(session);
        await dbContext.SaveChangesAsync();

        (await dbContext.PlaySessions.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ConsumerPersistence_BlocksCatalogWrites()
    {
        await using var provider = CreateConsumerProvider();

        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();

        dbContext.Platforms.Add(new PlatformEntity
        {
            Name = "Consumer Catalog Write",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Consumer Catalog Write", BaseCompactLabel = "Consumer Catalog Write", CanonicalKey = "consumer-catalog-write", ShortName = "consumer-catalog-write",
            Manufacturer = "ROMD"
        });

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await dbContext.SaveChangesAsync());

        exception.Message.ShouldContain("Consumer persistence cannot write entity type 'PlatformEntity' in state 'Added'");
    }

    [Fact]
    public async Task ConsumerPersistence_BlocksUserCreateAndDelete()
    {
        await using var provider = CreateConsumerProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            dbContext.Users.Add(CreateUser("new-consumer", "new-consumer@romd.test", InitialPassword));

            var exception = await Should.ThrowAsync<InvalidOperationException>(
                async () => await dbContext.SaveChangesAsync());

            exception.Message.ShouldContain("Consumer persistence cannot write entity type 'RomdUser' in state 'Added'");
        }

        var user = await SeedUserWithAdminPersistenceAsync("deleted-consumer", "deleted-consumer@romd.test");

        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var persistedUser = await dbContext.Users.AsTracking().SingleAsync(entity => entity.Id == user.Id);
            dbContext.Users.Remove(persistedUser);

            var exception = await Should.ThrowAsync<InvalidOperationException>(
                async () => await dbContext.SaveChangesAsync());

            exception.Message.ShouldContain("Consumer persistence cannot write entity type 'RomdUser' in state 'Deleted'");
        }
    }

    [Theory]
    [InlineData("library-id", nameof(RomdUser.LibraryId))]
    [InlineData("email", nameof(RomdUser.Email))]
    [InlineData("user-name", nameof(RomdUser.UserName))]
    public async Task ConsumerPersistence_BlocksForbiddenUserPropertyWrites(
        string mutation,
        string expectedPropertyName)
    {
        var user = await SeedUserWithAdminPersistenceAsync("profile-consumer", "profile-consumer@romd.test");

        await using var provider = CreateConsumerProvider();
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var persistedUser = await dbContext.Users.AsTracking().SingleAsync(entity => entity.Id == user.Id);
        ApplyForbiddenUserMutation(persistedUser, mutation);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await dbContext.SaveChangesAsync());

        exception.Message.ShouldContain(
            $"Consumer persistence cannot write entity type 'RomdUser' property '{expectedPropertyName}' in state 'Modified'");
    }

    [Theory]
    [InlineData("role", "RomdIdentityRole")]
    [InlineData("user-role", "IdentityUserRole")]
    [InlineData("login", "IdentityUserLogin")]
    [InlineData("token", "IdentityUserToken")]
    [InlineData("claim", "IdentityUserClaim")]
    public async Task ConsumerPersistence_BlocksIdentityRoleLinkTokenAndClaimWrites(
        string entityKind,
        string expectedEntityTypeName)
    {
        await using var provider = CreateConsumerProvider();

        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        AddIdentityEntity(dbContext, entityKind);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await dbContext.SaveChangesAsync());

        exception.Message.ShouldContain(
            $"Consumer persistence cannot write entity type '{expectedEntityTypeName}");
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("token")]
    public async Task ConsumerPersistence_AllowsOpenIddictTokenAndAuthorizationWrites(string entityKind)
    {
        await using var provider = CreateConsumerProvider();

        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        AddOpenIddictEntity(dbContext, entityKind);

        await dbContext.SaveChangesAsync();
    }

    [Theory]
    [InlineData("application", "OpenIddictEntityFrameworkCoreApplication")]
    [InlineData("scope", "OpenIddictEntityFrameworkCoreScope")]
    public async Task ConsumerPersistence_BlocksOpenIddictApplicationAndScopeWrites(
        string entityKind,
        string expectedEntityTypeName)
    {
        await using var provider = CreateConsumerProvider();

        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        AddOpenIddictEntity(dbContext, entityKind);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await dbContext.SaveChangesAsync());

        exception.Message.ShouldContain(
            $"Consumer persistence cannot write entity type '{expectedEntityTypeName}");
    }

    [Fact]
    public async Task AdminPersistence_AllowsCatalogWrites()
    {
        await using var provider = CreateAdminProvider();

        int platformId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var platform = new PlatformEntity
            {
                Name = "Admin Catalog Write",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Admin Catalog Write", BaseCompactLabel = "Admin Catalog Write", CanonicalKey = "admin-catalog-write", ShortName = "admin-catalog-write",
                Manufacturer = "ROMD"
            };

            dbContext.Platforms.Add(platform);
            await dbContext.SaveChangesAsync();

            platformId = platform.Id;
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var platform = await dbContext.Platforms.SingleAsync(entity => entity.Id == platformId);

            platform.Name.ShouldBe("Admin Catalog Write");
        }
    }

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_tempDataDirectory))
        {
            Directory.Delete(_tempDataDirectory, true);
        }
    }

    private ServiceProvider CreateConsumerProvider()
    {
        var services = CreateServices();

        services.AddAuthentication();
        services
            .AddRomdConsumerInfrastructure()
            .AddRomdConsumerIdentity();

        return services.BuildServiceProvider();
    }

    private ServiceProvider CreateAdminProvider()
    {
        var services = CreateServices();

        services.AddRomdAdminPersistence();

        return services.BuildServiceProvider();
    }

    private Task<RomdUser> SeedUserWithAdminPersistenceAsync() =>
        SeedUserWithAdminPersistenceAsync("consumer", "consumer@romd.test");

    private async Task<RomdUser> SeedUserWithAdminPersistenceAsync(string userName, string email)
    {
        await using var provider = CreateAdminProvider();

        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var user = CreateUser(userName, email, InitialPassword);

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        return user;
    }

    private ServiceCollection CreateServices()
    {
        Directory.CreateDirectory(_tempDataDirectory);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}"] = _database.ConnectionString
            })
            .Build());
        services.AddSingleton<IRomdOptions>(new RomdOptions
        {
            DataDirectory = _tempDataDirectory
        });

        return services;
    }

    private static void AddIdentityEntity(RomdDbContext dbContext, string entityKind)
    {
        var userId = Guid.NewGuid();

        switch (entityKind)
        {
            case "role":
                dbContext.Set<RomdIdentityRole>().Add(new RomdIdentityRole("ConsumerWriteGuardRole"));
                break;
            case "user-role":
                dbContext.Set<IdentityUserRole<Guid>>().Add(new IdentityUserRole<Guid>
                {
                    UserId = userId,
                    RoleId = Guid.NewGuid()
                });
                break;
            case "login":
                dbContext.Set<IdentityUserLogin<Guid>>().Add(new IdentityUserLogin<Guid>
                {
                    UserId = userId,
                    LoginProvider = "consumer",
                    ProviderKey = "consumer-key",
                    ProviderDisplayName = "Consumer"
                });
                break;
            case "token":
                dbContext.Set<IdentityUserToken<Guid>>().Add(new IdentityUserToken<Guid>
                {
                    UserId = userId,
                    LoginProvider = "consumer",
                    Name = "refresh",
                    Value = "token"
                });
                break;
            case "claim":
                dbContext.Set<IdentityUserClaim<Guid>>().Add(new IdentityUserClaim<Guid>
                {
                    UserId = userId,
                    ClaimType = "consumer",
                    ClaimValue = "blocked"
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entityKind), entityKind, "Unknown identity entity kind.");
        }
    }

    private static void AddOpenIddictEntity(RomdDbContext dbContext, string entityKind)
    {
        string id = Guid.NewGuid().ToString("N");

        switch (entityKind)
        {
            case "application":
                dbContext.Set<OpenIddictEntityFrameworkCoreApplication>().Add(
                    new OpenIddictEntityFrameworkCoreApplication
                    {
                        Id = id,
                        ClientId = $"romd-console-{id}"
                    });
                break;
            case "authorization":
                dbContext.Set<OpenIddictEntityFrameworkCoreAuthorization>().Add(
                    new OpenIddictEntityFrameworkCoreAuthorization
                    {
                        Id = id
                    });
                break;
            case "scope":
                dbContext.Set<OpenIddictEntityFrameworkCoreScope>().Add(
                    new OpenIddictEntityFrameworkCoreScope
                    {
                        Id = id,
                        Name = $"scope-{id}"
                    });
                break;
            case "token":
                dbContext.Set<OpenIddictEntityFrameworkCoreToken>().Add(
                    new OpenIddictEntityFrameworkCoreToken
                    {
                        Id = id
                    });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entityKind), entityKind, "Unknown OpenIddict entity kind.");
        }
    }

    private static void ApplyForbiddenUserMutation(RomdUser user, string mutation)
    {
        switch (mutation)
        {
            case "library-id":
                user.LibraryId = 123;
                break;
            case "email":
                user.Email = "changed-consumer@romd.test";
                user.NormalizedEmail = "CHANGED-CONSUMER@ROMD.TEST";
                break;
            case "user-name":
                user.UserName = "changed-consumer";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown user mutation.");
        }
    }

    private static RomdUser CreateUser(string userName, string email, string password)
    {
        var user = RomdUser.Create(userName, email);
        var normalizedUserName = userName.ToUpperInvariant();
        var normalizedEmail = email.ToUpperInvariant();

        user.NormalizedUserName = normalizedUserName;
        user.NormalizedEmail = normalizedEmail;
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        user.PasswordHash = new PasswordHasher<RomdUser>().HashPassword(user, password);

        return user;
    }
}
