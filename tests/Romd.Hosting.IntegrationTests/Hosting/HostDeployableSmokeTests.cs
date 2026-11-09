using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Titles.Commands.QueueTitleEnrichmentJob;
using Romd.Admin.Application.Titles.Commands.QueueBulkEnrichmentJob;
using Romd.Admin.Application.Export.Commands.QueueLibraryExport;
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
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.IngestRom;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.Queries.OpenTitleMedia;
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
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Readiness;
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
        "/delivery/original-layout/{token}",
        "/delivery/disc-set/{token}",
        "/api/me/connections",
        "/api/me/connections/{connectionId:guid}",
        "/api/me/connections/{connectionId:guid}/rotate",
        "/api/me/password",
        "/api/me/settings",
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
        "/api/systems/{systemKey}",
        "/api/companies",
        "/api/companies/{companyKey}",
        "/api/regions",
        "/api/regions/{regionKey}",
        "/api/languages",
        "/api/languages/{languageKey}",
        "/api/rating-boards",
        "/api/rating-boards/{board}",
        "/api/rating-boards/{board}/ratings",
        "/api/rating-boards/{board}/ratings/{ratingCode}",
        "/api/catalog-snapshot",
        "/api/catalog-snapshot/systems",
        "/api/catalog-snapshot/companies",
        "/api/catalog-snapshot/regions",
        "/api/catalog-snapshot/languages",
        "/api/catalog-snapshot/rating-boards",
        "/api/catalog-snapshot/ratings",
        "/api/assets/{hash}",
        "/api/catalog",
        "/api/titles/{titleId}",
        "/api/titles/{titleId}/media",
        "/api/titles/{titleId}/releases",
        "/api/releases/{releaseId}/access",
        "/api/releases/{releaseId}/manifest",
        "/api/releases/{releaseId}/cd-archive",
        "/api/releases/{releaseId}/cd-archive/cue",
        "/api/disc-sets/{discSetId}/manifest",
        "/api/disc-sets/{discSetId}/status",
        "/api/disc-sets/{discSetId}/selection-manifest",
        "/api/disc-sets/{discSetId}/access/preview",
        "/api/disc-sets/{discSetId}/download-tickets",
        "/api/disc-sets",
        "/api/collections",
        "/api/collections/{collectionId}",
        "/api/collections/{collectionId}/titles"
    ];

    private static readonly string[] ExpectedAdminManagementRoutes =
    [
        "/api/original-disc-layouts",
        "/api/original-disc-layouts/{layoutId}",
        "/api/original-disc-layouts/{layoutId}/candidates",
        "/api/original-disc-layouts/{layoutId}/audit",
        "/api/metadata-providers/igdb/settings",
        "/api/metadata-providers/igdb/settings/test-connection",
        "/api/artwork-providers/steamgriddb/settings",
        "/api/artwork-providers/steamgriddb/settings/test-connection",
        "/api/artwork-providers",
        "/api/titles/{titleId}/artwork",
        "/api/titles/{titleId}/artwork/{role}",
        "/api/titles/{titleId}/artwork/{role}/apply",
        "/api/titles/{titleId}/artwork-candidates",
        "/api/titles/{titleId}/artwork-candidates/preview",
        "/api/titles/{titleId}/provider-matches",
        "/api/titles/{titleId}/provider-matches/{providerId}",
        "/api/metadata-providers/{providerId}/games",
        "/api/bios/backfill",
        "/api/bios/backfill-catalog",
        "/api/me/sessions",
        "/api/server/health",
        "/api/dats",
        "/api/dats/{datId}/replacements",
        "/api/dats/{datId}/replacements/preview",
        "/api/dats/{datId}/replacements/changes",
        "/api/dats/{datId}/replacements/changes/{entryId}",
        "/api/download-tickets",
        "/api/download-tickets/{token}",
        "/api/enrichment/stats",
        "/api/export/library",
        "/api/jobs",
        "/api/libraries",
        "/api/libraries/{libraryId}",
        "/api/libraries/{libraryId}/disc-set-options",
        "/api/libraries/{libraryId}/titles/{titleId}/releases",
        "/api/roms",
        "/api/roms/{romId}/download",
        "/api/languages/{languageKey}/aliases",
        "/api/languages/{languageKey}/aliases/{aliasId}",
        "/api/languages/{languageKey}/merge",
        "/api/languages/{languageKey}/merge/preview",
        "/api/regions/{regionKey}/aliases",
        "/api/regions/{regionKey}/aliases/{aliasId}",
        "/api/regions/{regionKey}/merge",
        "/api/regions/{regionKey}/merge/preview",
        "/api/titles/{titleId}/source-references",
        "/api/tracked-titles",
        "/api/tracked-titles/stats",
        "/api/upload/rom",
        "/api/users"
    ];

    private static readonly string[] ForbiddenConsumerManagementRoutePrefixes =
    [
        "/api/admin",
        "/api/artwork",
        "/api/artwork-providers",
        "/api/metadata-providers",
        "/api/bios",
        "/api/server/storage",
        "/api/server/health",
        "/api/server/diagnostics",
        "/api/auth",
        "/api/dashboard",
        "/api/dats",
        "/api/download-tickets",
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
                    // Assets must verify first. The provisioners then precede the
                    // media child and services that touch their schemas.
                    services
                        .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                        .Take(4)
                        .Select(descriptor => descriptor.ImplementationType)
                        .ShouldBe([
                            typeof(ChdAssetsStartupCheck),
                            typeof(PostgreSqlSchemaProvisioner),
                            typeof(HangfireSchemaProvisioner),
                            typeof(MediaWorkerStartupCheck)
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
                services.IsService(typeof(IJobExecutor<MetadataRematerializationJob>)).ShouldBeTrue();
                services.IsService(typeof(MetadataRematerializationJobHangfireHandler)).ShouldBeTrue();
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
                    scope.ServiceProvider.GetRequiredService<IJobExecutor<MetadataRematerializationJob>>()
                        .ShouldBeOfType<MetadataRematerializationJobExecutor>();
                    (await scope.ServiceProvider.GetRequiredService<IJobCancellationPolicy<MetadataRematerializationJob>>()
                        .ReadSignalAsync(Guid.NewGuid())).ShouldBe(JobCancellationSignal.NotRequested);
                    // Application-owned enrichment workflows resolve with every port composed explicitly.
                    scope.ServiceProvider.GetRequiredService<IEnrichmentOrchestrator>()
                        .ShouldBeOfType<EnrichmentOrchestrator>();
                    scope.ServiceProvider.GetRequiredService<IEnrichmentMediaStager>()
                        .ShouldBeOfType<HttpEnrichmentMediaStager>();
                    scope.ServiceProvider.GetRequiredService<IMetadataRematerializer>()
                        .ShouldBeOfType<TitleMetadataRematerializer>();
                    scope.ServiceProvider.GetRequiredService<IRematerializationService>()
                        .ShouldBeOfType<RematerializationService>();
                    scope.ServiceProvider.GetServices<IMetadataRematerializer>().Count().ShouldBe(1);
                }

                var hostedServiceTypes = host.Services.GetServices<IHostedService>()
                    .Select(service => service.GetType())
                    .ToArray();

                hostedServiceTypes.ShouldContain(typeof(OpenIddictSigningKeyInitializer));
                hostedServiceTypes.ShouldContain(typeof(ServerInstanceIdentityInitializer));
                hostedServiceTypes.ShouldContain(typeof(AdminSeeder));
                hostedServiceTypes.ShouldContain(typeof(SharedReferenceDataSeeder));
                hostedServiceTypes.ShouldContain(typeof(RecurringJobRegistrar));
                hostedServiceTypes.ShouldNotContain(type => type.Name == "LibraryMaterializationReconciler");
                hostedServiceTypes.ShouldContain(typeof(MetadataRematerializationWorker));
                hostedServiceTypes.ShouldContain(typeof(CatalogProjectionRecoveryDispatcher));
                hostedServiceTypes.ShouldContain(typeof(ContentStoreStartupCleanup));
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
        var spaResponse = await client.GetAsync("/systems/deep-link");
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
        routePatterns.ShouldContain("/api/me");
        routePatterns.ShouldContain("/api/me/password");
        foreach (string route in ExpectedAdminManagementRoutes)
        {
            routePatterns.ShouldContain(route);
        }
    }

    [Fact]
    public async Task AdminHost_DoesNotComposeMediaInspectionOrRomIngestion()
    {
        await using var factory = CreateFactory<Romd.Admin.Host.Program>();
        var services = factory.Services.GetRequiredService<IServiceProviderIsService>();

        services.IsService(typeof(IRomPreparationService)).ShouldBeFalse();
        services.IsService(typeof(RvzVerifier)).ShouldBeFalse();
        services.IsService(typeof(IMediaWorkerSupervisor)).ShouldBeFalse();
        services.IsService(typeof(MediaWorkerStartupValidator)).ShouldBeFalse();
        services.IsService(typeof(ICommandHandler<IngestRomCommand, RomIngestResult>)).ShouldBeFalse();
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

    [Fact]
    public async Task AdminHost_ResolvesLegacyTitleMediaQueryThroughApplicationPorts()
    {
        await using var factory = CreateFactory<Romd.Admin.Host.Program>();
        await using var scope = factory.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IQueryHandler<OpenTitleMediaQuery, TitleMediaContent>>()
            .ShouldBeOfType<OpenTitleMediaQueryHandler>();
        scope.ServiceProvider.GetRequiredService<ITitleMediaFileReader>()
            .ShouldBeOfType<Romd.Persistence.Queries.TitleMediaFileReader>();
    }

    private static async Task AssertManagementHostServiceProviderIsEnqueueOnlyAsync<TProgram>()
        where TProgram : class
    {
        await using var factory = CreateFactory<TProgram>(removeHostedServices: false);
        var services = factory.Services.GetRequiredService<IServiceProviderIsService>();

        services.IsService(typeof(IUploadJobAcceptance)).ShouldBeTrue();
        services.IsService(typeof(IReplaceDatJobAcceptance)).ShouldBeTrue();
        services.IsService(typeof(ICommandHandler<QueueTitleEnrichmentJobCommand, ErrorOr.Success>)).ShouldBeTrue();
        services.IsService(typeof(ICommandHandler<QueueBulkEnrichmentJobCommand, Guid>)).ShouldBeTrue();
        services.IsService(typeof(IRematerializationScheduler)).ShouldBeTrue();
        services.IsService(typeof(IMaterializationJobEnqueuer)).ShouldBeTrue();
        services.IsService(typeof(IEnrichmentJobEnqueuer)).ShouldBeTrue();
        services.IsService(typeof(ICommandHandler<QueueLibraryExportCommand, Guid>)).ShouldBeTrue();
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
        services.IsService(typeof(MetadataRematerializationJobHangfireHandler)).ShouldBeFalse();
        services.IsService(typeof(IJobExecutor<MetadataRematerializationJob>)).ShouldBeFalse();
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
        hostedServices.ShouldNotContain(type => type.Name == "LibraryMaterializationReconciler");
        hostedServices.ShouldNotContain(typeof(CatalogProjectionRecoveryDispatcher));
        hostedServices.ShouldNotContain(typeof(ContentStoreStartupCleanup));
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
    [InlineData("GET", "/api/metadata-providers/igdb/settings")]
    [InlineData("PUT", "/api/metadata-providers/igdb/settings")]
    [InlineData("POST", "/api/metadata-providers/igdb/settings/test-connection")]
    [InlineData("GET", "/api/artwork-providers/steamgriddb/settings")]
    [InlineData("PUT", "/api/artwork-providers/steamgriddb/settings")]
    [InlineData("POST", "/api/artwork-providers/steamgriddb/settings/test-connection")]
    [InlineData("GET", "/api/artwork-providers")]
    [InlineData("POST", "/api/titles/example/artwork/Poster/apply")]
    [InlineData("POST", "/api/metadata-providers/igdb/search")]
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
                     !route.Pattern.StartsWith("/api/me/connections", StringComparison.Ordinal) &&
                     route.Pattern != "/api/me/password" && route.Pattern != "/api/me/settings" &&
                     !route.Pattern.StartsWith("/api/me/activity", StringComparison.Ordinal) &&
                     route.Pattern != "/api/releases/{releaseId}/manifest" &&
                     route.Pattern != "/api/disc-sets/{discSetId}/manifest" &&
                     route.Pattern != "/api/disc-sets/{discSetId}/selection-manifest" &&
                     route.Pattern != "/api/disc-sets/{discSetId}/access/preview" &&
                     route.Pattern != "/api/disc-sets/{discSetId}/download-tickets"))
        {
            route.Methods.ShouldBe(["GET"]);
        }

        routes.Where(route => route.Pattern == "/api/me/connections")
            .SelectMany(route => route.Methods).Order(StringComparer.Ordinal).ShouldBe(["GET", "POST"]);
        routes.Where(route => route.Pattern == "/api/me/connections/{connectionId:guid}")
            .SelectMany(route => route.Methods).Order(StringComparer.Ordinal).ShouldBe(["DELETE", "PATCH"]);
        routes.Single(route => route.Pattern == "/api/me/connections/{connectionId:guid}/rotate")
            .Methods.ShouldBe(["POST"]);
        routes.Single(route => route.Pattern == "/api/me/password")
            .Methods.ShouldBe(["PUT"]);
        routes
            .Where(route => route.Pattern == "/api/me/settings")
            .SelectMany(route => route.Methods)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["GET", "PATCH"]);
        routes.Single(route => route.Pattern == "/api/releases/{releaseId}/manifest")
            .Methods.ShouldBe(["POST"]);
        routes.Single(route => route.Pattern == "/api/disc-sets/{discSetId}/manifest")
            .Methods.ShouldBe(["POST"]);
        routes.Single(route => route.Pattern == "/api/disc-sets/{discSetId}/selection-manifest").Methods.ShouldBe(["POST"]);
        routes.Single(route => route.Pattern == "/api/disc-sets/{discSetId}/access/preview").Methods.ShouldBe(["POST"]);
        routes.Single(route => route.Pattern == "/api/disc-sets/{discSetId}/download-tickets").Methods.ShouldBe(["POST"]);
        routes.Single(route => route.Pattern == "/api/releases/{releaseId}/access")
            .Methods.ShouldBe(["GET"]);
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
        services.IsService(typeof(ICommandHandler<QueueTitleEnrichmentJobCommand, ErrorOr.Success>)).ShouldBeFalse();
        services.IsService(typeof(ICommandHandler<QueueBulkEnrichmentJobCommand, Guid>)).ShouldBeFalse();
        services.IsService(typeof(IProviderRateLimiter)).ShouldBeFalse();
        services.IsService(typeof(IUploadJobAcceptance)).ShouldBeFalse();
        services.IsService(typeof(IReplaceDatJobAcceptance)).ShouldBeFalse();
        services.IsService(typeof(IMaterializationJobEnqueuer)).ShouldBeFalse();
        services.IsService(typeof(IEnrichmentJobEnqueuer)).ShouldBeFalse();
        services.IsService(typeof(ICommandHandler<QueueLibraryExportCommand, Guid>)).ShouldBeFalse();
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
        services.IsService(typeof(MetadataRematerializationJobHangfireHandler)).ShouldBeFalse();
        services.IsService(typeof(IJobExecutor<MetadataRematerializationJob>)).ShouldBeFalse();
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
        services.IsService(typeof(ITitleMediaFileReader)).ShouldBeFalse();
        services.IsService(typeof(Romd.Admin.Application.Titles.ITitleReader)).ShouldBeFalse();
        services.IsService(typeof(Romd.Admin.Application.Titles.ITitleDetailReader)).ShouldBeFalse();
        services.IsService(typeof(Romd.Admin.Application.Titles.ITitleMediaReader)).ShouldBeFalse();
        services.IsService(typeof(Romd.Admin.Application.Titles.ITitleEnrichmentStatusReader)).ShouldBeFalse();
        services.IsService(typeof(Romd.Admin.Application.Titles.ITrackedTitleReader)).ShouldBeFalse();
        services.IsService(typeof(Romd.Admin.Application.Titles.ITitleAggregateReader)).ShouldBeFalse();
        services.IsService(typeof(Romd.Admin.Application.Titles.ITitleMetadataStore)).ShouldBeFalse();
        services.IsService(typeof(Romd.Admin.Application.Titles.ITitleRowLock)).ShouldBeFalse();
        services.IsService(typeof(Romd.Admin.Application.Titles.ITitleMediaStore)).ShouldBeFalse();
        services.IsService(typeof(Romd.Admin.Application.Titles.ITitleMergeStore)).ShouldBeFalse();
        services.IsService(typeof(Romd.Admin.Application.Titles.ITitleCreationStore)).ShouldBeFalse();
        services.IsService(typeof(Romd.Admin.Application.Titles.ITitleOrphanCleanup)).ShouldBeFalse();
        services.IsService(typeof(IQueryHandler<OpenTitleMediaQuery, TitleMediaContent>)).ShouldBeFalse();
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
