using Romd.Application.Common.ReferenceCatalog;
using Romd.Admin.Application.ReferenceData;
using Romd.Persistence.Enrichment;
using Romd.Application.Common.Artwork;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Artwork.Settings;
using Romd.Infrastructure.Artwork;
using Romd.Infrastructure.Artwork.SteamGridDb;
using Romd.Persistence.Artwork;
using Romd.Persistence.MetadataProviders;
using Romd.Admin.Application.MetadataProviders;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Subscriptions;
// src/Romd.Infrastructure/DependencyInjection.cs

using System.IO.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.EntityFrameworkCore;
using Romd.Admin.Application;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Collections;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Readiness;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Dashboard;
using Romd.Admin.Application.Diagnostics;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Ingestion.Classification;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Search;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.Matching;
using Romd.Admin.Application.TrackedCollection;
using Romd.Admin.Application.Users;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Access;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Activity;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Collections;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Admin.Application.Libraries;
using Romd.Dat.Parsing;
using Romd.Dat.Parsing.Formats.ClrMamePro;
using Romd.Dat.Parsing.Formats.Logiqx;
using Romd.Domain.Catalog;
using Romd.Domain.Libraries;
using Romd.Domain.Taxonomy;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Catalog;
using Romd.Infrastructure.Dashboard;
using Romd.Infrastructure.Dats;
using Romd.Infrastructure.Delivery;
using Romd.Infrastructure.Diagnostics;
using Romd.Infrastructure.Enrichment;
using Romd.Infrastructure.Libraries;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Jobs.Handlers;
using Romd.Infrastructure.Jobs.Processors;
using Romd.Persistence;
using Romd.Persistence.Repositories;
using Romd.Persistence.Search;
using Romd.Infrastructure.Readiness;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Resilience;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Taxonomy;
using Romd.Infrastructure.Upload;

