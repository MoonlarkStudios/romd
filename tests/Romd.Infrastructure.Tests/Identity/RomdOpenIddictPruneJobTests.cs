using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using Romd.Infrastructure.Identity;
using Romd.Persistence;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Identity;

public sealed class RomdOpenIddictPruneJobTests
{
    [Fact]
    public async Task ExecuteAsync_ExpiredTokenAndAuthorization_ArePruned()
    {
        var connection = PostgreSqlTestDatabase.Create();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRomdTestingPersistence(connection.ConnectionString);
        services.AddScoped<RomdOpenIddictPruneJob>();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RomdDbContext>().Database.MigrateAsync();
        }

        var staleDate = DateTime.UtcNow.AddDays(-90);
        string authorizationId = Guid.NewGuid().ToString("N");
        string tokenId = Guid.NewGuid().ToString("N");

        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            dbContext.Add(new OpenIddictEntityFrameworkCoreAuthorization
            {
                Id = authorizationId,
                Status = OpenIddictConstants.Statuses.Revoked,
                Type = OpenIddictConstants.AuthorizationTypes.AdHoc,
                CreationDate = staleDate
            });
            dbContext.Add(new OpenIddictEntityFrameworkCoreToken
            {
                Id = tokenId,
                Status = OpenIddictConstants.Statuses.Valid,
                Type = OpenIddictConstants.TokenTypeHints.RefreshToken,
                CreationDate = staleDate,
                ExpirationDate = staleDate.AddMinutes(15)
            });
            await dbContext.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<RomdOpenIddictPruneJob>();
            await job.ExecuteAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            (await dbContext.Set<OpenIddictEntityFrameworkCoreToken>().CountAsync()).ShouldBe(0);
            (await dbContext.Set<OpenIddictEntityFrameworkCoreAuthorization>().CountAsync()).ShouldBe(0);
        }

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_RecentValidToken_IsRetained()
    {
        var connection = PostgreSqlTestDatabase.Create();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRomdTestingPersistence(connection.ConnectionString);
        services.AddScoped<RomdOpenIddictPruneJob>();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RomdDbContext>().Database.MigrateAsync();
        }

        var now = DateTime.UtcNow;
        string tokenId = Guid.NewGuid().ToString("N");

        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            dbContext.Add(new OpenIddictEntityFrameworkCoreToken
            {
                Id = tokenId,
                Status = OpenIddictConstants.Statuses.Valid,
                Type = OpenIddictConstants.TokenTypeHints.RefreshToken,
                CreationDate = now,
                ExpirationDate = now.AddDays(30)
            });
            await dbContext.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<RomdOpenIddictPruneJob>();
            await job.ExecuteAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            (await dbContext.Set<OpenIddictEntityFrameworkCoreToken>().CountAsync()).ShouldBe(1);
        }

        await connection.DisposeAsync();
    }
}
