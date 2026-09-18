using ErrorOr;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using OpenIddict.Validation.AspNetCore;
using Romd.Admin.Application.TrackedCollection.Queries.ListSatisfiedTrackedTitles;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.TrackedCollection;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Persistence;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class TrackedCollectionEndpointsTests
{
    private const string TestJwtSecret = "tracked-collection-test-secret-at-least-32-characters";

    [Fact]
    public async Task AdminHost_TrackedCollectionRoutes_AuthenticatedEmptyCollection_ReturnEmptyResponses()
    {
        string dataDirectory = Path.Combine(Path.GetTempPath(), $"romd-tracked-collection-{Guid.NewGuid():N}");

        try
        {
            using var database = PostgreSqlTestDatabase.Create();
            await using var factory = CreateAdminHostFactory(dataDirectory, database.ConnectionString);
            using var client = factory.CreateClient()
                .WithTestUser(Guid.NewGuid(), "admin@localhost", [RomdRoleType.Admin]);
            foreach (string route in new[] { "satisfied", "missing", "upgrades" })
            {
                using var response = await client.GetAsync($"/api/tracked-titles/{route}");
                var body = await response.Content.ReadFromJsonAsync<IReadOnlyList<TrackedCollectionTitleDto>>();

                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                body.ShouldNotBeNull().ShouldBeEmpty();
            }

            using var statsResponse = await client.GetAsync("/api/tracked-titles/stats");
            var stats = await statsResponse.Content.ReadFromJsonAsync<TrackedCollectionStatsDto>();

            statsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            stats.ShouldNotBeNull();
            stats.TrackedTitleCount.ShouldBe(0);
            stats.SatisfiedTitleCount.ShouldBe(0);
            stats.MissingTitleCount.ShouldBe(0);
            stats.UpgradeTitleCount.ShouldBe(0);
            stats.CompletionPercent.ShouldBe(0m);
            stats.Platforms.ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AdminHost_SatisfiedRoute_SerializesDerivedSatisfiedAt()
    {
        string dataDirectory = Path.Combine(Path.GetTempPath(), $"romd-tracked-collection-{Guid.NewGuid():N}");
        var satisfiedAt = new DateTimeOffset(2026, 8, 23, 12, 30, 0, TimeSpan.Zero);
        var handler = Substitute.For<IQueryHandler<
            ListSatisfiedTrackedTitlesQuery,
            IReadOnlyList<TrackedCollectionTitleDto>>>();
        IReadOnlyList<TrackedCollectionTitleDto> titles =
        [
                new TrackedCollectionTitleDto
                {
                    TitleId = "title-1",
                    SystemKey = "snes",
                    PlatformName = "Super Nintendo",
                    TitleName = "Chrono Trigger",
                    IsSatisfied = true,
                    HasUpgrade = false,
                    IsPinned = false,
                    SatisfiedAt = satisfiedAt
                }
        ];
        handler.HandleAsync(Arg.Any<ListSatisfiedTrackedTitlesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ErrorOrFactory.From(titles)));

        try
        {
            using var database = PostgreSqlTestDatabase.Create();
            await using var factory = CreateAdminHostFactory(dataDirectory, database.ConnectionString, services =>
            {
                services.RemoveAll<IQueryHandler<
                    ListSatisfiedTrackedTitlesQuery,
                    IReadOnlyList<TrackedCollectionTitleDto>>>();
                services.AddSingleton(handler);
            });
            using var client = factory.CreateClient()
                .WithTestUser(Guid.NewGuid(), "admin@localhost", [RomdRoleType.Admin]);

            using var response = await client.GetAsync("/api/tracked-titles/satisfied");
            var body = await response.Content.ReadFromJsonAsync<IReadOnlyList<TrackedCollectionTitleDto>>();

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            body.ShouldHaveSingleItem().SatisfiedAt.ShouldBe(satisfiedAt);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private static WebApplicationFactory<Romd.Admin.Host.Program> CreateAdminHostFactory(
        string dataDirectory,
        string connectionString,
        Action<IServiceCollection>? configureServices = null) =>
        new WebApplicationFactory<Romd.Admin.Host.Program>()
            .WithWebHostBuilder(builder =>
            {
                string webRoot = Path.Combine(dataDirectory, "wwwroot");
                Directory.CreateDirectory(webRoot);
                File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html></html>");
                OpenIddictSigningKey.EnsureCreated(dataDirectory);

                builder.UseEnvironment("Testing");
                builder.UseContentRoot(AppContext.BaseDirectory);
                builder.UseWebRoot(webRoot);
                builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
                builder.UseSetting("Romd:DataDirectory", dataDirectory);
                builder.UseSetting(
                    $"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}",
                    connectionString);
                builder.UseSetting("Romd:JwtSecret", TestJwtSecret);
                builder.UseSetting("Romd:JwtIssuer", "RomdTrackedCollectionTests");
                builder.UseSetting("Romd:JwtAudience", "RomdTrackedCollectionTests");

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.AddHangfire((_, config) => config
                        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                        .UseSimpleAssemblyNameTypeSerializer()
                        .UseRecommendedSerializerSettings()
                        .UseInMemoryStorage());
                    services.AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                    configureServices?.Invoke(services);
                });
            });
}
