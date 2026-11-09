// src/Romd.Infrastructure/DependencyInjection.cs

using System.IO.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenIddict.EntityFrameworkCore;
using Romd.Admin.Application;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Artwork.Settings;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Collections;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Dashboard;
using Romd.Admin.Application.Diagnostics;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Ingestion.Classification;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.ReferenceData;
using Romd.Admin.Application.Search;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.IngestRom;
using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.Matching;
using Romd.Admin.Application.TrackedCollection;
using Romd.Admin.Application.Users;
using Romd.Application.Common.Artwork;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Readiness;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Application.Common.Security.Commands.StartAccountSession;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Access;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Activity;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Collections;
using Romd.Consumer.Application.Common.Persistence;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.Vfs;
using Romd.Dat.Parsing;
using Romd.Dat.Parsing.Formats.ClrMamePro;
using Romd.Dat.Parsing.Formats.Logiqx;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Domain.Libraries;
using Romd.Domain.Taxonomy;
using Romd.Infrastructure.Artwork;
using Romd.Infrastructure.Artwork.SteamGridDb;
using Romd.Infrastructure.Catalog;
using Romd.Infrastructure.Dashboard;
using Romd.Infrastructure.Dats;
using Romd.Infrastructure.Delivery;
using Romd.Infrastructure.Diagnostics;
using Romd.Infrastructure.Enrichment;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Jobs.Handlers;
using Romd.Infrastructure.Jobs.Lifecycle;
using Romd.Infrastructure.Jobs.Processors;
using Romd.Infrastructure.Libraries;
using Romd.Infrastructure.Readiness;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Resilience;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Upload;
using Romd.Media;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Libraries;
using Romd.Persistence.Queries;
using Romd.Persistence.Artwork;
using Romd.Persistence.Enrichment;
using Romd.Persistence.Identity;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.MetadataProviders;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.Persistence.Search;
using Romd.Persistence.Subscriptions;

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
        services.AddRomdAccountSessionServices();

        services.AddDbContext<RomdDbContext>((sp, options) =>
        {
            ConfigureRuntimeDatabase(sp, options);
            options.AddInterceptors(
                sp.GetRequiredService<AuditInterceptor>(),
                sp.GetRequiredService<SearchDocumentInterceptor>());
        });

        return services.AddRomdPersistenceRepositories();
    }

    /// <summary>Local recovery composition; no HTTP, provisioning, or hosted startup.</summary>
    public static IServiceCollection AddRomdLocalAdminRecovery(this IServiceCollection services)
    {
        services.AddRomdPersistence();
        // Issuance needs Identity's account stores, not its HTTP or token-provider startup.
        services.AddIdentityCore<RomdUser>(RomdIdentityOptions.Configure)
            .AddRoles<RomdIdentityRole>()
            .AddEntityFrameworkStores<RomdDbContext>()
            .AddPasswordValidator<RomdPasswordLengthValidator>();
        services.AddScoped<IRomdSchemaProbe, RomdSchemaProbe>();
        services.AddScoped<Romd.Persistence.Identity.LocalAdminRecovery>();
        return services;
    }

    public static IServiceCollection AddRomdConsumerPersistence(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddRomdAuditInfrastructure();
        services.AddRomdOpenIddictCore();
        services.AddRomdAccountSessionServices();
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
        services.AddRomdAccountSessionServices();

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
            .AddRomdWorkerApplicationHandlers()
            .AddRomdConsumerApplicationHandlers();

    public static IServiceCollection AddRomdAdminApplicationHandlers(this IServiceCollection services) =>
        services.AddRomdAuthenticationApplicationHandlers()
            .AddRomdApplicationHandlersFromAssembly<AdminApplicationAssembly>(includeRomIngestion: false);

    public static IServiceCollection AddRomdWorkerApplicationHandlers(this IServiceCollection services) =>
        services.AddRomdApplicationHandlersFromAssembly<AdminApplicationAssembly>();

    public static IServiceCollection AddRomdConsumerApplicationHandlers(this IServiceCollection services) =>
        services.AddRomdAuthenticationApplicationHandlers()
            .AddRomdApplicationHandlersFromAssembly<ConsumerApplicationAssembly>();

    private static IServiceCollection AddRomdAuthenticationApplicationHandlers(this IServiceCollection services)
    {
        services.TryAddScoped<ICommandHandler<StartAccountSessionCommand, AccountSession>, StartAccountSessionCommandHandler>();
        return services;
    }

    private static IServiceCollection AddRomdAccountSessionServices(this IServiceCollection services)
    {
        services.TryAddScoped<Romd.Persistence.Identity.AccountSessionStore>();
        services.TryAddScoped<IAccountSessions>(sp => sp.GetRequiredService<Romd.Persistence.Identity.AccountSessionStore>());
        services.TryAddScoped<IAccountSessionCreationStore>(sp => sp.GetRequiredService<Romd.Persistence.Identity.AccountSessionStore>());
        services.TryAddScoped<IAccountSessionTransaction>(sp => new Romd.Persistence.Identity.AccountSessionTransaction(
            new EfUnitOfWork(sp.GetRequiredService<RomdDbContext>()),
            sp.GetRequiredService<ILogger<Romd.Persistence.Identity.AccountSessionTransaction>>()));
        return services;
    }

    public static IServiceCollection AddRomdStorage(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddRomdChdAssets(requireTool: false);
        services.AddScoped<IRomContentCatalog, RomContentCatalog>();
        services.AddScoped<RomContentReader>();
        services.AddScoped<IRomContentReader>(sp => sp.GetRequiredService<RomContentReader>());
        services.AddScoped<IMediaFactReader, MediaFactReader>();
        services.AddScoped<IDerivedDescriptorReader, DerivedDescriptorReader>();
        services.AddScoped<IRomCdArchiveDownloadCatalog, RomCdArchiveDownloadCatalog>();
        services.AddScoped<IRomCdArchiveDownloadReader, RomCdArchiveDownloadReader>();
        services.AddRomdMediaReads();
        services.AddSingleton<IFileSystem, FileSystem>();
        services.AddSingleton(sp =>
        {
            var limits = new TempStorageOptions();
            sp.GetService<IConfiguration>()?.GetSection("Romd:TempStorage").Bind(limits);
            return limits;
        });
        services.AddSingleton<LocalTempFileFactory>();
        services.AddSingleton<ITempFileFactory>(sp => sp.GetRequiredService<LocalTempFileFactory>());
        services.AddSingleton<IAggregateTempFileFactory>(sp => sp.GetRequiredService<LocalTempFileFactory>());
        services.AddScoped<IFileStorageService, FileStorageService>();
        services.AddScoped<IExportArchiveWriter, Romd.Infrastructure.Export.ExportArchiveWriter>();
        services.AddSingleton<IAdminDownloadTickets, Romd.Infrastructure.Export.AdminDownloadTickets>();
        services.AddSingleton<IHashingService, HashingService>();
        // Built during composition so an invalid media policy or encoding registration fails host startup.
        var media = RomMediaPolicyComposition.Create();
        services.AddSingleton(media);
        services.AddSingleton(media.Kinds);
        services.AddSingleton(media.Registry);
        services.AddSingleton(media.Reconciliation);
        services.AddSingleton(RomEncodingComposition.CreateRegistry());
        services.AddSingleton(media.CdRecipes);
        services.AddScoped<IRomCdDescriptorIdentityReader, RomCdDescriptorIdentityReader>();
        services.AddSingleton<IArchiveExtractor, ZipArchiveExtractor>();
        services.AddSingleton<IArchiveExtractor, SevenZipArchiveExtractor>();
        services.AddSingleton<IArchiveExtractorResolver, ArchiveExtractorResolver>();

        return services;
    }

    public static IServiceCollection AddRomdWorkerMediaProcessing(this IServiceCollection services)
    {
        services.AddRomdChdAssets(requireTool: true);
        services.AddSingleton(sp =>
        {
            var options = new MediaProcessingOptions();
            sp.GetService<IConfiguration>()?.GetSection("Romd:Media").Bind(options);
            return options.ValidatedCopy();
        });
        services.AddSingleton<MediaProcessingRuntime>();
        services.AddSingleton<IMediaRunCustody>(sp => sp.GetRequiredService<MediaProcessingRuntime>());
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<MediaProcessingOptions>();
            var runtime = sp.GetRequiredService<MediaProcessingRuntime>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<MediaWorkerSupervisor>>();
            var assets = sp.GetRequiredService<ChdDeploymentRuntime>().Assets;
            return assets is null ? new MediaWorkerSupervisor(options, runtime, logger) :
                new MediaWorkerSupervisor(options, runtime, assets.Native, logger);
        });
        services.AddSingleton<IMediaWorkerSupervisor>(sp => sp.GetRequiredService<MediaWorkerSupervisor>());
        services.AddSingleton(sp => new RvzVerifier(sp.GetRequiredService<MediaProcessingOptions>(),
            sp.GetRequiredService<IMediaWorkerSupervisor>(), sp.GetRequiredService<MediaProcessingRuntime>()));
        services.AddSingleton(sp =>
        {
            var native = sp.GetRequiredService<ChdDeploymentRuntime>().Assets?.Native
                ?? throw new NotSupportedException("CHD preparation requires configured native assets.");
            return new ChdVerifier(sp.GetRequiredService<MediaProcessingOptions>(),
                sp.GetRequiredService<IMediaWorkerSupervisor>(), sp.GetRequiredService<MediaProcessingRuntime>(), native);
        });
        services.AddSingleton(sp =>
        {
            var native = sp.GetRequiredService<ChdDeploymentRuntime>().Assets?.Native
                ?? throw new NotSupportedException("CD inspection requires configured native assets.");
            return new ChdCdImportInspector(sp.GetRequiredService<MediaProcessingOptions>(),
                sp.GetRequiredService<IMediaWorkerSupervisor>(), sp.GetRequiredService<MediaProcessingRuntime>(), native,
                sp.GetRequiredService<TimeProvider>());
        });
        services.AddSingleton<IRomOriginalMediaIdentifier, RomOriginalMediaIdentifier>();
        services.AddScoped<IMediaRepresentationCleanup, MediaRepresentationCleanup>();
        services.AddScoped<RomCdArchivePublisher>();
        services.AddScoped<IRomCdCueGenerator, RomCdCueGenerator>();
        services.AddScoped<IRomCdImportStore, RomCdImportStore>();
        services.AddScoped<IRomCdImportPublication, RomCdImportPublication>();
        services.AddScoped<IRomCdIngestor, RomCdIngestor>();
        services.AddScoped<RomRepresentationPublisher>();
        services.AddScoped<IMediaFactPublisher, MediaFactPublisher>();
        services.AddScoped<ICatalogCdLayoutStore, CatalogCdLayoutStore>();
        services.AddScoped<IRomCdImportCatalogReader, Romd.Persistence.Catalog.RomCdImportCatalogReader>();
        services.AddScoped<IRomMediaObservationStore, RomMediaObservationStore>();
        services.AddScoped<IRomMediaCreationStore, RomMediaCreationStore>();
        services.AddScoped<RomCdMediaVerification>();
        services.AddScoped<IRomMediaVerification>(sp => new RomMediaVerification(
            sp.GetRequiredService<IAggregateTempFileFactory>(), sp.GetRequiredService<IMediaFactReader>(),
            sp.GetRequiredService<IFileStorageService>(), sp.GetRequiredService<MediaProcessingRuntime>(),
            sp.GetRequiredService<MediaProcessingOptions>(), () => sp.GetRequiredService<ChdVerifier>(),
            () => sp.GetRequiredService<RvzVerifier>(), sp.GetRequiredService<RomCdMediaVerification>()));
        services.AddSingleton(sp =>
        {
            var deployment = sp.GetRequiredService<ChdDeploymentRuntime>();
            if (!deployment.IsReady || deployment.Assets?.Tool is not { } tool)
                throw new NotSupportedException("Native encoding requires verified tool assets.");
            return new ChdArtifactEncoder(sp.GetRequiredService<IMediaWorkerSupervisor>(), sp.GetRequiredService<MediaProcessingOptions>(), tool);
        });
        services.AddScoped<IMediaReconciliationMediaProfile>(sp => new MediaReconciliationMediaProfile(sp.GetRequiredService<MediaProcessingOptions>(),
            sp.GetRequiredService<TempStorageOptions>(), sp.GetRequiredService<IRomdOptions>(), sp.GetRequiredService<ChdDeploymentRuntime>(),
            sp.GetRequiredService<RomMediaComposition>(), sp.GetRequiredService<IMediaHostCapabilities>()));
        services.AddScoped<MediaReconciliationDecision>(sp => new(sp.GetRequiredService<RomMediaComposition>().Kinds, sp.GetRequiredService<RomMediaComposition>().Reconciliation,
            sp.GetRequiredService<IMediaHostCapabilities>(), sp.GetRequiredService<IMediaReconciliationMediaProfile>()));
        services.AddScoped<IMediaReconciliationSnapshotReader>(sp => new MediaReconciliationSnapshotReader(sp.GetRequiredService<DbContextOptions<RomdDbContext>>(),
            sp.GetRequiredService<RomMediaComposition>().Kinds, sp.GetRequiredService<IRomCdDescriptorIdentityReader>()));
        services.AddScoped<IMediaReconciliationPlanning, MediaReconciliationPlanning>();
        services.AddScoped<IRomOriginalMediaInspection, RomOriginalMediaInspection>();
        services.AddScoped<RomWholeReconciliationEncoding>(sp => new(sp.GetRequiredService<IAggregateTempFileFactory>(), sp.GetRequiredService<IFileStorageService>(),
            sp.GetRequiredService<MediaProcessingRuntime>(), sp.GetRequiredService<MediaProcessingOptions>(), sp.GetRequiredService<IRomOriginalMediaIdentifier>(),
            () => sp.GetRequiredService<RvzVerifier>(), () => sp.GetRequiredService<ChdVerifier>(), () => sp.GetRequiredService<ChdArtifactEncoder>(),
            sp.GetRequiredService<IMediaReconciliationMediaProfile>(), sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<RomCdReconciliationEncoding>();
        services.AddScoped<IMediaReconciliationAdoption, RomCdReconciliationAdoption>();
        services.AddScoped<IMediaReconciliationEncoding, MediaReconciliationEncoding>();
        services.AddScoped<MediaReconciliationExecution>(sp => new(sp.GetRequiredService<RomdDbContext>(), sp.GetRequiredService<DbContextOptions<RomdDbContext>>(),
            sp.GetRequiredService<IUnitOfWork>(), sp.GetRequiredService<IMediaRunCustody>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<RomMediaComposition>().Kinds, sp.GetRequiredService<MediaReconciliationDecision>()));
        services.AddScoped<IMediaReconciliationExecution>(sp => sp.GetRequiredService<MediaReconciliationExecution>());
        services.AddScoped<IMediaReconciliationPublicationStore>(sp => sp.GetRequiredService<MediaReconciliationExecution>());
        services.AddScoped<IMediaReconciliationPublication, MediaReconciliationPublication>();
        services.AddScoped<IMediaReconciliationSelections>(sp => new MediaReconciliationSelections(sp.GetRequiredService<RomdDbContext>(),
            sp.GetRequiredService<DbContextOptions<RomdDbContext>>(), sp.GetRequiredService<IUnitOfWork>(), sp.GetRequiredService<IMediaReconciliationRequests>(),
            sp.GetRequiredService<IMediaReconciliationSnapshotReader>(), sp.GetRequiredService<RomMediaComposition>().Kinds,
            sp.GetRequiredService<IMediaRunCustody>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<RvzEncodingService>();
        services.AddSingleton(sp => new MediaWorkerStartupValidator(sp.GetRequiredService<IMediaWorkerSupervisor>(),
            sp.GetRequiredService<MediaWorkerSupervisor>(), sp.GetRequiredService<ChdDeploymentRuntime>()));
        services.AddHostedService<MediaWorkerStartupCheck>();
        services.AddSingleton(sp => new RomPreparationAdapterRegistry(RomEncodingComposition.Entries, new RomPreparationDependencies(
            sp.GetRequiredService<IHashingService>(), () => sp.GetRequiredService<RvzVerifier>(),
            () => sp.GetRequiredService<IRomOriginalMediaIdentifier>(), () => sp.GetRequiredService<ChdVerifier>(),
            () => sp.GetRequiredService<ChdCdImportInspector>())));
        services.AddScoped<IRomPreparationService>(sp => new RomPreparationService(sp.GetRequiredService<RomPreparationAdapterRegistry>(),
            sp.GetRequiredService<RomPs2DvdEligibilityResolver>()));
        return services;
    }

    private static IServiceCollection AddRomdMediaReads(this IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
        {
            var limits = new ReadDecoderOptions();
            sp.GetService<IConfiguration>()?.GetSection("Romd:MediaRead").Bind(limits);
            limits.Validate();
            return limits;
        });
        services.TryAddSingleton(sp => new MediaBlockCache(sp.GetRequiredService<ReadDecoderOptions>().CacheBytes));
        services.TryAddSingleton<IRomChdDeliveryAdmission>(sp => new ChdDeliveryAdmission(sp.GetRequiredService<ChdDeploymentRuntime>(),
            sp.GetRequiredService<IMediaHostCapabilities>(), RomMediaPolicyComposition.Create().Kinds));
        services.TryAddSingleton(RomMediaPolicyComposition.CreateDeliveryPolicy());
        services.TryAddSingleton(sp =>
        {
            var native = sp.GetRequiredService<ChdDeploymentRuntime>().Assets?.Native
                ?? throw new NotSupportedException("CHD reading requires configured native assets.");
            return new ChdMediaAdapter(native, sp.GetRequiredService<MediaBlockCache>(),
                sp.GetRequiredService<ReadDecoderOptions>());
        });
        services.TryAddSingleton(sp => new RomMediaAdapterRegistry(RomEncodingComposition.Entries, new RomReadDependencies(
            sp.GetRequiredService<ChdDeploymentRuntime>().Assets is null ? null : sp.GetRequiredService<ChdMediaAdapter>())));
        services.TryAddSingleton<IRomRepresentationDecoder>(sp => new RomRepresentationDecoder(
            sp.GetRequiredService<RomMediaAdapterRegistry>(), sp.GetRequiredService<ReadDecoderOptions>(),
            sp.GetRequiredService<MediaBlockCache>()));
        return services;
    }

    public static IServiceCollection AddRomdReadOnlyStorage(this IServiceCollection services)
    {
        services.AddRomdChdAssets(requireTool: false);
        services.AddScoped<IRomContentCatalog, RomContentCatalog>();
        services.AddScoped<RomContentReader>();
        services.AddScoped<IRomContentReader>(sp => sp.GetRequiredService<RomContentReader>());
        services.AddScoped<IMediaFactReader, MediaFactReader>();
        services.AddScoped<IDerivedDescriptorReader, DerivedDescriptorReader>();
        services.AddScoped<IRomCdArchiveDownloadCatalog, RomCdArchiveDownloadCatalog>();
        services.AddScoped<IRomCdArchiveDownloadReader, RomCdArchiveDownloadReader>();
        services.AddRomdMediaReads();
        services.AddScoped<IConsumerDerivedDescriptorSource, ConsumerDerivedDescriptorSource>();
        services.AddScoped<IConsumerVfsContentResolver, ConsumerVfsContentResolver>();
        services.AddScoped<IConsumerContentArtifactResolver, ConsumerContentArtifactResolver>();
        services.AddScoped<IConsumerMediaMetadataReader, ConsumerMediaMetadataReader>();
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
            .AddRomdStorage(configuration)
            .AddRomdAdminCurationServices(configuration)
            .AddRomdRealtimeOutboxPublishers()
            .AddRomdIdentityServices()
            .AddRomdAdminDiagnostics()
            .AddRomdAdminReadinessChecks();

    public static IServiceCollection AddRomdWorkerInfrastructure(
        this IServiceCollection services,
        IConfiguration? configuration = null) =>
        services
            .AddRomdWorkerMediaProcessing()
            .AddRomdServerInstanceIdentity()
            .AddRomdAdminPersistence()
            .AddRomdWorkerApplicationHandlers()
            .AddRomdStorage(configuration)
            .AddRomdWorkerCurationServices(configuration)
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
            .AddRomdJobDispatchWorker()
            .AddRomdCatalogProjectionRecovery()
            .AddRomdContentStoreStartupCleanup();

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
        services.AddScoped<ITaxonomyResolver<Region>>(sp =>
            new TaxonomyResolver<Region>(
                sp.GetRequiredService<ITaxonomyRepository<Region>>(),
                sp.GetRequiredService<IUnitOfWork>(),
                entityFactory: token => Region.CreateNew(token, sortOrder: 999, isAutoCreated: true)));
        services.AddScoped<ITaxonomyResolver<GameLanguage>>(sp =>
            new TaxonomyResolver<GameLanguage>(
                sp.GetRequiredService<ITaxonomyRepository<GameLanguage>>(),
                sp.GetRequiredService<IUnitOfWork>(),
                entityFactory: token => GameLanguage.CreateNew(token, token.ToLowerInvariant(), sortOrder: 999, isAutoCreated: true)));

        services.AddScoped<TaxonomyService<Region>>();
        services.AddScoped<TaxonomyService<GameLanguage>>();

        services.AddScoped<ITitleMatcher, TitleMatcher>();
        services.AddScoped<ITitleDerivationService, TitleDerivationService>();
        services.AddScoped<ITitleSourceReferenceReader, TitleSourceReferenceReader>();
        services.AddScoped<ISourceLifecycle, SourceLifecycleStore>();
        services.AddScoped<Romd.Persistence.Catalog.DatSourceManagement>();
        services.AddScoped<Romd.Admin.Application.Catalog.Sources.IDatSourceManagement>(sp => sp.GetRequiredService<Romd.Persistence.Catalog.DatSourceManagement>());
        services.AddScoped<Romd.Admin.Application.Catalog.Sources.IDatSourceLifecycleStore>(sp => sp.GetRequiredService<Romd.Persistence.Catalog.DatSourceManagement>());
        services.AddScoped<Romd.Persistence.Catalog.DiscSetService>();
        services.AddScoped<IDiscSetService>(sp => sp.GetRequiredService<Romd.Persistence.Catalog.DiscSetService>());
        services.AddScoped<IDiscSetDecisionStore>(sp => sp.GetRequiredService<Romd.Persistence.Catalog.DiscSetService>());
        services.AddScoped<IDiscSetProposalStore>(sp => sp.GetRequiredService<Romd.Persistence.Catalog.DiscSetService>());
        services.AddScoped<Romd.Persistence.Catalog.OriginalDiscLayoutService>();
        services.AddScoped<IOriginalDiscLayoutService>(sp => sp.GetRequiredService<Romd.Persistence.Catalog.OriginalDiscLayoutService>());
        services.AddScoped<IOriginalDiscDescriptorRepository>(sp => sp.GetRequiredService<Romd.Persistence.Catalog.OriginalDiscLayoutService>());
        services.AddScoped<Romd.Application.Common.Content.IOriginalDiscLayoutReader>(sp => sp.GetRequiredService<Romd.Persistence.Catalog.OriginalDiscLayoutService>());
        services.AddScoped<IOriginalDiscDescriptorIntake, Romd.Infrastructure.Source.OriginalDiscDescriptorIntake>();
        services.AddScoped<Romd.Admin.Application.Catalog.Sources.ISourceEntryReader, Romd.Persistence.Catalog.SourceEntryReader>();
        services.AddScoped<Romd.Admin.Application.Catalog.IBiosGrouper, Romd.Admin.Application.Catalog.BiosGrouper>();
        services.AddScoped<IPlatformHeaderResolver, PlatformHeaderResolver>();

        services.AddScoped<IDatReader, DatReader>();
        services.AddScoped<DatReplacementReview>();
        services.AddScoped<IDatReplacementReview>(sp => sp.GetRequiredService<DatReplacementReview>());
        services.AddScoped<IReviewedDatReplacementPreparation>(sp => sp.GetRequiredService<DatReplacementReview>());
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
        services.AddScoped<Romd.Admin.Application.ReferenceData.Assets.IReferenceAssetReservationStore, ReferenceAssetReservationStore>();
        services.AddScoped<ISignedDatCatalogClient, SignedDatCatalogClient>();
        services.AddScoped<DatCatalogEnrollmentService>();
        services.AddScoped<IDatCatalogEnrollmentService>(provider => provider.GetRequiredService<DatCatalogEnrollmentService>());
        services.AddScoped<IDatSubscriptionStopStore>(provider => provider.GetRequiredService<DatCatalogEnrollmentService>());
        services.AddScoped<IDatSubscriptionCheckReader>(provider => provider.GetRequiredService<DatCatalogEnrollmentService>());
        services.AddScoped<IDatSubscriptionCheckStore>(provider => provider.GetRequiredService<DatCatalogEnrollmentService>());
        services.AddScoped<IDatSubscriptionApplyStore>(provider => provider.GetRequiredService<DatCatalogEnrollmentService>());
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

        services.AddScoped<IJobWorkspaceExclusion, JobWorkspaceExclusion>();
        services.AddScoped<IUploadWorkspacePreparation, UploadWorkspacePreparation>();
        services.AddScoped<IReplaceDatWorkspacePreparation, ReplaceDatWorkspacePreparation>();
        services.AddScoped<IPathValidator, Import.PathValidator>();
        services.AddScoped<IPathImportPreparation, Import.PathImportPreparation>();
        services.AddScoped<IEnrichmentJobEnqueuer, EnrichmentJobEnqueuer>();
        services.AddScoped<IRematerializationScheduler, RematerializationScheduler>();
        services.AddScoped<MetadataRematerializationJobRepository>();
        services.AddScoped<IMetadataRematerializationJobRepository>(sp => sp.GetRequiredService<MetadataRematerializationJobRepository>());
        services.AddScoped<IJobRepository<MetadataRematerializationJob>, ClaimedJobRepository<MetadataRematerializationJob, MetadataRematerializationJobRepository>>();
        services.AddScoped<IMetadataRematerializationTitles, MetadataRematerializationTitles>();
        services.AddScoped<IMaterializationJobEnqueuer, MaterializationJobEnqueuer>();

        services.AddScoped<IFileClassifier, FileClassifier>();

        services.TryAddScoped<IMetadataRematerializer, TitleMetadataRematerializer>();

        services.AddRomdMetadataProviderServices(configuration);
        services.AddRomdArtworkServices(configuration);

        return services;
    }

    private static IServiceCollection AddRomdArtworkServices(this IServiceCollection services, IConfiguration? configuration)
    {
        // Combined admin/worker hosts may compose curation twice. Provider registries
        // require one registration per identity, including their shared settings.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IArtworkBrowsingService)))
            return services;
        services.AddOptions<SteamGridDbOptions>().Configure(options =>
            configuration?.GetSection(SteamGridDbOptions.SectionName).Bind(options));
        services.AddHttpClient("SteamGridDbConnectionTest")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<ISteamGridDbSettingsStore, SteamGridDbSettingsStore>();
        services.AddScoped<SteamGridDbSettingsService>();
        services.AddScoped<ISteamGridDbSettingsService>(sp => sp.GetRequiredService<SteamGridDbSettingsService>());
        services.AddScoped<ISteamGridDbCredentials>(sp => sp.GetRequiredService<SteamGridDbSettingsService>());
        services.AddSingleton<SteamGridDbRateGate>();
        services.AddSingleton<SteamGridDbRequestGate>();
        services.AddScoped<SteamGridDbArtworkProvider>();
        services.AddScoped<IProviderIdentityAdapter, SteamGridDbIdentityAdapter>();
        services.AddScoped<IProviderIdentityAdapter>(sp => sp.GetRequiredService<IgdbMetadataProvider>());
        services.AddScoped<ITitleProviderMatchStore, TitleProviderMatchStore>();
        services.AddScoped<ITitleProviderMatchService, TitleProviderMatchService>();
        services.AddScoped<ITitleEnrichmentEvidenceReader, TitleEnrichmentEvidenceReader>();
        services.AddScoped<IProviderIdentityStore, ProviderIdentityStore>();
        services.AddScoped<IProviderIdentityService, ProviderIdentityService>();
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
        services.AddScoped<IArtworkAcquisitionStore, ArtworkAcquisitionStore>();
        services.AddScoped<Romd.Admin.Application.Enrichment.IEnrichmentWorkbench, Romd.Persistence.Enrichment.EnrichmentWorkbench>();
        services.AddScoped<Romd.Persistence.Artwork.ArtworkCompletionStore>();
        services.AddScoped<IArtworkCompletionStore>(sp => sp.GetRequiredService<Romd.Persistence.Artwork.ArtworkCompletionStore>());
        services.AddScoped<IArtworkCompletionAcceptance>(sp => sp.GetRequiredService<Romd.Persistence.Artwork.ArtworkCompletionStore>());
        services.AddScoped<IArtworkCompletionService, Romd.Admin.Application.Artwork.ArtworkCompletionService>();
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
        // Admin and worker curation may be composed together. Installing the named
        // HTTP resilience handler twice would nest retries and multiply provider calls.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(MetadataProviderServicesRegistration)))
            return services;
        services.AddSingleton(new MetadataProviderServicesRegistration());

        services.AddResiliencePolicies();

        services.AddHttpClient("IgdbConnectionTest");
        services.AddScoped<IIgdbProviderSettingsStore, IgdbProviderSettingsStore>();
        services.AddScoped<IgdbProviderSettingsService>();
        services.AddScoped<IIgdbProviderSettingsService>(sp => sp.GetRequiredService<IgdbProviderSettingsService>());

        services.AddHttpClient(nameof(IgdbMetadataProvider))
            .AddIgdbResilienceHandler();

        services.AddOptions<IgdbProviderOptions>()
            .Configure(options =>
            {
                configuration?.GetSection(IgdbProviderOptions.SectionName).Bind(options);
            });
        services.AddSingleton<MatchConfidenceCalculator>();
        services.AddSingleton<IProviderRateLimiter, ProviderRateLimiter>();
        services.TryAddSingleton<IgdbAccessTokenCache>();

        services.AddScoped(sp => ActivatorUtilities.CreateInstance<IgdbMetadataProvider>(sp,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IgdbMetadataProvider))));
        services.AddScoped<IMetadataProvider>(sp => sp.GetRequiredService<IgdbMetadataProvider>());

        return services;
    }

    private sealed class MetadataProviderServicesRegistration;

    private static IServiceCollection AddRomdEnrichmentExecutionServices(this IServiceCollection services)
    {
        services.AddHttpClient<IEnrichmentMediaStager, HttpEnrichmentMediaStager>();
        services.AddMemoryCache();
        services.AddSingleton<ArtworkCandidateFailureCache>();
        services.AddScoped<IAutomaticArtworkService, LinkedProviderArtworkService>();
        services.AddScoped<IArtworkIdentityBridge, SteamArtworkIdentityBridge>();
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
        services.AddScoped<ArtworkCompletionJobHangfireHandler>();
        services.AddScoped<IJobExecutor<ArtworkCompletionJob>, ArtworkCompletionJobExecutor>();
        services.AddScoped<MetadataRematerializationJobHangfireHandler>();
        services.AddScoped<IJobExecutor<MetadataRematerializationJob>, MetadataRematerializationJobExecutor>();
        services.AddScoped<ArtworkCompletionJobRepository>();
        services.AddScoped<IJobRepository<ArtworkCompletionJob>, ClaimedJobRepository<ArtworkCompletionJob, ArtworkCompletionJobRepository>>();
        services.AddScoped<ExportJobHangfireHandler>();
        services.AddScoped<MaterializationJobHangfireHandler>();
        services.AddScoped<ArtworkImportJobHangfireHandler>();
        services.AddScoped<MediaReconciliationJobHangfireHandler>();

        return services;
    }

    public static IServiceCollection AddRomdJobExecution(this IServiceCollection services)
    {
        services.AddScoped<Import.ImportSourceCleanup>();
        services.AddSingleton(new Import.ArchiveExtractionLimits());
        services.AddScoped<ArchiveExtractionStep>();
        services.AddScoped<IJobExecutor<UploadJob>, UploadJobExecutor>();
        services.AddScoped<IJobExecutor<ReplaceDatJob>, ReplaceDatJobExecutor>();
        services.AddScoped<ITitleEnrichmentPipeline, TitleEnrichmentPipeline>();
        services.AddScoped<IJobExecutor<EnrichmentJob>, EnrichmentJobExecutor>();
        services.AddScoped<BulkEnrichmentTitleProcessor>();
        services.AddScoped<IJobExecutor<BulkEnrichmentJob>, BulkEnrichmentJobExecutor>();
        services.AddScoped<IJobExecutor<MaterializationJob>, MaterializationJobExecutor>();
        services.AddScoped<IJobExecutor<ArtworkImportJob>, ArtworkImportJobExecutor>();
        services.AddScoped<IJobExecutor<MediaReconciliationJob>, MediaReconciliationJobExecutor>();

        return services
            .AddRomdJobLifecyclePolicies()
            .AddRomdExportExecution();
    }

    /// <summary>
    ///     Lifecycle capabilities consumed by <see cref="JobRunner{TJob}" />. Every capability falls back
    ///     to <see cref="DefaultJobLifecyclePolicy{TJob}" />; a family registers closed overrides for the
    ///     capabilities it needs. Execution-only: API hosts never compose this.
    /// </summary>
    public static IServiceCollection AddRomdJobLifecyclePolicies(this IServiceCollection services)
    {
        services.TryAddSingleton(typeof(IJobRetryBudgetPolicy<>), typeof(DefaultJobLifecyclePolicy<>));
        services.TryAddSingleton(typeof(IJobCancellationPolicy<>), typeof(DefaultJobLifecyclePolicy<>));
        services.TryAddSingleton<IJobCancellationPolicy<MetadataRematerializationJob>, MetadataRematerializationCancellationPolicy>();
        services.TryAddSingleton(typeof(IJobTerminationPolicy<>), typeof(DefaultJobLifecyclePolicy<>));
        services.TryAddSingleton(typeof(IJobCompletionCleanup<>), typeof(DefaultJobLifecyclePolicy<>));

        var retryAge = TimeSpan.FromDays(1);
        services.TryAddSingleton<IJobRetryBudgetPolicy<UploadJob>>(new JobRetryBudgetPolicy<UploadJob>(
            new("Upload", retryAge, JobContext.MaxUploadDeferrals)));
        services.TryAddScoped<IJobCompletionCleanup<UploadJob>, MoveImportCompletionCleanup>();

        services.TryAddSingleton<IJobRetryBudgetPolicy<MediaReconciliationJob>>(new MediaReconciliationRetryBudgetPolicy(retryAge, JobContext.MaxUploadDeferrals));
        services.TryAddScoped<MediaReconciliationLifecyclePolicy>();
        services.TryAddScoped<IJobCancellationPolicy<MediaReconciliationJob>>(sp => sp.GetRequiredService<MediaReconciliationLifecyclePolicy>());
        services.TryAddScoped<IJobTerminationPolicy<MediaReconciliationJob>>(sp => sp.GetRequiredService<MediaReconciliationLifecyclePolicy>());

        return services;
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

    public static IServiceCollection AddRomdJobDispatchWorker(this IServiceCollection services)
    {
        services.AddHostedService<JobDispatchWorker>();
        // Worker composition calls this after schema provisioning and version publication.

        return services;
    }

    public static IServiceCollection AddRomdCatalogProjectionRecovery(this IServiceCollection services)
    {
        services.AddHostedService<CatalogProjectionRecoveryDispatcher>();

        return services;
    }

    public static IServiceCollection AddRomdContentStoreStartupCleanup(this IServiceCollection services)
    {
        services.AddHostedService<ContentStoreStartupCleanup>();

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
        services.AddScoped<IReadinessCheck, ChdAssetsReadinessCheck>();

        return services;
    }

    /// <summary>
    ///     Verifies configured native builds before host startup; only workers require the encoder. The host's media
    ///     capabilities derive from those builds, and only the worker's include encoding.
    /// </summary>
    public static IServiceCollection AddRomdChdAssets(this IServiceCollection services, bool requireTool)
    {
        services.TryAddSingleton(sp =>
        {
            var options = new NativeToolOptions();
            sp.GetService<IConfiguration>()?.GetSection(NativeToolOptions.SectionName).Bind(options);
            return options;
        });
        var descriptor = ServiceDescriptor.Singleton<ChdDeploymentRuntime>(sp =>
            new(sp.GetRequiredService<NativeToolOptions>(), requireTool));
        var capabilities = ServiceDescriptor.Singleton<IMediaHostCapabilities>(sp =>
            new MediaHostCapabilities(sp.GetRequiredService<ChdDeploymentRuntime>(), RomMediaPolicyComposition.Create(), requireTool));
        if (requireTool)
        { services.Replace(descriptor); services.Replace(capabilities); }
        else
        { services.TryAdd(descriptor); services.TryAdd(capabilities); }
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, ChdAssetsStartupCheck>());
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
        services.AddScoped<IConsumerOriginalLayoutGrantIssuer, ConsumerOriginalLayoutGrantService>();
        services.AddScoped<IConsumerDerivedDescriptorGrantIssuer, ConsumerContentGrantService>();
        services.AddScoped<IConsumerBiosGrantIssuer, ConsumerContentGrantService>();
        services.AddSingleton<IConsumerDiscSetDownloadTickets, ConsumerDiscSetDownloadTickets>();
        services.AddScoped<IConsumerDiscSetArchiveWriter, ConsumerDiscSetArchiveWriter>();

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
            .AddRomdStorage(configuration)
            .AddRomdWorkerMediaProcessing()
            .AddRomdCurationServices(configuration)
            .AddRomdHangfireJobPipeline()
            .AddRomdJobExecution()
            .AddRomdRealtimeOutboxPublishers()
            .AddRomdIdentityServices()
            .AddRomdAdminDiagnostics()
            .AddRomdDatabaseStartupInitialization()
            .AddRomdSeeders()
            .AddRomdRecurringJobs()
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
            .AddRomdWorkerMediaProcessing()
            .AddRomdCurationServices()
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
        services.AddScoped<IMediaFactPublisher, MediaFactPublisher>();
        services.AddScoped<IRomMediaObservationStore, RomMediaObservationStore>();
        services.AddScoped<IRomMediaCreationStore, RomMediaCreationStore>();
        services.AddScoped<IRomRepository, RomRepository>();
        services.AddScoped<IRomRetainedLogicalInspectionReader, RomRetainedLogicalInspectionReader>();
        services.AddScoped<IRomDiscMediaReferenceStore, RomDiscMediaReferenceStore>();
        services.AddScoped<RomPs2DvdEligibilityResolver>();
        services.AddScoped<IRomPs2CurrentReferenceAuthority, RomPs2CurrentReferenceAuthority>();
        services.AddScoped<IPlatformRepository, PlatformRepository>();
        services.AddScoped<ISystemSetupService, Romd.Persistence.Systems.SystemSetupService>();
        services.AddScoped<ISystemDatImportStore, Romd.Persistence.Systems.SystemDatImportStore>();
        services.AddScoped<IPlatformAliasRepository, PlatformAliasRepository>();
        // Aggregate and mutation roles share one scoped adapter; each port exposes only its role.
        services.AddScoped<TitleRepository>();
        services.AddScoped<ITitleAggregateReader>(sp => sp.GetRequiredService<TitleRepository>());
        services.AddScoped<ITitleMetadataStore>(sp => sp.GetRequiredService<TitleRepository>());
        services.AddScoped<ITitleRowLock>(sp => sp.GetRequiredService<TitleRepository>());
        services.AddScoped<ITitleMediaStore>(sp => sp.GetRequiredService<TitleRepository>());
        services.AddScoped<ITitleMergeStore>(sp => sp.GetRequiredService<TitleRepository>());
        services.AddScoped<ITitleCreationStore>(sp => sp.GetRequiredService<TitleRepository>());
        services.AddScoped<ITitleOrphanCleanup, TitleOrphanRepository>();
        // Read roles share one scoped adapter; none of them exposes a title write.
        services.AddScoped<TitleReadRepository>();
        services.AddScoped<ITitleReader>(sp => sp.GetRequiredService<TitleReadRepository>());
        services.AddScoped<ITitleEnrichmentStatusReader>(sp => sp.GetRequiredService<TitleReadRepository>());
        services.AddScoped<ITitleMediaReader>(sp => sp.GetRequiredService<TitleReadRepository>());
        services.AddScoped<ITitleDetailReader, Romd.Persistence.Queries.TitleDetailReader>();
        services.AddScoped<TrackedTitleRepository>();
        services.AddScoped<ITrackedTitleRepository>(sp => sp.GetRequiredService<TrackedTitleRepository>());
        services.AddScoped<ITrackedTitleReader>(sp => sp.GetRequiredService<TrackedTitleRepository>());
        services.AddScoped<ITrackedCollectionReadRepository, TrackedCollectionReadRepository>();
        services.AddScoped<Romd.Admin.Application.Catalog.IBiosRepository, BiosRepository>();
        services.AddScoped<ICollectionRepository, CollectionRepository>();
        services.AddScoped<ICollectionCoverMediaReader, Romd.Persistence.Queries.CollectionCoverMediaReader>();
        services.AddScoped<ISearchRepository, SearchRepository>();
        services.AddScoped<UploadJobRepository>();
        services.AddScoped<IUploadJobRepository>(sp => sp.GetRequiredService<UploadJobRepository>());
        services.AddScoped<IUploadJobAcceptance>(sp => sp.GetRequiredService<UploadJobRepository>());
        services.AddScoped<IJobRepository<UploadJob>, ClaimedJobRepository<UploadJob, UploadJobRepository>>();
        services.AddScoped<ReplaceDatJobRepository>();
        services.AddScoped<IJobRepository<ReplaceDatJob>, ClaimedJobRepository<ReplaceDatJob, ReplaceDatJobRepository>>();
        services.AddScoped<IReplaceDatJobRepository>(sp => sp.GetRequiredService<ReplaceDatJobRepository>());
        services.AddScoped<IReplaceDatJobAcceptance>(sp => sp.GetRequiredService<ReplaceDatJobRepository>());
        services.AddScoped<IJobRepository<EnrichmentJob>, ClaimedJobRepository<EnrichmentJob, EnrichmentJobRepository>>();
        services.AddScoped<IEnrichmentJobRepository, EnrichmentJobRepository>();
        services.AddScoped<EnrichmentJobRepository>();
        services.AddScoped<IEnrichmentJobAcceptance>(provider => provider.GetRequiredService<EnrichmentJobRepository>());
        services.AddScoped<IJobRepository<BulkEnrichmentJob>, ClaimedJobRepository<BulkEnrichmentJob, BulkEnrichmentJobRepository>>();
        services.AddScoped<IBulkEnrichmentJobRepository, BulkEnrichmentJobRepository>();
        services.AddScoped<BulkEnrichmentJobRepository>();
        services.AddScoped<IBulkEnrichmentJobAcceptance>(provider => provider.GetRequiredService<BulkEnrichmentJobRepository>());
        services.AddScoped<IJobRepository<ExportJob>, ClaimedJobRepository<ExportJob, ExportJobRepository>>();
        services.AddScoped<ExportJobRepository>();
        services.AddScoped<IExportArtifactCleanupStore>(provider => provider.GetRequiredService<ExportJobRepository>());
        services.AddScoped<IExportJobAcceptance>(provider => provider.GetRequiredService<ExportJobRepository>());
        services.AddScoped<IJobRepository<MaterializationJob>, ClaimedJobRepository<MaterializationJob, MaterializationJobRepository>>();
        services.AddScoped<IMaterializationJobRepository, MaterializationJobRepository>();
        services.AddScoped<MaterializationJobRepository>();
        services.AddScoped<ArtworkImportJobRepository>();
        services.AddScoped<IArtworkCurationRepository, ArtworkCurationRepository>();
        services.AddScoped<IJobRepository<ArtworkImportJob>, ClaimedJobRepository<ArtworkImportJob, ArtworkImportJobRepository>>();
        services.AddScoped<IJobRepository, JobRepository>();
        services.AddScoped<IJobCancellationStore, JobCancellationStore>();
        services.AddScoped<IJobDeliveryCancellation, HangfireJobDeliveryCancellation>();
        services.AddScoped<MediaReconciliationJobRepository>();
        services.AddScoped<IJobRepository<MediaReconciliationJob>, ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>>();
        services.AddScoped<IMediaReconciliationRequests, MediaReconciliationRequests>();
        services.AddScoped<IJobItemRepository, JobItemRepository>();
        services.AddScoped<IExportRepository, ExportRepository>();
        services.AddScoped<IReleaseDownloadRepository, ReleaseDownloadRepository>();
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
        services.AddScoped<Romd.Admin.Application.ReferenceData.Assets.IReferenceAssetReservationStore, ReferenceAssetReservationStore>();
        services.AddScoped<IConsumerBrowseRepository, ConsumerBrowseRepository>();
        services.AddScoped<IConsumerReleaseAccessRepository, ConsumerReleaseAccessRepository>();
        services.AddScoped<IConsumerReleaseManifestRepository, ConsumerReleaseManifestRepository>();
        services.AddScoped<IConsumerCdArchiveRepository, ConsumerCdArchiveRepository>();
        services.AddScoped<Romd.Consumer.Application.Delivery.IConsumerDiscSetRepository, Romd.Persistence.Repositories.ConsumerDiscSetRepository>();
        services.AddScoped<IConsumerBiosRepository, ConsumerBiosRepository>();
        services.AddScoped<IConsumerCollectionReadRepository, CollectionRepository>();
        services.AddScoped<IConsumerLibraryContextRepository, LibraryRepository>();
        services.AddScoped<IConsumerPasswordPolicy, ConsumerPasswordPolicy>();
        services.AddScoped<ConsumerAccountService>();
        services.AddScoped<IConsumerPasswordChanger>(sp => sp.GetRequiredService<ConsumerAccountService>());
        services.AddScoped<IConsumerUserSettingsStore, Romd.Persistence.Identity.ConsumerUserSettingsStore>();
        services.AddScoped<IConsumerTaxonomyKeys, ConsumerTaxonomyKeys>();
        services.AddScoped<IPlatformFieldDefaultRepository, PlatformFieldDefaultRepository>();
        services.AddScoped<AdminRealtimeOutbox>();
        services.AddScoped<IAdminRealtimeOutbox>(sp => sp.GetRequiredService<AdminRealtimeOutbox>());
        services.AddScoped<IAdminEventOutbox>(sp => sp.GetRequiredService<AdminRealtimeOutbox>());
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IReadSnapshotTransactionFactory, EfReadSnapshotTransactionFactory>();
        services.AddScoped<IUserAdministration, UserAdministration>();
        services.AddScoped<IAccountLifecycle, Romd.Persistence.Identity.AccountLifecycle>();
        services.AddScoped<Romd.Admin.Application.Users.Queries.GetUserDirectory.IUserDirectoryReader, Romd.Persistence.Queries.UserDirectoryReader>();
        services.AddScoped<Romd.Admin.Application.Users.Queries.GetAdminAudit.IAdminAuditReader, Romd.Persistence.Queries.AdminAuditReader>();
        services.AddScoped<IFileRepository, FileRepository>();
        services.AddScoped<IFileMutationLock, FileMutationLock>();
        services.AddScoped<Romd.Admin.Application.Titles.Queries.OpenTitleMedia.ITitleMediaFileReader, Romd.Persistence.Queries.TitleMediaFileReader>();
        services.AddScoped<IRegionRepository, RegionRepository>();
        services.AddScoped<ITaxonomyRepository<Region>, RegionRepository>();
        services.AddScoped<ILanguageRepository, LanguageRepository>();
        services.AddScoped<ITaxonomyRepository<GameLanguage>, LanguageRepository>();

        return services;
    }

    private static IServiceCollection AddRomdConsumerPersistenceRepositories(this IServiceCollection services)
    {
        services.AddScoped<IConsumerSecurityTransaction>(sp => new Romd.Persistence.Identity.ConsumerSecurityTransaction(
            new EfUnitOfWork(sp.GetRequiredService<RomdDbContext>()),
            sp.GetRequiredService<ILogger<Romd.Persistence.Identity.ConsumerSecurityTransaction>>()));
        services.AddScoped<IVfsConnections, Romd.Persistence.Identity.VfsConnectionStore>();
        services.AddScoped<IConsumerVfsRepository, ConsumerVfsRepository>();
        services.TryAddSingleton<IConsumerReleaseSelector, ConsumerReleaseSelector>();
        services.AddScoped<IArtworkReader, ArtworkReader>();
        services.AddScoped<IArtworkDelivery, ArtworkDeliveryService>();
        services.AddScoped<IReferenceBlobStore, Romd.Infrastructure.Storage.ReadOnlyReferenceBlobStore>();
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
        services.AddScoped<IConsumerCdArchiveRepository, ConsumerCdArchiveRepository>();
        services.AddScoped<IOriginalDiscLayoutReader, Romd.Persistence.Catalog.ConsumerOriginalDiscLayoutReader>();
        services.AddScoped<Romd.Consumer.Application.Delivery.IConsumerDiscSetRepository, Romd.Persistence.Repositories.ConsumerDiscSetRepository>();
        services.AddScoped<IConsumerBiosRepository, ConsumerBiosRepository>();
        services.AddScoped<IConsumerCollectionReadRepository, CollectionRepository>();
        services.AddScoped<IConsumerLibraryContextRepository, LibraryRepository>();
        services.AddScoped<IConsumerPasswordPolicy, ConsumerPasswordPolicy>();
        services.AddScoped<ConsumerAccountService>();
        services.AddScoped<IConsumerPasswordChanger>(sp => sp.GetRequiredService<ConsumerAccountService>());
        services.AddScoped<IConsumerUserSettingsStore, Romd.Persistence.Identity.ConsumerUserSettingsStore>();
        services.AddScoped<IConsumerTaxonomyKeys, ConsumerTaxonomyKeys>();
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
        this IServiceCollection services,
        bool includeRomIngestion = true)
    {
        services.Scan(scan => scan
            .FromAssemblyOf<TAssemblyMarker>()
            .AddClasses(classes => classes
                .AssignableTo(typeof(IQueryHandler<,>)))
            .AsImplementedInterfaces()
            .WithScopedLifetime()
            .AddClasses(classes => classes
                .AssignableTo(typeof(ICommandHandler<,>))
                .Where(type => includeRomIngestion || type != typeof(IngestRomCommandHandler)))
            .AsImplementedInterfaces()
            .WithScopedLifetime());

        return services;
    }
}