namespace Romd.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddRomdAdminPersistence(this IServiceCollection services) =>
        services.AddRomdPersistence();

    private static IServiceCollection AddRomdOpenIddictCore(this IServiceCollection services)
    {
        services.AddOpenIddict()
            .AddCore(options =>
            {
                options.DisableEntityCaching();
                options.UseEntityFrameworkCore()
                    .UseDbContext<RomdDbContext>();
            });

        return services;
    }

    public static IServiceCollection AddRomdPersistence(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddRomdAuditInfrastructure();
        services.AddRomdOpenIddictCore();
        services.AddScoped<IAccountSessions, Romd.Persistence.Identity.AccountSessionStore>();

        services.AddDbContext<RomdDbContext>((sp, options) =>
        {
            ConfigureRuntimeDatabase(sp, options);
            options.AddInterceptors(
                sp.GetRequiredService<AuditInterceptor>(),
                sp.GetRequiredService<SearchDocumentInterceptor>());
        });

        return services.AddRomdPersistenceRepositories();
    }

    public static IServiceCollection AddRomdConsumerPersistence(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddRomdAuditInfrastructure();
        services.AddRomdOpenIddictCore();
        services.AddScoped<IAccountSessions, Romd.Persistence.Identity.AccountSessionStore>();
        services.AddScoped<ConsumerWriteGuardInterceptor>();

        services.AddDbContext<RomdDbContext>((sp, options) =>
        {
            ConfigureRuntimeDatabase(sp, options);
            options.AddInterceptors(
                new AuditInterceptor(sp.GetRequiredService<IAuditContext>(), sp.GetRequiredService<TimeProvider>(),
                    captureAdministrativeChanges: false),
                sp.GetRequiredService<ConsumerWriteGuardInterceptor>(),
                sp.GetRequiredService<SearchDocumentInterceptor>());
        });

        return services.AddRomdConsumerPersistenceRepositories();
    }

    public static IServiceCollection AddRomdTestingPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddRomdAuditInfrastructure();
        services.AddRomdOpenIddictCore();
        services.AddScoped<IAccountSessions, Romd.Persistence.Identity.AccountSessionStore>();

        services.AddDbContext<RomdDbContext>((sp, options) =>
        {
            PostgreSqlConfiguration.Configure(options, connectionString);
            options.UseOpenIddict();
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
            options.AddInterceptors(
                sp.GetRequiredService<AuditInterceptor>(),
                sp.GetRequiredService<SearchDocumentInterceptor>());
        });

        return services.AddRomdPersistenceRepositories();
    }

    private static void ConfigureRuntimeDatabase(IServiceProvider sp, DbContextOptionsBuilder options)
    {
        string connectionString = PostgreSqlConfiguration.GetRequiredConnectionString(
            sp.GetRequiredService<IConfiguration>(),
            PostgreSqlConfiguration.RuntimeConnectionName);
        PostgreSqlConfiguration.Configure(options, connectionString);
        // Log executed SQL at Debug (not EF's default of Information) so bulk imports don't
        // flood the logs with a line per command — also a throughput win under load.
        options.ConfigureWarnings(w =>
            w.Log((Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted,
                Microsoft.Extensions.Logging.LogLevel.Debug)));
        options.UseOpenIddict();
    }

    public static IServiceCollection AddRomdApplicationHandlers(this IServiceCollection services) =>
        services
            .AddRomdAdminApplicationHandlers()
            .AddRomdConsumerApplicationHandlers();

    public static IServiceCollection AddRomdAdminApplicationHandlers(this IServiceCollection services) =>
        services.AddRomdApplicationHandlersFromAssembly<AdminApplicationAssembly>();

    public static IServiceCollection AddRomdConsumerApplicationHandlers(this IServiceCollection services) =>
        services.AddRomdApplicationHandlersFromAssembly<ConsumerApplicationAssembly>();

    public static IServiceCollection AddRomdStorage(this IServiceCollection services)
    {
        services.AddSingleton<IFileSystem, FileSystem>();
        services.AddSingleton<ITempFileFactory, LocalTempFileFactory>();
        services.AddScoped<IFileStorageService, FileStorageService>();
        services.AddSingleton<IHashingService, HashingService>();
        services.AddSingleton<IArchiveExtractor, ZipArchiveExtractor>();
        services.AddSingleton<IArchiveExtractorResolver, ArchiveExtractorResolver>();

        return services;
    }

    public static IServiceCollection AddRomdReadOnlyStorage(this IServiceCollection services)
    {
        services.AddScoped<IConsumerContentArtifactResolver, ConsumerContentArtifactResolver>();
        services.AddScoped<IConsumerMediaArtifactResolver, ConsumerMediaArtifactResolver>();

        return services;
    }

    public static IServiceCollection AddRomdAdminEnqueueInfrastructure(
        this IServiceCollection services,
        IConfiguration? configuration = null) =>
        services
            .AddRomdServerInstanceIdentity()
            .AddRomdAdminPersistence()
            .AddRomdAdminApplicationHandlers()
            .AddRomdStorage()
            .AddRomdAdminCurationServices(configuration)
            .AddRomdExportScheduling()
            .AddRomdRealtimeOutboxPublishers()
            .AddRomdIdentityServices()
            .AddRomdAdminDiagnostics()
            .AddRomdAdminReadinessChecks();

    public static IServiceCollection AddRomdWorkerInfrastructure(
        this IServiceCollection services,
        IConfiguration? configuration = null) =>
        services
            .AddRomdServerInstanceIdentity()
            .AddRomdAdminPersistence()
            .AddRomdAdminApplicationHandlers()
            .AddRomdStorage()
            .AddRomdWorkerCurationServices(configuration)
            .AddRomdExportScheduling()
            .AddRomdHangfireJobPipeline()
            .AddRomdJobExecution()
            .AddRomdRealtimeOutboxPublishers()
            .AddRomdIdentityServices()
            // The worker scans the full admin application assembly and validates every
            // handler at startup. Register the read-only dependencies even though the
            // worker exposes no HTTP endpoint for this query.
            .AddRomdAdminDiagnostics()
            .AddRomdDatabaseStartupInitialization()
            .AddRomdSeeders()
            .AddRomdRecurringJobs()
            .AddRomdLibraryMaterializationReconciler()
            .AddRomdJobDispatchWorker()
            .AddRomdCatalogProjectionRecovery();

    public static IServiceCollection AddRomdCurationServices(
        this IServiceCollection services,
        IConfiguration? configuration = null) =>
        services.AddRomdWorkerCurationServices(configuration);

    public static IServiceCollection AddRomdAdminCurationServices(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.TryAddScoped<ILibraryCandidateReader, MaterializationDataProvider>();
        services.AddScoped<ILibraryDraftEvaluator, LibraryDraftEvaluator>();
        services.AddSingleton<TaxonomyAliasCache>();
        services.AddScoped<ITaxonomyResolver<Region>>(sp =>
            new TaxonomyResolver<Region>(
                sp.GetRequiredService<ITaxonomyRepository<Region>>(),
                sp.GetRequiredService<TaxonomyAliasCache>(),
                cacheKey: "regions",
                entityFactory: token => Region.CreateNew(token, sortOrder: 999, isAutoCreated: true)));
        services.AddScoped<ITaxonomyResolver<GameLanguage>>(sp =>
            new TaxonomyResolver<GameLanguage>(
                sp.GetRequiredService<ITaxonomyRepository<GameLanguage>>(),
                sp.GetRequiredService<TaxonomyAliasCache>(),
                cacheKey: "languages",
                entityFactory: token => GameLanguage.CreateNew(token, token.ToLowerInvariant(), sortOrder: 999, isAutoCreated: true)));

        services.AddScoped<TaxonomyService<Region>>();
        services.AddScoped<TaxonomyService<GameLanguage>>();

        services.AddScoped<ITitleMatcher, TitleMatcher>();
        services.AddScoped<ITitleDerivationService, TitleDerivationService>();
        services.AddScoped<ITitleSourceReferenceReader, TitleSourceReferenceReader>();
        services.AddScoped<ISourceLifecycle, SourceLifecycleStore>();
        services.AddScoped<Romd.Admin.Application.Catalog.Sources.IDatSourceManagement, Romd.Persistence.Catalog.DatSourceManagement>();
        services.AddScoped<Romd.Admin.Application.Catalog.Sources.ISourceEntryReader, Romd.Persistence.Catalog.SourceEntryReader>();
        services.AddScoped<Romd.Admin.Application.Catalog.IBiosGrouper, Romd.Admin.Application.Catalog.BiosGrouper>();
        services.AddScoped<IPlatformHeaderResolver, PlatformHeaderResolver>();

        services.AddScoped<IDatReader, DatReader>();
        services.AddScoped<IDatReplacementReview, DatReplacementReview>();
        services.AddScoped<IReferenceBlobStore, Romd.Infrastructure.Storage.ReferenceBlobStore>();
        services.AddScoped<IReferenceCatalogService, ReferenceCatalogService>();
        services.AddScoped<IRegionReferenceReader, RegionReferenceReader>();
        services.AddScoped<ILanguageReferenceReader, LanguageReferenceReader>();
        services.AddScoped<IRatingBoardReferenceReader, RatingBoardReferenceReader>();
        services.AddScoped<IRatingReferenceReader, RatingReferenceReader>();

        services.AddScoped<ISystemReferenceReader, SystemReferenceReader>();
        services.AddScoped<ICompanyReferenceReader, CompanyReferenceReader>();
        services.AddScoped<ISystemReferenceRepository, SystemReferenceRepository>();
        services.AddScoped<ICompanyReferenceRepository, CompanyReferenceRepository>();
        services.AddScoped<IReferenceMutationSession, ReferenceMutationSession>();
        services.AddScoped<ISignedDatCatalogClient, SignedDatCatalogClient>();
        services.AddScoped<IDatSubscriptionService, DatSubscriptionService>();
        services.AddScoped<IDatCatalogEnrollmentService, DatCatalogEnrollmentService>();
        services.AddScoped<IDatFormat, LogiqxDatFormat>();
        services.AddScoped<IDatFormat, ClrMameProDatFormat>();
        services.AddScoped<DatParser>();
        services.AddScoped<IDatHeaderReader>(sp => sp.GetRequiredService<DatParser>());
        services.AddScoped<IDatGameStreamReader>(sp => sp.GetRequiredService<DatParser>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<ICatalogSourceSnapshotProvider, DatCatalogSourceSnapshotProvider>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<ICatalogPayloadAssertionProvider, DatCatalogPayloadAssertionProvider>());
        services.TryAddScoped<ICatalogSourceSnapshotReader, CatalogSourceSnapshotReader>();
        services.TryAddScoped<ICatalogPayloadAssertionReader, CatalogPayloadAssertionReader>();
        services.TryAddScoped<ICatalogPayloadAssertionSynchronizer, CatalogPayloadAssertionSynchronizer>();
        services.TryAddScoped<ITitlePayloadAvailabilityProjection, TitlePayloadAvailabilityProjection>();
        services.TryAddScoped<ICatalogProjectionService, CatalogProjectionService>();

        services.AddScoped<IUploadJobCreator, UploadJobCreator>();
        services.AddScoped<IPathValidator, Import.PathValidator>();
        services.AddScoped<IPathImportJobCreator, Import.PathImportJobCreator>();
        services.AddScoped<IReplaceDatJobCreator, ReplaceDatJobCreator>();
        services.AddScoped<IEnrichmentScheduler, EnrichmentScheduler>();
        services.AddScoped<IEnrichmentJobEnqueuer, EnrichmentJobEnqueuer>();
        services.AddScoped<IRematerializationScheduler, RematerializationScheduler>();
        services.AddScoped<ILibraryMaterializationScheduler, LibraryMaterializationScheduler>();
        services.AddScoped<IMaterializationJobEnqueuer, MaterializationJobEnqueuer>();

        services.AddScoped<IFileClassifier, FileClassifier>();

        services.TryAddScoped<MetadataMerger>();
        services.TryAddScoped<IMetadataRematerializer>(sp => sp.GetRequiredService<MetadataMerger>());

        services.AddRomdMetadataProviderServices(configuration);
        services.AddRomdArtworkServices(configuration);

        return services;
    }

    private static IServiceCollection AddRomdArtworkServices(this IServiceCollection services, IConfiguration? configuration)
    {
        // Combined admin/worker hosts may compose curation twice. Provider registries
        // require one registration per identity, including their shared settings.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IArtworkBrowsingService))) return services;
        services.AddOptions<SteamGridDbOptions>().Configure(options =>
            configuration?.GetSection(SteamGridDbOptions.SectionName).Bind(options));
        services.AddHttpClient("SteamGridDbConnectionTest")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<ISteamGridDbSettingsStore, SteamGridDbSettingsStore>();
        services.AddScoped<SteamGridDbSettingsService>();
        services.AddScoped<ISteamGridDbSettingsService>(sp => sp.GetRequiredService<SteamGridDbSettingsService>());
        services.AddScoped<ISteamGridDbCredentials>(sp => sp.GetRequiredService<SteamGridDbSettingsService>());
        services.AddSingleton<SteamGridDbRateGate>();
        services.AddScoped<SteamGridDbArtworkProvider>();
        services.AddScoped<IProviderIdentityAdapter, SteamGridDbIdentityAdapter>();
        services.AddScoped<IProviderIdentityAdapter>(sp => sp.GetRequiredService<IgdbMetadataProvider>());
        services.AddScoped<ITitleProviderMatchService, TitleProviderMatchService>();
        services.AddScoped<IArtworkProviderBrowser>(sp => sp.GetRequiredService<SteamGridDbArtworkProvider>());
        services.AddScoped<IgdbArtworkProvider>();
        services.AddScoped<IArtworkProviderBrowser>(sp => sp.GetRequiredService<IgdbArtworkProvider>());
        services.AddScoped<IArtworkProvider>(sp => sp.GetRequiredService<SteamGridDbArtworkProvider>());
        services.AddScoped<IArtworkProvider>(sp => sp.GetRequiredService<IgdbArtworkProvider>());
        services.AddScoped<IArtworkAssetSource, ArtworkAssetSource>();
        services.AddScoped<IArtworkImageProcessor, ArtworkImageProcessor>();
        services.AddSingleton<ArtworkPreviewCache>();
        services.AddScoped<IArtworkBrowsingService, ArtworkBrowsingService>();
        services.AddScoped<GalleryArtworkService>();
        services.AddScoped<ArtworkCurationService>();
        services.AddScoped<ILocalArtworkService, LocalArtworkService>();
        services.AddScoped<IArtworkAcquisitionStore, ArtworkAcquisitionStore>();
        services.AddHttpClient(AutomaticArtworkService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }

    public static IServiceCollection AddRomdWorkerCurationServices(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.AddRomdAdminCurationServices(configuration);
        services.AddRomdEnrichmentExecutionServices();
        services.AddRomdMaterializationExecutionServices();
        services.AddRomdIngestionExecutionServices();

        return services;
    }

    private static IServiceCollection AddRomdMetadataProviderServices(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.AddResiliencePolicies();

        services.AddHttpClient("IgdbConnectionTest");
        services.AddScoped<IIgdbProviderSettingsStore, IgdbProviderSettingsStore>();
        services.AddScoped<IgdbProviderSettingsService>();
        services.AddScoped<IIgdbProviderSettingsService>(sp => sp.GetRequiredService<IgdbProviderSettingsService>());

        services.AddHttpClient<IgdbMetadataProvider>()
            .AddIgdbResilienceHandler();

        services.AddOptions<IgdbProviderOptions>()
            .Configure(options =>
            {
                configuration?.GetSection(IgdbProviderOptions.SectionName).Bind(options);
            });
        services.AddSingleton<MatchConfidenceCalculator>();
        services.AddSingleton<IProviderRateLimiter, ProviderRateLimiter>();

        services.AddScoped<IgdbMetadataProvider>();
        services.AddScoped<IMetadataProvider>(sp => sp.GetRequiredService<IgdbMetadataProvider>());

        return services;
    }

    private static IServiceCollection AddRomdEnrichmentExecutionServices(this IServiceCollection services)
    {
        services.AddHttpClient<EnrichmentOrchestrator>();
        services.AddHttpClient<MediaDownloader>();

        services.TryAddScoped<MetadataMerger>();
        services.TryAddScoped<IMetadataRematerializer>(sp => sp.GetRequiredService<MetadataMerger>());
        services.AddScoped<MediaDownloader>();
        services.AddScoped<AutomaticArtworkService>();
        services.AddScoped<IAutomaticArtworkService, LinkedProviderArtworkService>();
        services.AddScoped<ITitleEnrichmentEvidenceReader, TitleEnrichmentEvidenceReader>();
        services.AddScoped<IEnrichmentContextFactory, EnrichmentContextFactory>();
        services.AddScoped<IEnrichmentOrchestrator, EnrichmentOrchestrator>();

        return services;
    }

    private static IServiceCollection AddRomdMaterializationExecutionServices(this IServiceCollection services)
    {
        services.AddScoped<IRematerializationService, RematerializationService>();
        services.AddScoped<IMetadataRematerializationQueue, MetadataRematerializationQueue>();
        services.AddHostedService<MetadataRematerializationWorker>();
        services.AddScoped<IMaterializationDataProvider, MaterializationDataProvider>();
        services.AddScoped<ILibraryMaterializationService, LibraryMaterializationService>();

        return services;
    }

    private static IServiceCollection AddRomdIngestionExecutionServices(this IServiceCollection services)
    {
        services.AddScoped<DatProcessor>();
        services.AddScoped<RomProcessor>();

        return services;
    }

    public static IServiceCollection AddRomdExportScheduling(this IServiceCollection services)
    {
        services.AddScoped<IExportScheduler, ExportScheduler>();

        return services;
    }

    public static IServiceCollection AddRomdExportExecution(this IServiceCollection services)
    {
        services.AddScoped<ExportArtifactCleanupJob>();
        services.AddScoped<IJobExecutor<ExportJob>, ExportJobExecutor>();

        return services;
    }

    public static IServiceCollection AddRomdHangfireJobPipeline(this IServiceCollection services)
    {
        services.AddSingleton<HangfireJobStateSyncFilter>();
        services.AddScoped(typeof(JobRunner<>));
        services.AddScoped<UploadJobHangfireHandler>();
        services.AddScoped<ReplaceDatJobHangfireHandler>();
        services.AddScoped<EnrichmentJobHangfireHandler>();
        services.AddScoped<BulkEnrichmentJobHangfireHandler>();
        services.AddScoped<ExportJobHangfireHandler>();
        services.AddScoped<MaterializationJobHangfireHandler>();
        services.AddScoped<ArtworkImportJobHangfireHandler>();

        return services;
    }

    public static IServiceCollection AddRomdJobExecution(this IServiceCollection services)
    {
        services.AddScoped<Import.ImportSourceCleanup>();
        services.AddSingleton(new Import.ArchiveExtractionLimits());
        services.AddScoped<ArchiveExtractionStep>();
        services.AddScoped<IJobExecutor<UploadJob>, UploadJobExecutor>();
        services.AddScoped<IJobExecutor<ReplaceDatJob>, ReplaceDatJobExecutor>();
        services.AddScoped<IJobExecutor<EnrichmentJob>, EnrichmentJobExecutor>();
        services.AddScoped<IJobExecutor<BulkEnrichmentJob>, BulkEnrichmentJobExecutor>();
        services.AddScoped<IJobExecutor<MaterializationJob>, MaterializationJobExecutor>();
        services.AddScoped<IJobExecutor<ArtworkImportJob>, ArtworkImportJobExecutor>();

        return services.AddRomdExportExecution();
    }

    public static IServiceCollection AddRomdNotifications(this IServiceCollection services)
    {
        services.AddScoped<IJobNotifier, NoOpJobNotifier>();
        services.AddScoped<IStatsNotifier, NoOpStatsNotifier>();

        return services;
    }

    public static IServiceCollection AddRomdRealtimeOutboxPublishers(this IServiceCollection services)
    {
        services.AddScoped<AdminRealtimeOutboxNotifierCommitGate>();
        services.AddScoped<OutboxJobNotifier>();
        services.AddScoped<OutboxStatsNotifier>();
        services.AddScoped<IJobNotifier>(sp => sp.GetRequiredService<OutboxJobNotifier>());
        services.AddScoped<IStatsNotifier>(sp => sp.GetRequiredService<OutboxStatsNotifier>());

        return services;
    }

    public static IServiceCollection AddRomdIdentityServices(this IServiceCollection services)
    {
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<RomdOpenIddictAccountService>();

        return services;
    }

    public static IServiceCollection AddRomdServerInstanceIdentity(this IServiceCollection services)
    {
        services.TryAddSingleton<ServerInstanceIdentity>();
        services.TryAddSingleton<IServerInstanceIdentity>(provider =>
            provider.GetRequiredService<ServerInstanceIdentity>());
        services.AddHostedService<ServerInstanceIdentityInitializer>();

        return services;
    }

    public static IServiceCollection AddRomdDatabaseStartupInitialization(this IServiceCollection services)
    {
        services.AddHostedService<OpenIddictSigningKeyInitializer>();

        return services;
    }

    public static IServiceCollection AddRomdSeeders(this IServiceCollection services)
    {
        services.AddHostedService<AdminSeeder>();
        services.AddHostedService<SharedReferenceDataSeeder>();
        services.AddHostedService<CatalogBackfillSeeder>();
        services.AddHostedService<RomdOpenIddictApplicationSeeder>();

        return services;
    }

    public static IServiceCollection AddRomdRecurringJobs(this IServiceCollection services)
    {
        services.AddScoped<DatSubscriptionCheckJob>();
        services.AddScoped<AdminRealtimeOutboxCleanupJob>();
        services.AddScoped<RomdOpenIddictPruneJob>();
        services.AddScoped<DatReplacementConvergenceSweepJob>();
        services.AddScoped<TitlePayloadAvailabilitySweepJob>();
        services.AddScoped<IDatVersionRetentionCoordinator, DatVersionRetentionCoordinator>();
        services.AddHostedService<RecurringJobRegistrar>();

        return services;
    }

    public static IServiceCollection AddRomdLibraryMaterializationReconciler(this IServiceCollection services)
    {
        services.AddHostedService<LibraryMaterializationReconciler>();

        return services;
    }

    public static IServiceCollection AddRomdJobDispatchWorker(this IServiceCollection services)
    {
        services.AddHostedService<JobDispatchWorker>();

        return services;
    }

    public static IServiceCollection AddRomdCatalogProjectionRecovery(this IServiceCollection services)
    {
        services.AddHostedService<CatalogProjectionRecoveryDispatcher>();

        return services;
    }

    /// <summary>
    ///     Readiness for the admin host: the shared checks plus the Hangfire
    ///     storage, admin realtime outbox, and worker heartbeat checks. The worker host stays
    ///     non-HTTP and registers no readiness services.
    /// </summary>
    public static IServiceCollection AddRomdAdminReadinessChecks(this IServiceCollection services)
    {
        services.AddRomdReadinessEvaluation();
        services.TryAddSingleton<IHangfireStorageProbe, HangfireJobStorageProbe>();
        services.TryAddSingleton<IHangfireSchemaProbe, PostgreSqlHangfireSchemaProbe>();
        services.AddScoped<IReadinessCheck, HangfireStorageReadinessCheck>();
        services.AddScoped<IReadinessCheck, AdminRealtimeOutboxReadinessCheck>();
        services.AddScoped<IReadinessCheck, WorkerHeartbeatReadinessCheck>();

        return services;
    }

    public static IServiceCollection AddRomdAdminDiagnostics(this IServiceCollection services)
    {
        services.TryAddSingleton(new OperationalDiagnosticsOptions());
        services.TryAddSingleton<OperationalDiagnosticsAdmission>();
        services.AddScoped<IOperationalDiagnosticsReader, OperationalDiagnosticsReader>();
        services.AddSingleton<IHangfireDiagnosticsSource, HangfireDiagnosticsSource>();
        services.AddScoped<IHangfireDiagnosticsReader, HangfireDiagnosticsReader>();
        services.AddScoped<IStorageDiagnosticsReader, StorageDiagnosticsReader>();
        services.AddSingleton<IStorageDiagnosticsProbe, FileSystemStorageDiagnosticsProbe>();

        return services;
    }

    /// <summary>
    ///     Readiness for the consumer host: shared application checks plus the worker-published
    ///     Hangfire schema gate. The consumer remains free of Hangfire client/server services.
    /// </summary>
    public static IServiceCollection AddRomdConsumerReadinessChecks(this IServiceCollection services)
    {
        services.AddRomdReadinessEvaluation();
        services.TryAddSingleton<IHangfireSchemaProbe, PostgreSqlHangfireSchemaProbe>();
        services.AddScoped<IReadinessCheck, HangfireStorageReadinessCheck>();

        return services;
    }

    private static IServiceCollection AddRomdReadinessEvaluation(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new ReadinessOptions());
        services.TryAddSingleton<IReadinessEvaluator, ReadinessEvaluator>();
        services.AddScoped<IRomdSchemaProbe, RomdSchemaProbe>();
        services.AddScoped<IReadinessCheck, DatabaseReadinessCheck>();
        services.AddScoped<IReadinessCheck, ApplicationSchemaReadinessCheck>();
        services.AddScoped<IReadinessCheck, DiskSpaceReadinessCheck>();
        services.AddScoped<IReadinessCheck, ContentStorageReadinessCheck>();

        return services;
    }

    public static IServiceCollection AddRomdConsumerInfrastructure(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        return services
            .AddRomdServerInstanceIdentity()
            .AddRomdConsumerPersistence()
            .AddRomdConsumerApplicationHandlers()
            .AddRomdConsumerDelivery(configuration)
            .AddRomdReadOnlyStorage()
            .AddRomdNotifications()
            .AddRomdIdentityServices()
            .AddRomdConsumerReadinessChecks();
    }

    public static IServiceCollection AddRomdConsumerDelivery(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.AddOptions<ConsumerDeliveryOptions>()
            .Configure(options => configuration?.GetSection(ConsumerDeliveryOptions.SectionName).Bind(options));
        services.AddScoped<IConsumerContentGrantIssuer, ConsumerContentGrantService>();
        services.AddScoped<IConsumerBiosGrantIssuer, ConsumerContentGrantService>();

        return services;
    }

    /// <summary>
    ///     Adds infrastructure services configured for production use.
    ///     Registers all current single-host hosted services against the PostgreSQL database.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration? configuration = null)
    {
        return services
            .AddRomdServerInstanceIdentity()
            .AddRomdPersistence()
            .AddRomdApplicationHandlers()
            .AddRomdStorage()
            .AddRomdCurationServices(configuration)
            .AddRomdExportScheduling()
            .AddRomdHangfireJobPipeline()
            .AddRomdJobExecution()
            .AddRomdRealtimeOutboxPublishers()
            .AddRomdIdentityServices()
            .AddRomdAdminDiagnostics()
            .AddRomdDatabaseStartupInitialization()
            .AddRomdSeeders()
            .AddRomdRecurringJobs()
            .AddRomdLibraryMaterializationReconciler()
            .AddRomdJobDispatchWorker()
            .AddRomdCatalogProjectionRecovery();
    }

    /// <summary>
    ///     Adds infrastructure services configured for integration testing against a
    ///     PostgreSQL test database and skips hosted services that interfere with tests.
    /// </summary>
    public static IServiceCollection AddTestingInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        return services
            .AddRomdTestingPersistence(connectionString)
            .AddRomdApplicationHandlers()
            .AddRomdStorage()
            .AddRomdCurationServices()
            .AddRomdExportScheduling()
            .AddRomdHangfireJobPipeline()
            .AddRomdJobExecution()
            .AddRomdNotifications()
            .AddRomdIdentityServices()
            .AddRomdAdminDiagnostics();
    }

    private static IServiceCollection AddRomdPersistenceRepositories(this IServiceCollection services)
    {
        services.AddScoped<JobDispatchRepository>();
        services.AddScoped<JobDispatchService>();
        services.TryAddSingleton(new JobExecutionClaimOptions());
        services.AddScoped<IJobExecutionMutationGuard, JobExecutionMutationGuard>();
        services.TryAddSingleton<IConsumerReleaseSelector, ConsumerReleaseSelector>();
        services.AddScoped<DatRepository>();
        services.AddScoped<IDatRepository>(sp => sp.GetRequiredService<DatRepository>());
        services.AddScoped<ITitleSourceAssignmentStore>(sp => sp.GetRequiredService<DatRepository>());
        services.AddScoped<RomCatalogOwnershipReader>();
        services.AddScoped<IRomCatalogMatchReader>(sp => sp.GetRequiredService<RomCatalogOwnershipReader>());
        services.AddScoped<IRomOwnershipImpactReader>(sp => sp.GetRequiredService<RomCatalogOwnershipReader>());
        services.AddScoped<IRomPayloadAssertionImpactReader>(sp => sp.GetRequiredService<RomCatalogOwnershipReader>());
        services.AddScoped<IRomRepository, RomRepository>();
        services.AddScoped<IPlatformRepository, PlatformRepository>();
        services.AddScoped<ISystemSetupService, Romd.Persistence.Systems.SystemSetupService>();
        services.AddScoped<IPlatformAliasRepository, PlatformAliasRepository>();
        services.AddScoped<ITitleRepository, TitleRepository>();
        services.AddScoped<ITrackedTitleRepository, TrackedTitleRepository>();
        services.AddScoped<ITrackedCollectionReadRepository, TrackedCollectionReadRepository>();
        services.AddScoped<Romd.Admin.Application.Catalog.IBiosRepository, BiosRepository>();
        services.AddScoped<ICollectionRepository, CollectionRepository>();
        services.AddScoped<ISearchRepository, SearchRepository>();
        services.AddScoped<IUploadJobRepository, UploadJobRepository>();
        services.AddScoped<UploadJobRepository>();
        services.AddScoped<IJobRepository<UploadJob>, ClaimedJobRepository<UploadJob, UploadJobRepository>>();
        services.AddScoped<ReplaceDatJobRepository>();
        services.AddScoped<IJobRepository<ReplaceDatJob>, ClaimedJobRepository<ReplaceDatJob, ReplaceDatJobRepository>>();
        services.AddScoped<IReplaceDatJobRepository, ReplaceDatJobRepository>();
        services.AddScoped<IJobRepository<EnrichmentJob>, ClaimedJobRepository<EnrichmentJob, EnrichmentJobRepository>>();
        services.AddScoped<IEnrichmentJobRepository, EnrichmentJobRepository>();
        services.AddScoped<EnrichmentJobRepository>();
        services.AddScoped<IJobRepository<BulkEnrichmentJob>, ClaimedJobRepository<BulkEnrichmentJob, BulkEnrichmentJobRepository>>();
        services.AddScoped<IBulkEnrichmentJobRepository, BulkEnrichmentJobRepository>();
        services.AddScoped<BulkEnrichmentJobRepository>();
        services.AddScoped<IJobRepository<ExportJob>, ClaimedJobRepository<ExportJob, ExportJobRepository>>();
        services.AddScoped<ExportJobRepository>();
        services.AddScoped<IJobRepository<MaterializationJob>, ClaimedJobRepository<MaterializationJob, MaterializationJobRepository>>();
        services.AddScoped<IMaterializationJobRepository, MaterializationJobRepository>();
        services.AddScoped<MaterializationJobRepository>();
        services.AddScoped<ArtworkImportJobRepository>();
        services.AddScoped<IArtworkCurationRepository, ArtworkCurationRepository>();
        services.AddScoped<IJobRepository<ArtworkImportJob>, ClaimedJobRepository<ArtworkImportJob, ArtworkImportJobRepository>>();
        services.AddScoped<IJobRepository, JobRepository>();
        services.AddScoped<IJobItemRepository, JobItemRepository>();
        services.AddScoped<IExportRepository, ExportRepository>();
        services.AddScoped<IExportAuthorizationReader, ExportAuthorizationReader>();
        services.AddScoped<ILibraryRepository, LibraryRepository>();
        services.AddScoped<ILibraryExperienceRepository, LibraryExperienceRepository>();
        services.AddScoped<IArtworkReader, ArtworkReader>();
        services.AddScoped<IArtworkDelivery, ArtworkDeliveryService>();
        services.AddScoped<IReferenceBlobStore, Romd.Infrastructure.Storage.ReferenceBlobStore>();
        services.AddScoped<IReferenceCatalogService, ReferenceCatalogService>();
        services.AddScoped<IRegionReferenceReader, RegionReferenceReader>();
        services.AddScoped<ILanguageReferenceReader, LanguageReferenceReader>();
        services.AddScoped<IRatingBoardReferenceReader, RatingBoardReferenceReader>();
        services.AddScoped<IRatingReferenceReader, RatingReferenceReader>();

        services.AddScoped<ISystemReferenceReader, SystemReferenceReader>();
        services.AddScoped<ICompanyReferenceReader, CompanyReferenceReader>();
        services.AddScoped<ISystemReferenceRepository, SystemReferenceRepository>();
        services.AddScoped<ICompanyReferenceRepository, CompanyReferenceRepository>();
        services.AddScoped<IReferenceMutationSession, ReferenceMutationSession>();
        services.AddScoped<IConsumerBrowseRepository, ConsumerBrowseRepository>();
        services.AddScoped<IConsumerReleaseAccessRepository, ConsumerReleaseAccessRepository>();
        services.AddScoped<IConsumerReleaseManifestRepository, ConsumerReleaseManifestRepository>();
        services.AddScoped<IConsumerBiosRepository, ConsumerBiosRepository>();
        services.AddScoped<IConsumerCollectionReadRepository, CollectionRepository>();
        services.AddScoped<IConsumerLibraryContextRepository, LibraryRepository>();
        services.AddScoped<IConsumerPasswordPolicy, ConsumerPasswordPolicy>();
        services.AddScoped<ConsumerAccountService>();
        services.AddScoped<IConsumerPasswordChanger>(sp => sp.GetRequiredService<ConsumerAccountService>());
        services.AddScoped<IConsumerUserSettingsStore>(sp => sp.GetRequiredService<ConsumerAccountService>());
        services.AddScoped<IPlatformFieldDefaultRepository, PlatformFieldDefaultRepository>();
        services.AddScoped<AdminRealtimeOutbox>();
        services.AddScoped<IAdminRealtimeOutbox>(sp => sp.GetRequiredService<AdminRealtimeOutbox>());
        services.AddScoped<IAdminEventOutbox>(sp => sp.GetRequiredService<AdminRealtimeOutbox>());
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IReadSnapshotTransactionFactory, EfReadSnapshotTransactionFactory>();
        services.AddScoped<IUserAdministration, UserAdministration>();
        services.AddScoped<IAccountLifecycle, Romd.Persistence.Identity.AccountLifecycle>();
        services.AddScoped<Romd.Admin.Application.Taxonomy.Queries.GetMergeImpact.ITaxonomyMergeImpactReader, Romd.Persistence.Queries.TaxonomyMergeImpactReader>();
        services.AddScoped<Romd.Admin.Application.Users.Queries.GetUserDirectory.IUserDirectoryReader, Romd.Persistence.Queries.UserDirectoryReader>();
        services.AddScoped<Romd.Admin.Application.Users.Queries.GetAdminAudit.IAdminAuditReader, Romd.Persistence.Queries.AdminAuditReader>();
        services.AddScoped<IFileRepository, FileRepository>();
        services.AddScoped<IFileMutationLock, FileMutationLock>();
        services.AddScoped<IRegionRepository, RegionRepository>();
        services.AddScoped<ITaxonomyRepository<Region>, RegionRepository>();
        services.AddScoped<ILanguageRepository, LanguageRepository>();
        services.AddScoped<ITaxonomyRepository<GameLanguage>, LanguageRepository>();

        return services;
    }

    private static IServiceCollection AddRomdConsumerPersistenceRepositories(this IServiceCollection services)
    {
        services.TryAddSingleton<IConsumerReleaseSelector, ConsumerReleaseSelector>();
        services.AddScoped<IArtworkReader, ArtworkReader>();
        services.AddScoped<IArtworkDelivery, ArtworkDeliveryService>();
        services.AddScoped<IReferenceBlobStore, Romd.Infrastructure.Storage.ReferenceBlobStore>();
        services.AddScoped<IReferenceCatalogService, ReferenceCatalogService>();
        services.AddScoped<IRegionReferenceReader, RegionReferenceReader>();
        services.AddScoped<ILanguageReferenceReader, LanguageReferenceReader>();
        services.AddScoped<IRatingBoardReferenceReader, RatingBoardReferenceReader>();
        services.AddScoped<IRatingReferenceReader, RatingReferenceReader>();

        services.AddScoped<ISystemReferenceReader, SystemReferenceReader>();
        services.AddScoped<ICompanyReferenceReader, CompanyReferenceReader>();
        services.AddScoped<IConsumerBrowseRepository, ConsumerBrowseRepository>();
        services.AddScoped<IConsumerReleaseAccessRepository, ConsumerReleaseAccessRepository>();
        services.AddScoped<IConsumerReleaseManifestRepository, ConsumerReleaseManifestRepository>();
        services.AddScoped<IConsumerBiosRepository, ConsumerBiosRepository>();
        services.AddScoped<IConsumerCollectionReadRepository, CollectionRepository>();
        services.AddScoped<IConsumerLibraryContextRepository, LibraryRepository>();
        services.AddScoped<IConsumerPasswordPolicy, ConsumerPasswordPolicy>();
        services.AddScoped<ConsumerAccountService>();
        services.AddScoped<IConsumerPasswordChanger>(sp => sp.GetRequiredService<ConsumerAccountService>());
        services.AddScoped<IConsumerUserSettingsStore>(sp => sp.GetRequiredService<ConsumerAccountService>());
        services.AddScoped<IPlayActivityRepository, PlayActivityRepository>();
        services.AddScoped<IPlayActivityUnitOfWork, PlayActivityUnitOfWork>();

        return services;
    }

    private static IServiceCollection AddRomdAuditInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<JobAuditContext>();
        services.AddScoped<AuditInterceptor>();
        services.TryAddSingleton<SearchDocumentInterceptor>();
        services.AddScoped<IAuditContext>(sp =>
        {
            var httpAccessor = sp.GetService<IHttpContextAccessor>();
            if (httpAccessor?.HttpContext is not null)
            {
                return new HttpAuditContext(httpAccessor);
            }

            return sp.GetRequiredService<JobAuditContext>();
        });

        return services;
    }

    private static IServiceCollection AddRomdApplicationHandlersFromAssembly<TAssemblyMarker>(
        this IServiceCollection services)
    {
        services.Scan(scan => scan
            .FromAssemblyOf<TAssemblyMarker>()
            .AddClasses(classes => classes
                .AssignableTo(typeof(IQueryHandler<,>)))
            .AsImplementedInterfaces()
            .WithScopedLifetime()
            .AddClasses(classes => classes
                .AssignableTo(typeof(ICommandHandler<,>)))
            .AsImplementedInterfaces()
            .WithScopedLifetime());

        return services;
    }
}
