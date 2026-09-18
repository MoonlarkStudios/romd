using Romd.Persistence.ReferenceData;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Artwork.Settings;
using Romd.Application.Common.Artwork;
using System.Net;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Dashboard;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Jobs;
using Romd.Host.Hubs;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Enrichment;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Jobs.Handlers;
using Romd.Infrastructure.Libraries;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.PostgreSql.TestSupport;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Source;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class HostDeployableSmokeTests
{
    private const string TestJwtSecret = "host-smoke-test-secret-key-at-least-32-characters";

    private static readonly string[] ExpectedConsumerRoutes =
    [
        "/artwork/{assetId}/{variantName}/{contentVersion}",
        "/health",
        "/health/ready",
        "/media/{mediaId}",
        "/delivery/content/{token}",
        "/delivery/bios/{token}",
        "/api/account/password",
        "/api/account/settings",
        "/api/server/identity",
        "/api/me",
        "/api/me/activity/play-sessions",
        "/api/me/activity/play-sessions/{sessionId:guid}",
        "/api/me/activity/recently-played",
        "/api/me/library",
        "/api/me/library/systems",
        "/api/me/library/systems/{systemKey}",
        "/api/systems/{systemKey}/bios",
        "/api/playback/config",
        "/api/systems",
        "/api/systems/{key}",
        "/api/companies",
        "/api/companies/{key}",
        "/api/regions",
        "/api/regions/{key}",
        "/api/languages",
        "/api/languages/{key}",
        "/api/rating-boards",
        "/api/rating-boards/{key}",
        "/api/rating-boards/{board}/ratings",
        "/api/rating-boards/{board}/ratings/{code}",
        "/api/catalog-snapshot",
        "/api/assets/{hash}",
        "/api/catalog",
        "/api/titles/{titleId}",
        "/api/releases/{releaseId}/access",
        "/api/releases/{releaseId}/manifest",
        "/api/collections",
        "/api/collections/{collectionId}",
        "/api/collections/{collectionId}/titles"
    ];

    private static readonly string[] ExpectedAdminManagementRoutes =
    [
        "/api/admin/metadata-providers/igdb",
        "/api/admin/metadata-providers/igdb/test-connection",
        "/api/admin/artwork-providers/steamgriddb",
        "/api/admin/artwork-providers/steamgriddb/test-connection",
        "/api/artwork/providers",
        "/api/artwork/titles/{titleId}/selections",
        "/api/artwork/titles/{titleId}/games",
        "/api/artwork/titles/{titleId}/candidates",
        "/api/artwork/titles/{titleId}/preview",
        "/api/artwork/titles/{titleId}/apply",
        "/api/artwork/titles/{titleId}/roles/{role}/automatic",
        "/api/admin/backfill-bios",
        "/api/admin/backfill-bios-catalog",
        "/api/auth/me",
        "/api/auth/password",
        "/api/dashboard/health",
        "/api/dats",
        "/api/enrichment/stats",
        "/api/export/library",
        "/api/jobs",
        "/api/libraries",
        "/api/libraries/{libraryId}",
        "/api/libraries/{libraryId}/titles/{titleId}/releases",
        "/api/roms",
        "/api/roms/{romId}/download",
        "/api/system/stats",
        "/api/taxonomy/languages",
        "/api/taxonomy/regions",
        "/api/titles/{titleId}/details",
        "/api/tracked-titles/missing",
        "/api/tracked-titles/satisfied",
        "/api/tracked-titles/stats",
        "/api/tracked-titles/upgrades",
        "/api/upload/rom",
        "/api/users"
    ];

    private static readonly string[] ForbiddenConsumerManagementRoutePrefixes =
    [
        "/api/admin",
        "/api/artwork",
        "/api/auth",
        "/api/dashboard",
        "/api/dats",
        "/api/enrichment",
        "/api/export",
        "/api/jobs",
        "/api/libraries",
        "/api/roms",
        "/api/system",
        "/api/taxonomy",
        "/api/upload",
        "/api/users"
    ];

    private static readonly string[] ForbiddenConsumerRawStorageRouteTokens =
    [
        "{fileId}",
        "{storageId}",
        "{storageKey}",
        "{sha256}",
        "{hash}",
        "{casKey}",
        "{blobId}"
    ];

    [Fact]
    public async Task AdminHost_MapsManagementRealtimeHealthMediaAndSpaRoutes() =>
        await AssertManagementHostSurfaceAsync<Romd.Admin.Host.Program>();

    [Fact]
    public async Task AdminHost_ServiceProviderExcludesConsumerDelivery() =>
        await AssertManagementHostServiceProviderExcludesConsumerDeliveryAsync<Romd.Admin.Host.Program>();

    [Fact]
    public async Task AdminHost_ServiceProviderIsEnqueueOnly() =>
        await AssertManagementHostServiceProviderIsEnqueueOnlyAsync<Romd.Admin.Host.Program>();

    [Fact]
    public async Task WorkerHost_StartsWithoutHttpSurfaceAndOwnsWorkerServices()
    {
        var tempDataDirectory = Path.Combine(Path.GetTempPath(), $"romd-worker-smoke-{Guid.NewGuid():N}");

        try
        {
            // The host is started, so the seeders run: give them a real (template-copied) database.
            using var database = PostgreSqlTestDatabase.Create();
            using var host = Romd.Worker.Host.Program.CreateHostBuilder([])
                .UseEnvironment("Development")
                .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Romd:DataDirectory"] = tempDataDirectory,
                        ["Romd:JwtSecret"] = TestJwtSecret,
                        ["Romd:JwtIssuer"] = "RomdWorkerSmokeTests",
                        ["Romd:JwtAudience"] = "RomdWorkerSmokeTests",
                        ["ConnectionStrings:Hangfire"] = "Host=127.0.0.1;Database=unused;Username=unused",
                        [$"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}"] =
                            database.ConnectionString
                    }))
                .ConfigureServices(services =>
                {
                    // Hosted services start in registration order. The provisioners must
                    // precede every other hosted service (recurring-job registrar, dispatchers,
                    // Hangfire servers), all of which touch storage only the provisioner creates.
                    services
                        .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                        .Take(2)
                        .Select(descriptor => descriptor.ImplementationType)
                        .ShouldBe([
                            typeof(HangfireSchemaProvisioner),
                            typeof(PostgreSqlSchemaProvisioner)
                        ]);

                    ServiceDescriptor[] provisioningServices = services
                        .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                            && (descriptor.ImplementationType == typeof(HangfireSchemaProvisioner)
                                || descriptor.ImplementationType == typeof(PostgreSqlSchemaProvisioner)))
                        .ToArray();
                    foreach (ServiceDescriptor descriptor in provisioningServices)
                        services.Remove(descriptor);

                    services.AddHangfire((_, configuration) => configuration.UseInMemoryStorage());
                })
                .Build();

            await host.StartAsync();
            try
            {
                var services = host.Services.GetRequiredService<IServiceProviderIsService>();

                services.IsService(typeof(HangfireJobStateSyncFilter)).ShouldBeTrue();
                services.IsService(typeof(JobRunner<UploadJob>)).ShouldBeTrue();
                services.IsService(typeof(UploadJobHangfireHandler)).ShouldBeTrue();
                services.IsService(typeof(IJobExecutor<UploadJob>)).ShouldBeTrue();
                services.IsService(typeof(IJobExecutor<ExportJob>)).ShouldBeTrue();
                services.IsService(typeof(IJobExecutor<MaterializationJob>)).ShouldBeTrue();
                services.IsService(typeof(IAdminEventOutbox)).ShouldBeTrue();
                services.IsService(typeof(IAdminRealtimeOutbox)).ShouldBeTrue();
                services.IsService(typeof(AdminRealtimeOutboxNotifierCommitGate)).ShouldBeTrue();
                services.IsService(typeof(OutboxJobNotifier)).ShouldBeTrue();
                services.IsService(typeof(OutboxStatsNotifier)).ShouldBeTrue();
                services.IsService(typeof(AdminRealtimeOutboxCleanupJob)).ShouldBeTrue();
                services.IsService(typeof(DatReplacementConvergenceSweepJob)).ShouldBeTrue();
                services.IsService(typeof(IAdminRealtimeEventSink)).ShouldBeFalse();
                services.IsService(typeof(SignalRJobNotifier)).ShouldBeFalse();
                services.IsService(typeof(SignalRStatsNotifier)).ShouldBeFalse();
                services.IsService(typeof(IRematerializationService)).ShouldBeTrue();
                services.IsService(typeof(IMetadataRematerializationQueue)).ShouldBeTrue();
                services.IsService(typeof(IMaterializationDataProvider)).ShouldBeTrue();
                services.IsService(typeof(ILibraryMaterializationService)).ShouldBeTrue();
                services.IsService(typeof(ILibraryMaterializationScheduler)).ShouldBeTrue();
                services.IsService(typeof(IMaterializationJobEnqueuer)).ShouldBeTrue();
                services.IsService(typeof(IEnrichmentJobEnqueuer)).ShouldBeTrue();
                services.IsService(typeof(IConsumerContentGrantIssuer)).ShouldBeFalse();
                services.IsService(typeof(IServerInstanceIdentity)).ShouldBeTrue();
                host.Services.GetService<EndpointDataSource>().ShouldBeNull();

                await using (var scope = host.Services.CreateAsyncScope())
                {
                    ReferenceEquals(
                            scope.ServiceProvider.GetRequiredService<IJobNotifier>(),
                            scope.ServiceProvider.GetRequiredService<OutboxJobNotifier>())
                        .ShouldBeTrue();
                    ReferenceEquals(
                            scope.ServiceProvider.GetRequiredService<IStatsNotifier>(),
                            scope.ServiceProvider.GetRequiredService<OutboxStatsNotifier>())
                        .ShouldBeTrue();
                }

                var hostedServiceTypes = host.Services.GetServices<IHostedService>()
                    .Select(service => service.GetType())
                    .ToArray();

                hostedServiceTypes.ShouldContain(typeof(OpenIddictSigningKeyInitializer));
                hostedServiceTypes.ShouldContain(typeof(ServerInstanceIdentityInitializer));
                hostedServiceTypes.ShouldContain(typeof(AdminSeeder));
                hostedServiceTypes.ShouldContain(typeof(SharedReferenceDataSeeder));
                hostedServiceTypes.ShouldContain(typeof(RecurringJobRegistrar));
                hostedServiceTypes.ShouldContain(typeof(LibraryMaterializationReconciler));
                hostedServiceTypes.ShouldContain(typeof(MetadataRematerializationWorker));
                hostedServiceTypes.ShouldContain(typeof(CatalogProjectionRecoveryDispatcher));
                hostedServiceTypes.ShouldNotContain(typeof(AdminRealtimeOutboxDispatcher));

                var monitoringApi = host.Services.GetRequiredService<JobStorage>().GetMonitoringApi();
                using var serverRegistrationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (monitoringApi.Servers().Count < 4)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25), serverRegistrationTimeout.Token);
                }

                var hangfireServers = monitoringApi.Servers();
                hangfireServers.Count.ShouldBe(4);
                hangfireServers.ShouldAllBe(server => server.Queues.Count == 1);

                var queueRegistrations = hangfireServers
                    .Select(server => new
                    {
                        Queue = server.Queues.Single(),
                        WorkerCount = server.WorkersCount
                    })
                    .OrderBy(registration => registration.Queue, StringComparer.Ordinal)
                    .ToArray();
                queueRegistrations.Select(registration => registration.Queue).ShouldBe(
                    ["default", "enrichment", "materialization", "upload"]);
                queueRegistrations.Single(registration => registration.Queue == "default")
                    .WorkerCount.ShouldBe(Environment.ProcessorCount);
                queueRegistrations.Single(registration => registration.Queue == "upload")
                    .WorkerCount.ShouldBe(1);
                queueRegistrations.Single(registration => registration.Queue == "enrichment")
                    .WorkerCount.ShouldBe(1);
                queueRegistrations.Single(registration => registration.Queue == "materialization")
                    .WorkerCount.ShouldBe(2);
            }
            finally
            {
                await host.StopAsync();
            }
        }
        finally
        {
            if (Directory.Exists(tempDataDirectory))
            {
                Directory.Delete(tempDataDirectory, recursive: true);
            }
        }
    }

    private static async Task AssertManagementHostSurfaceAsync<TProgram>()
        where TProgram : class
    {
        await using var factory = CreateFactory<TProgram>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        var spaResponse = await client.GetAsync("/admin/deep-link");
        var apiFallbackResponse = await client.GetAsync("/api/does-not-exist");
        var routePatterns = GetRoutePatterns(factory);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        spaResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        apiFallbackResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        apiFallbackResponse.Content.Headers.ContentType?.MediaType.ShouldNotBe("text/html");
        routePatterns.ShouldContain("/health");
        routePatterns.ShouldContain("/health/ready");
        routePatterns.ShouldContain("/media/{mediaId}");
        routePatterns.ShouldContain("/hubs/jobs");
        routePatterns.ShouldContain("/hubs/system");
        foreach (string route in ExpectedAdminManagementRoutes)
        {
            routePatterns.ShouldContain(route);
        }
    }

    private static async Task AssertManagementHostServiceProviderExcludesConsumerDeliveryAsync<TProgram>()
        where TProgram : class
    {
        await using var factory = CreateFactory<TProgram>();
        var services = factory.Services.GetRequiredService<IServiceProviderIsService>();

        services.IsService(typeof(IConsumerMediaArtifactResolver)).ShouldBeFalse();
        services.IsService(typeof(IConsumerContentArtifactResolver)).ShouldBeFalse();
        services.IsService(typeof(IConsumerContentGrantIssuer)).ShouldBeFalse();
    }

    private static async Task AssertManagementHostServiceProviderIsEnqueueOnlyAsync<TProgram>()
        where TProgram : class
    {
        await using var factory = CreateFactory<TProgram>(removeHostedServices: false);
        var services = factory.Services.GetRequiredService<IServiceProviderIsService>();

        services.IsService(typeof(IUploadJobCreator)).ShouldBeTrue();
        services.IsService(typeof(IReplaceDatJobCreator)).ShouldBeTrue();
        services.IsService(typeof(IEnrichmentScheduler)).ShouldBeTrue();
        services.IsService(typeof(IRematerializationScheduler)).ShouldBeTrue();
        services.IsService(typeof(ILibraryMaterializationScheduler)).ShouldBeTrue();
        services.IsService(typeof(IMaterializationJobEnqueuer)).ShouldBeTrue();
        services.IsService(typeof(IEnrichmentJobEnqueuer)).ShouldBeTrue();
        services.IsService(typeof(IExportScheduler)).ShouldBeTrue();
        services.IsService(typeof(IBackgroundJobClient)).ShouldBeTrue();
        services.IsService(typeof(IAdminEventOutbox)).ShouldBeTrue();
        services.IsService(typeof(IAdminRealtimeOutbox)).ShouldBeTrue();
        services.IsService(typeof(IAdminRealtimeEventSink)).ShouldBeTrue();
        services.IsService(typeof(IServerInstanceIdentity)).ShouldBeTrue();
        services.IsService(typeof(AdminRealtimeOutboxNotifierCommitGate)).ShouldBeTrue();
        services.IsService(typeof(OutboxJobNotifier)).ShouldBeTrue();
        services.IsService(typeof(OutboxStatsNotifier)).ShouldBeTrue();
        services.IsService(typeof(SignalRJobNotifier)).ShouldBeTrue();
        services.IsService(typeof(SignalRStatsNotifier)).ShouldBeTrue();
        services.IsService(typeof(AdminRealtimeOutboxCleanupJob)).ShouldBeFalse();
        services.IsService(typeof(DatReplacementConvergenceSweepJob)).ShouldBeFalse();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var enqueuePort = scope.ServiceProvider.GetRequiredService<IAdminEventOutbox>();
            var dispatchPort = scope.ServiceProvider.GetRequiredService<IAdminRealtimeOutbox>();
            ReferenceEquals(enqueuePort, dispatchPort).ShouldBeTrue();
            ReferenceEquals(
                    scope.ServiceProvider.GetRequiredService<IJobNotifier>(),
                    scope.ServiceProvider.GetRequiredService<OutboxJobNotifier>())
                .ShouldBeTrue();
            ReferenceEquals(
                    scope.ServiceProvider.GetRequiredService<IStatsNotifier>(),
                    scope.ServiceProvider.GetRequiredService<OutboxStatsNotifier>())
                .ShouldBeTrue();
        }

        services.IsService(typeof(HangfireJobStateSyncFilter)).ShouldBeFalse();
        services.IsService(typeof(JobRunner<UploadJob>)).ShouldBeFalse();
        services.IsService(typeof(UploadJobHangfireHandler)).ShouldBeFalse();
        services.IsService(typeof(ReplaceDatJobHangfireHandler)).ShouldBeFalse();
        services.IsService(typeof(EnrichmentJobHangfireHandler)).ShouldBeFalse();
        services.IsService(typeof(BulkEnrichmentJobHangfireHandler)).ShouldBeFalse();
        services.IsService(typeof(ExportJobHangfireHandler)).ShouldBeFalse();
        services.IsService(typeof(MaterializationJobHangfireHandler)).ShouldBeFalse();
        services.IsService(typeof(IJobExecutor<UploadJob>)).ShouldBeFalse();
        services.IsService(typeof(IJobExecutor<ReplaceDatJob>)).ShouldBeFalse();
        services.IsService(typeof(IJobExecutor<EnrichmentJob>)).ShouldBeFalse();
        services.IsService(typeof(IJobExecutor<BulkEnrichmentJob>)).ShouldBeFalse();
        services.IsService(typeof(IJobExecutor<ExportJob>)).ShouldBeFalse();
        services.IsService(typeof(IJobExecutor<MaterializationJob>)).ShouldBeFalse();
        services.IsService(typeof(ExportJobExecutor)).ShouldBeFalse();
        services.IsService(typeof(UploadJobExecutor)).ShouldBeFalse();
        services.IsService(typeof(ReplaceDatJobExecutor)).ShouldBeFalse();
        services.IsService(typeof(EnrichmentJobExecutor)).ShouldBeFalse();
        services.IsService(typeof(BulkEnrichmentJobExecutor)).ShouldBeFalse();
        services.IsService(typeof(MaterializationJobExecutor)).ShouldBeFalse();
        services.IsService(typeof(ExportArtifactCleanupJob)).ShouldBeFalse();
        services.IsService(typeof(IRematerializationService)).ShouldBeFalse();
        services.IsService(typeof(IMetadataRematerializationQueue)).ShouldBeFalse();
        services.IsService(typeof(IMaterializationDataProvider)).ShouldBeFalse();
        services.IsService(typeof(ILibraryMaterializationService)).ShouldBeFalse();
        services.IsService(typeof(BackgroundJobServer)).ShouldBeFalse();

        var hostedServices = factory.Services.GetServices<IHostedService>().Select(service => service.GetType()).ToArray();
        factory.Services.GetRequiredService<IServerInstanceIdentity>().InstanceId.ShouldNotBe(Guid.Empty);
        hostedServices.ShouldContain(typeof(ServerInstanceIdentityInitializer));
        hostedServices.ShouldContain(typeof(AdminRealtimeOutboxDispatcher));
        hostedServices.ShouldNotContain(typeof(PostgreSqlSchemaProvisioner));
        hostedServices.ShouldNotContain(typeof(AdminSeeder));
        hostedServices.ShouldNotContain(typeof(SharedReferenceDataSeeder));
        hostedServices.ShouldNotContain(typeof(RecurringJobRegistrar));
        hostedServices.ShouldNotContain(typeof(LibraryMaterializationReconciler));
        hostedServices.ShouldNotContain(typeof(CatalogProjectionRecoveryDispatcher));
    }

    [Fact]
    public async Task ConsumerHost_MapsOnlyConsumerScaffoldHealthAndMediaRoutes()
    {
        await using var factory = CreateFactory<Romd.Consumer.Host.Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        var spaRootResponse = await client.GetAsync("/");
        var spaLoginResponse = await client.GetAsync("/login");
        var spaLibraryResponse = await client.GetAsync("/library");
        var spaDeepLinkResponse = await client.GetAsync("/titles/deep-link");
        var apiFallbackResponse = await client.GetAsync("/api/does-not-exist");
        var routePatterns = GetRoutePatterns(factory);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        spaRootResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        spaLoginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        spaLibraryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        spaLibraryResponse.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        spaDeepLinkResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        apiFallbackResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        apiFallbackResponse.Content.Headers.ContentType?.MediaType.ShouldNotBe("text/html");
        foreach (string route in ExpectedConsumerRoutes)
        {
            routePatterns.ShouldContain(route);
        }

        // This exact route serves public reference artwork, not arbitrary content hashes.
        routePatterns.Where(route => route != "/api/assets/{hash}").ShouldNotContain(route =>
            ForbiddenConsumerRawStorageRouteTokens.Any(token =>
                route.Contains(token, StringComparison.OrdinalIgnoreCase)));
        routePatterns.ShouldNotContain(pattern => pattern.StartsWith("/hubs", StringComparison.Ordinal));
        foreach (string route in ExpectedAdminManagementRoutes)
        {
            routePatterns.ShouldNotContain(route);
        }

        routePatterns.ShouldNotContain(pattern =>
            ForbiddenConsumerManagementRoutePrefixes.Any(prefix =>
                pattern == prefix || pattern.StartsWith(prefix + "/", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("GET", "/api/admin/metadata-providers/igdb")]
    [InlineData("PUT", "/api/admin/metadata-providers/igdb")]
    [InlineData("POST", "/api/admin/metadata-providers/igdb/test-connection")]
    [InlineData("GET", "/api/admin/artwork-providers/steamgriddb")]
    [InlineData("PUT", "/api/admin/artwork-providers/steamgriddb")]
    [InlineData("POST", "/api/admin/artwork-providers/steamgriddb/test-connection")]
    [InlineData("GET", "/api/artwork/providers")]
    [InlineData("POST", "/api/artwork/titles/example/apply")]
    public async Task ConsumerHost_MetadataProviderManagement_ReturnsNotFound(string method, string path)
    {
        await using var factory = CreateFactory<Romd.Consumer.Host.Program>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldNotBe("text/html");
    }

    [Fact]
    public async Task ConsumerHost_MapsCatalogRoutesAsReadOnlyAndAccountRoutesAsLimitedWrites()
    {
        await using var factory = CreateFactory<Romd.Consumer.Host.Program>();
        var routes = GetRoutes(factory);
        var consumerApiRoutes = routes
            .Where(route => route.Pattern.StartsWith("/api/", StringComparison.Ordinal))
            .OrderBy(route => route.Pattern, StringComparer.Ordinal)
            .ToArray();

        consumerApiRoutes
            .Select(route => route.Pattern)
            .Distinct(StringComparer.Ordinal)
            .ShouldBe(ExpectedConsumerRoutes
                .Where(route => route.StartsWith("/api/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray());

        foreach (var route in consumerApiRoutes.Where(route =>
                     !route.Pattern.StartsWith("/api/account", StringComparison.Ordinal) &&
                     !route.Pattern.StartsWith("/api/me/activity", StringComparison.Ordinal) &&
                     route.Pattern != "/api/releases/{releaseId}/access" &&
                     route.Pattern != "/api/releases/{releaseId}/manifest"))
        {
            route.Methods.ShouldBe(["GET"]);
        }

        routes.Single(route => route.Pattern == "/api/account/password")
            .Methods.ShouldBe(["POST"]);
        routes
            .Where(route => route.Pattern == "/api/account/settings")
            .SelectMany(route => route.Methods)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["GET", "PUT"]);
        routes.Single(route => route.Pattern == "/api/releases/{releaseId}/manifest")
            .Methods.ShouldBe(["POST"]);
        routes.Single(route => route.Pattern == "/api/releases/{releaseId}/access")
            .Methods.ShouldBe(["POST"]);
        routes
            .Where(route => route.Pattern == "/api/me/activity/play-sessions")
            .SelectMany(route => route.Methods)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["DELETE", "GET"]);
        routes
            .Where(route => route.Pattern == "/api/me/activity/play-sessions/{sessionId:guid}")
            .SelectMany(route => route.Methods)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["DELETE", "GET", "PUT"]);
        routes.Single(route => route.Pattern == "/api/me/activity/recently-played")
            .Methods.ShouldBe(["GET"]);
    }

    [Fact]
    public async Task ConsumerHost_ServiceProviderExcludesManagementExecutionServices()
    {
        await using var factory = CreateFactory<Romd.Consumer.Host.Program>();
        var services = factory.Services.GetRequiredService<IServiceProviderIsService>();

        services.IsService(typeof(IMetadataRematerializationQueue)).ShouldBeFalse();
        services.IsService(typeof(IEnrichmentOrchestrator)).ShouldBeFalse();
        services.IsService(typeof(IEnrichmentScheduler)).ShouldBeFalse();
        services.IsService(typeof(IProviderRateLimiter)).ShouldBeFalse();
        services.IsService(typeof(IUploadJobCreator)).ShouldBeFalse();
        services.IsService(typeof(IReplaceDatJobCreator)).ShouldBeFalse();
        services.IsService(typeof(IMaterializationJobEnqueuer)).ShouldBeFalse();
        services.IsService(typeof(IEnrichmentJobEnqueuer)).ShouldBeFalse();
        services.IsService(typeof(IExportScheduler)).ShouldBeFalse();
        services.IsService(typeof(IJobExecutor<UploadJob>)).ShouldBeFalse();
        services.IsService(typeof(IJobExecutor<EnrichmentJob>)).ShouldBeFalse();
        services.IsService(typeof(IJobExecutor<ExportJob>)).ShouldBeFalse();
        services.IsService(typeof(IJobExecutor<MaterializationJob>)).ShouldBeFalse();
        services.IsService(typeof(HangfireJobStateSyncFilter)).ShouldBeFalse();
        services.IsService(typeof(IAdminEventOutbox)).ShouldBeFalse();
        services.IsService(typeof(IAdminRealtimeOutbox)).ShouldBeFalse();
        services.IsService(typeof(IAdminRealtimeEventSink)).ShouldBeFalse();
        services.IsService(typeof(OutboxJobNotifier)).ShouldBeFalse();
        services.IsService(typeof(OutboxStatsNotifier)).ShouldBeFalse();
        services.IsService(typeof(SignalRJobNotifier)).ShouldBeFalse();
        services.IsService(typeof(SignalRStatsNotifier)).ShouldBeFalse();
        services.IsService(typeof(AdminRealtimeOutboxCleanupJob)).ShouldBeFalse();
        services.IsService(typeof(AdminRealtimeOutboxDispatcher)).ShouldBeFalse();
        services.IsService(typeof(JobRunner<UploadJob>)).ShouldBeFalse();
        services.IsService(typeof(UploadJobHangfireHandler)).ShouldBeFalse();
        services.IsService(typeof(ReplaceDatJobHangfireHandler)).ShouldBeFalse();
        services.IsService(typeof(EnrichmentJobHangfireHandler)).ShouldBeFalse();
        services.IsService(typeof(BulkEnrichmentJobHangfireHandler)).ShouldBeFalse();
        services.IsService(typeof(ExportJobHangfireHandler)).ShouldBeFalse();
        services.IsService(typeof(MaterializationJobHangfireHandler)).ShouldBeFalse();
        services.IsService(typeof(ExportJobExecutor)).ShouldBeFalse();
        services.IsService(typeof(UploadJobExecutor)).ShouldBeFalse();
        services.IsService(typeof(EnrichmentJobExecutor)).ShouldBeFalse();
        services.IsService(typeof(MaterializationJobExecutor)).ShouldBeFalse();
        services.IsService(typeof(ExportArtifactCleanupJob)).ShouldBeFalse();
        services.IsService(typeof(BackgroundJobServer)).ShouldBeFalse();
        services.IsService(typeof(IFileStorageService)).ShouldBeFalse();
        services.IsService(typeof(IFileRepository)).ShouldBeFalse();
        services.IsService(typeof(IArtworkBrowsingService)).ShouldBeFalse();
        services.IsService(typeof(IArtworkProviderBrowser)).ShouldBeFalse();
        services.IsService(typeof(IArtworkAssetSource)).ShouldBeFalse();
        services.IsService(typeof(ISteamGridDbCredentials)).ShouldBeFalse();
        services.IsService(typeof(ISteamGridDbSettingsService)).ShouldBeFalse();
        services.IsService(typeof(ISteamGridDbSettingsStore)).ShouldBeFalse();
        services.IsService(typeof(IArtworkCurationRepository)).ShouldBeFalse();
        services.IsService(typeof(ArtworkCurationService)).ShouldBeFalse();
        services.IsService(typeof(IArtworkImageProcessor)).ShouldBeFalse();
        services.IsService(typeof(IFileMutationLock)).ShouldBeFalse();
        services.IsService(typeof(IJobExecutor<ArtworkImportJob>)).ShouldBeFalse();
        services.IsService(typeof(IJobRepository<ArtworkImportJob>)).ShouldBeFalse();
        services.IsService(typeof(ArtworkImportJobHangfireHandler)).ShouldBeFalse();
        services.IsService(typeof(IArtworkReader)).ShouldBeTrue();
        services.IsService(typeof(IArtworkDelivery)).ShouldBeTrue();
        services.IsService(typeof(IUnitOfWork)).ShouldBeFalse();
        services.IsService(typeof(IConsumerContentArtifactResolver)).ShouldBeTrue();
        services.IsService(typeof(IConsumerMediaArtifactResolver)).ShouldBeTrue();
        services.IsService(typeof(IConsumerContentGrantIssuer)).ShouldBeTrue();
        services.IsService(typeof(IServerInstanceIdentity)).ShouldBeTrue();
    }

    [Fact]
    public async Task ConsumerHost_ServiceProviderConstrainedToConsumerIdentityServices()
    {
        await using var factory = CreateFactory<Romd.Consumer.Host.Program>();
        var services = factory.Services.GetRequiredService<IServiceProviderIsService>();

        services.IsService(typeof(IConsumerPasswordChanger)).ShouldBeTrue();
        services.IsService(typeof(IConsumerUserSettingsStore)).ShouldBeTrue();
        services.IsService(typeof(UserManager<RomdUser>)).ShouldBeFalse();
        services.IsService(typeof(SignInManager<RomdUser>)).ShouldBeFalse();
        services.IsService(typeof(IUserStore<RomdUser>)).ShouldBeFalse();

        services.IsService(typeof(RoleManager<RomdIdentityRole>)).ShouldBeFalse();
        services.IsService(typeof(IRoleStore<RomdIdentityRole>)).ShouldBeFalse();
        services.IsService(typeof(ILibraryRepository)).ShouldBeFalse();
    }

    private static WebApplicationFactory<TProgram> CreateFactory<TProgram>(bool removeHostedServices = true)
        where TProgram : class =>
        new WebApplicationFactory<TProgram>()
            .WithWebHostBuilder(builder =>
            {
                var tempDataDirectory = Path.Combine(Path.GetTempPath(), $"romd-host-smoke-{Guid.NewGuid():N}");
                var webRoot = Path.Combine(tempDataDirectory, "wwwroot");

                Directory.CreateDirectory(webRoot);
                File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html></html>");
                OpenIddictSigningKey.EnsureCreated(tempDataDirectory);

                builder.UseEnvironment("Testing");
                builder.UseContentRoot(AppContext.BaseDirectory);
                builder.UseWebRoot(webRoot);
                builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
                builder.UseSetting("Romd:DataDirectory", tempDataDirectory);
                builder.UseSetting(
                    $"ConnectionStrings:{PostgreSqlConfiguration.RuntimeConnectionName}",
                    "Host=127.0.0.1;Database=unused;Username=unused");
                builder.UseSetting("Romd:JwtSecret", TestJwtSecret);
                builder.UseSetting("Romd:JwtIssuer", "RomdHostSmokeTests");
                builder.UseSetting("Romd:JwtAudience", "RomdHostSmokeTests");

                builder.ConfigureTestServices(services =>
                {
                    // Assert the production registrations before the test removes hosted services.
                    // Admin and consumer hosts may submit work but cannot execute metadata jobs.
                    services.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(IHostedService)
                        && descriptor.ImplementationType == typeof(MetadataRematerializationWorker));
                    if (removeHostedServices)
                    {
                        services.RemoveAll<IHostedService>();
                    }

                    if (typeof(TProgram) == typeof(Romd.Admin.Host.Program))
                    {
                        services.AddHangfire((serviceProvider, config) => config
                            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                            .UseSimpleAssemblyNameTypeSerializer()
                            .UseRecommendedSerializerSettings()
                            .UseInMemoryStorage());
                    }
                });
            });

    private static string[] GetRoutePatterns<TProgram>(WebApplicationFactory<TProgram> factory)
        where TProgram : class =>
        GetRoutes(factory)
            .Select(route => route.Pattern)
            .ToArray();

    private static RouteInfo[] GetRoutes<TProgram>(WebApplicationFactory<TProgram> factory)
        where TProgram : class =>
        factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => new RouteInfo(
                NormalizeRoutePattern(endpoint.RoutePattern.RawText ?? string.Empty),
                endpoint.Metadata
                    .GetMetadata<HttpMethodMetadata>()
                    ?.HttpMethods
                    .Order(StringComparer.Ordinal)
                    .ToArray() ?? []))
            .OrderBy(route => route.Pattern, StringComparer.Ordinal)
            .ToArray();

    private sealed record RouteInfo(string Pattern, string[] Methods);

    private static string NormalizeRoutePattern(string pattern) =>
        pattern.Length > 1 ? pattern.TrimEnd('/') : pattern;
}
