using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>
///     Persistence-boundary guardrail for the PostgreSQL cutover. Three exact
///     allow-lists freeze every EF Core, database-provider, and <c>RomdDbContext</c>
///     reference outside the <c>Romd.Persistence</c> project, every raw SQL site in
///     runtime source, and every runtime project that references an EF Core or provider
///     package. A new, moved, renamed, or removed site fails until the matching list is
///     edited in the same change. Entries outside the boundary are migration debt unless
///     the file is an accepted provider adapter; the cutover shrinks the lists as persistence
///     moves behind application-owned ports, and SQLite removal empties the SQLite package entries.
/// </summary>
public sealed class PersistenceBoundaryArchitectureTests
{
    private const string EfCore = "Microsoft.EntityFrameworkCore";
    private const string Sqlite = "Microsoft.Data.Sqlite";
    private const string Npgsql = "Npgsql";
    private const string DbContext = "RomdDbContext";
    private const string CommandText = "CommandText";
    private const string RuntimeSourceRoot = "src";
    private const string Infrastructure = "src/Romd.Infrastructure/";
    private const string Persistence = "src/Romd.Persistence/";
    private const string HostingEndpoints = "src/Romd.Hosting/Endpoints/";

    /// <summary>
    ///     Repository-relative directory prefixes that own EF Core and provider types.
    /// </summary>
    private static readonly ImmutableArray<string> PersistenceBoundaryRoots = [Persistence];

    private static readonly ImmutableArray<string> ProviderNamespaceRoots = [EfCore, Sqlite, Npgsql];

    private static readonly ImmutableHashSet<string> RawSqlOperations = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "FromSql",
        "FromSqlRaw",
        "FromSqlInterpolated",
        "ExecuteSql",
        "ExecuteSqlAsync",
        "ExecuteSqlRaw",
        "ExecuteSqlRawAsync",
        "ExecuteSqlInterpolated",
        "ExecuteSqlInterpolatedAsync",
        "SqlQuery",
        "SqlQueryRaw");

    /// <summary>
    ///     Files outside the persistence boundary that legitimately own provider SQL. The
    ///     Hangfire schema provisioner and readiness probe speak to the hangfire schema
    ///     through Npgsql; nothing else outside the boundary may issue SQL.
    /// </summary>
    private static readonly ImmutableHashSet<string> AcceptedProviderAdapters = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "src/Romd.Infrastructure/Jobs/HangfireSchemaProvisioner.cs",
        "src/Romd.Infrastructure/Readiness/PostgreSqlHangfireSchemaProbe.cs");

    private static readonly ImmutableArray<ProviderReference> ProviderReferencesOutsideBoundary =
    [
        .. Site("src/Romd.Hosting/Hosting/RomdHostRegistrationExtensions.cs", DbContext),
        .. Site("src/Romd.Infrastructure/Catalog/RomCatalogOwnershipReader.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Catalog/TitleDerivationService.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Catalog/TitleSourceReferenceReader.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/DependencyInjection.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Diagnostics/OperationalDiagnosticsReader.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Enrichment/TitleEnrichmentEvidenceReader.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Identity/ConsumerAccountService.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Identity/RomdOpenIddictAccountService.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Identity/UserAdministration.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Jobs/HangfireJobStateSyncFilter.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Jobs/HangfireSchemaProvisioner.cs", Npgsql),
        .. Site("src/Romd.Infrastructure/Jobs/JobExecutionMutationGuard.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Readiness/AdminRealtimeOutboxReadinessCheck.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Readiness/DatabaseReadinessCheck.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Readiness/PostgreSqlHangfireSchemaProbe.cs", Npgsql),
        .. Site("src/Romd.Infrastructure/Realtime/AdminRealtimeOutbox.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Realtime/OutboxJobNotifier.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Realtime/OutboxStatsNotifier.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Resilience/ResilienceConfiguration.cs", EfCore),
        .. Site("src/Romd.Infrastructure/Source/CatalogPayloadAssertionReader.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Source/CatalogPayloadAssertionSynchronizer.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Source/CatalogProjectionService.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Source/DatCatalogPayloadAssertionProvider.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Source/DatCatalogSourceSnapshotProvider.cs", EfCore, DbContext),
        .. Site("src/Romd.Infrastructure/Source/TitlePayloadAvailabilityProjection.cs", EfCore, DbContext)
    ];

    private static readonly ImmutableArray<RawSqlSite> RawSqlSites =
    [
        // Parameterized atomic upsert prevents competing first settings saves from racing on UserId.
        Sql(Persistence + "Identity/ConsumerUserSettingsStore.cs", "ConsumerUserSettingsStore", "SaveSettingsAsync", "ExecuteSqlInterpolatedAsync"),
        // Reviewed run projection: parameterized continuation traversal and shared recorded predicates for pages,
        // retry membership and counts. Counts and the Review page compose EF-generated assessment SQL as one
        // command. Filters come from a closed vocabulary; no user text is composed.
        Sql(Persistence + "Artwork/EnrichmentTaskProjection.cs", "EnrichmentTaskProjection", "AttachRetryAsync", "ExecuteSqlRawAsync"),
        Sql(Persistence + "Artwork/EnrichmentTaskProjection.cs", "EnrichmentTaskProjection", "PageAsync", "SqlQueryRaw"),
        Sql(Persistence + "Artwork/EnrichmentTaskProjection.cs", "EnrichmentTaskProjection", "MatchingTitleIdsAsync", "SqlQueryRaw"),
        Sql(Persistence + "Artwork/EnrichmentTaskProjection.cs", "EnrichmentTaskProjection", "ReadAsync", CommandText),
        // Parameterized array insertion retains the acceptance lock and snapshots; timestamps
        // explicitly use the persistence model's unix-millisecond representation.
        Sql(Persistence + "Artwork/ArtworkCompletionStore.Acceptance.cs", "ArtworkCompletionStore", "InsertItemsStagedAsync", "ExecuteSqlInterpolatedAsync"),
        // Transaction-scoped identity invariant and per-system policy locks.
        Sql(Persistence + "Identity/AccountAccessGuard.cs", "AccountAccessGuard", "LockAsync", "ExecuteSqlRawAsync"),
        // Fixed IGDB singleton row lock serializes settings compare-and-update inside the command transaction;
        // reload and expected-revision recheck prevent stale credential/test writes.
        Sql(Persistence + "MetadataProviders/IgdbProviderSettingsStore.cs", "IgdbProviderSettingsStore", "LockAsync", "ExecuteSqlRawAsync"),
        // Serializes retryable session upserts for one user inside the application-owned transaction.
        Sql(Persistence + "PlayActivityUnitOfWork.cs", "PlayActivityUnitOfWork", "BeginAsync", "ExecuteSqlInterpolatedAsync"),
        // Parameterized policy job row lock serializes unclaimed-pending reuse with worker claim.
        Sql(Persistence + "Repositories/MetadataRematerializationJobRepository.cs", "MetadataRematerializationJobRepository", "GetReusablePendingAsync", "FromSqlInterpolated"),
        // Parameterized recovery lock revalidates the current replacement job before a stale-progress failure.
        Sql(Persistence + "Repositories/ReplaceDatJobRepository.cs", "ReplaceDatJobRepository", "TryFailStaleStartedStagedAsync", "FromSqlInterpolated"),
        Sql(Persistence + "Repositories/PlatformFieldDefaultRepository.cs", "PlatformFieldDefaultRepository", "LockAsync", "ExecuteSqlInterpolatedAsync"),
        // Reviewed atomic hash upsert preserves file identity and locks cleanup out
        // until artwork publication commits. EF converters are applied explicitly.
        // Provider-owned singleton row lock serializes atomic reference-data initialization.
        // Catalog enrollment serializes first acceptance and shares source locks with source lifecycle actions.
        Sql(Persistence + "Subscriptions/DatCatalogEnrollmentService.cs", "DatCatalogEnrollmentService", "LockCatalogAsync", "SqlQuery"),
        Sql(Persistence + "Subscriptions/DatCatalogEnrollmentService.cs", "DatCatalogEnrollmentService", "LockSourceAsync", "SqlQuery"),
        // Reviewed source removal shares the existing subscription source lock (key 145).
        Sql(Persistence + "Catalog/DatSourceManagement.Staging.cs", "DatSourceManagement", "TryLockCatalogAsync", "SqlQuery"),
        Sql(Persistence + "Catalog/DatSourceManagement.Staging.cs", "DatSourceManagement", "TryLockSourceAsync", "SqlQuery"),
        // Freeze reviewed personal state and FK additions until the source mutation commits.
        Sql(Persistence + "Catalog/DatSourceManagement.Staging.cs", "DatSourceManagement", "LockAffectedTitlesAsync", "ExecuteSqlInterpolatedAsync"),
        // Serializes reviewed reference updates with other checks and applies.
        Sql(Persistence + "ReferenceData/ReferenceCatalogService.cs", "ReferenceCatalogService", "LockAsync", "ExecuteSqlRawAsync"),
        Sql(
            Infrastructure + "Jobs/HangfireSchemaProvisioner.cs",
            "HangfireSchemaProvisioner",
            "StartAsync",
            CommandText),
        Sql(
            Infrastructure + "Readiness/PostgreSqlHangfireSchemaProbe.cs",
            "PostgreSqlHangfireSchemaProbe",
            "GetPublishedVersionAsync",
            CommandText),
        Sql(Persistence + "CatalogTopologyFence.cs", "CatalogTopologyFence", "AcquireAsync", "ExecuteSqlAsync"),
        // Parameterized collection row lock serializes item mutations before membership is read.
        Sql(Persistence + "Repositories/CollectionRepository.cs", "CollectionRepository", "LockForItemMutationStagedAsync", "SqlQuery"),
        // Parameterized visible-job root lock revalidates cancellation under the caller's transaction.
        Sql(Persistence + "Repositories/JobCancellationStore.cs", "JobCancellationStore", "LockVisibleForCancellationStagedAsync", "SqlQuery"),
        // Parameterized key-share lock retains selected cover media through the caller-owned collection transaction.
        Sql(Persistence + "Queries/CollectionCoverMediaReader.cs", "CollectionCoverMediaReader", "LockStagedAsync", "SqlQuery"),
        // Parameterized region and language row locks for merges and alias writes; each repository supplies its
        // literal statement per lock mode.
        Sql(Persistence + "Repositories/TaxonomyEntryProjection.cs", "TaxonomyEntryProjection", "LockRowsAsync", "SqlQuery"),
        // Parameterized transaction-local lock_timeout that bounds a taxonomy merge's lock waits.
        Sql(Persistence + "Repositories/TaxonomyEntryProjection.cs", "TaxonomyEntryProjection", "LimitLockWaitsAsync", "ExecuteSqlAsync"),
        // Parameterized alias lookup that locks the named region or language row FOR KEY SHARE inside a DAT ingest. It
        // is one statement, not a read followed by LockRowsAsync, so no merge can move the alias between the two.
        Sql(Persistence + "Repositories/RegionRepository.cs", "RegionRepository", "LockByAliasAsync", "SqlQuery"),
        Sql(Persistence + "Repositories/LanguageRepository.cs", "LanguageRepository", "LockByAliasAsync", "SqlQuery"),
        // Serializes representation retention and attachment for a logical ROM inside publication.
        Sql(Persistence + "Repositories/RomRepository.cs", "RomRepository", "LockRepresentationSetAsync", "ExecuteSqlInterpolatedAsync"),
        // Convergence acceptance locks the logical ROM before taking its source artifact lock;
        // publication/terminal work locks the durable source snapshot inside its fenced unit of work.
        // Exact durable CD intent lock within the worker's fenced publication/terminal transaction.
        // A fixed transaction-scoped advisory lock serializes bounded CHD campaign acceptance.
        // Immutable retained-body insertion uses the trusted importer's caller-owned transaction.
        Sql(Persistence + "Repositories/RomDiscMediaReferenceImportStore.cs", "RomDiscMediaReferenceImportStore", "StageAsync", "ExecuteSqlInterpolatedAsync"),
        // Fixed current_user query reports the actual runtime credential before HTTP/transaction admission.
        Sql(Persistence + "Repositories/RomDiscMediaReferenceImportStore.cs", "RomDiscMediaReferenceImportStore", "GetRuntimeRoleAsync", "SqlQueryRaw"),
        // Durable reevaluation uses parameterized caller-owned wakeups and a short SKIP LOCKED claim.
        Sql(Persistence + "Repositories/JobAcceptanceLock.cs", "JobAcceptanceLock", "AcquireAsync", "ExecuteSqlInterpolatedAsync"),
        // Fresh baseline runtime grants and readiness publication remain fixed provider-owned statements.
        // Unknown or unversioned application schemas are refused before any schema writes.
        Sql(
            Persistence + "PostgreSqlSchemaProvisioner.cs",
            "PostgreSqlSchemaProvisioner",
            "StartAsync",
            "ExecuteSqlRawAsync",
            4),
        // Parameterized tracking mutation + scoped invalidation + deduplicated job acceptance
        // are one PostgreSQL statement; changed IDs remain server-side even for platform batches.
        // Keep refresh timestamps strictly newer than publication at millisecond storage precision.
        Sql(Persistence + "Repositories/LibraryRepository.cs", "LibraryRepository", "FlagForRematerializationAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/LibraryRepository.cs", "LibraryRepository", "MarkPlatformChangedAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/TrackingLibraryInvalidation.cs", "TrackingLibraryInvalidation", "ApplyAsync", "SqlQueryRaw"),
        // Serializes per-account VFS credential changes and enforces the active-connection cap.
        Sql(Persistence + "Identity/VfsConnectionStore.cs", "VfsConnectionStore", "LockUserAsync", "ExecuteSqlInterpolatedAsync"),
        // Local operator admission checks the connected runtime role; no provisioning credential is used.
        Sql(Persistence + "Identity/LocalAdminRecovery.cs", "LocalAdminRecovery", "GetRuntimeRoleAsync", "SqlQueryRaw"),
        // Reviewed fresh media fact sites. IDs and values are bound parameters; container/layout names and query
        // shapes are closed provider-owned SQL. Snapshot readers are bounded and read-only; publication readers
        // hold the sorted ROM/representation/file lock set in the caller's ReadCommitted transaction.
        // Proof publication uses the guarded database entry point; raw container, manifest, inspection, boot and
        // historical source witnesses share that transaction. Cleanup rechecks every owner and job pin in its
        // guarded bounded collector. These entries do not permit independent application commits or SQL policy verdicts.
        // Reconciliation captures complete current scope before media admission; publication/terminal journals
        // use the same caller-owned fence and ordered lock set. All IDs, fingerprints and payload values are bound.
        Sql(Persistence + "MediaFacts/LogicalInspectionFactWriter.cs", "LogicalInspectionFactWriter", "StageAsync", "ExecuteSqlInterpolatedAsync", 3),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationAuthority.cs", "MediaReconciliationAuthority", "LockAsync", "ExecuteSqlInterpolatedAsync", 2),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationAuthority.cs", "MediaReconciliationAuthority", "LockAsync", "ExecuteSqlRawAsync", 2),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationAuthority.cs", "MediaReconciliationAuthority", "LockAsync", "SqlQueryRaw"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.Capture.cs", "MediaReconciliationExecution", "CaptureAsync", "ExecuteSqlInterpolatedAsync", 3),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.Capture.cs", "MediaReconciliationExecution", "CaptureAsync", "SqlQuery", 2),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.Capture.cs", "MediaReconciliationExecution", "InsertInputAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.Completion.cs", "MediaReconciliationExecution", "FailAttemptAsync", "ExecuteSqlInterpolatedAsync", 3),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.Completion.cs", "MediaReconciliationExecution", "FinishAsync", "ExecuteSqlInterpolatedAsync", 5),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.Completion.cs", "MediaReconciliationExecution", "FinishAsync", "SqlQuery"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.Completion.cs", "MediaReconciliationExecution", "SupersedeAttemptAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.Ownership.cs", "MediaReconciliationExecution", "AlignOwnershipAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.Publication.cs", "MediaReconciliationExecution", "PrepareAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.Publication.cs", "MediaReconciliationExecution", "PrepareAsync", "SqlQuery"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.Publication.cs", "MediaReconciliationExecution", "RecordPublicationAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.Sources.cs", "MediaReconciliationExecution", "RefreshSourcesAsync", "ExecuteSqlInterpolatedAsync", 4),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.Verification.cs", "MediaReconciliationExecution", "RecordVerificationAsync", "ExecuteSqlInterpolatedAsync", 2),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.Verification.cs", "MediaReconciliationExecution", "RecordVerificationAsync", "SqlQuery"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.cs", "MediaReconciliationExecution", "DemandAsync", "SqlQuery"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationExecution.cs", "MediaReconciliationExecution", "ReadAttemptOutcomeAsync", "SqlQuery"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationRequests.Control.cs", "MediaReconciliationRequests", "StageCancellationAsync", "SqlQuery"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationRequests.cs", "MediaReconciliationRequests", "LockRequestAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationRequests.cs", "MediaReconciliationRequests", "StageSubjectCoreAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationSelections.cs", "MediaReconciliationSelections", "AdvanceAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationSelections.cs", "MediaReconciliationSelections", "CountsAsync", "SqlQuery"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationSelections.cs", "MediaReconciliationSelections", "FinishAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationSelections.cs", "MediaReconciliationSelections", "LockAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationSelections.cs", "MediaReconciliationSelections", "ReadAsync", "ExecuteSqlRawAsync"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationSelections.cs", "MediaReconciliationSelections", "SaveProgressAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationSelections.cs", "MediaReconciliationSelections", "StopOwnedAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationSnapshotReader.cs", "MediaReconciliationSnapshotReader", "ReadAsync", "ExecuteSqlRawAsync"),
        // Derived descriptors and graph ownership issue SQL only inside the Persistence boundary.
        Sql(Persistence + "MediaFacts/DerivedDescriptorReader.cs", "DerivedDescriptorReader", "ReadForReleaseAsync", "SqlQuery"),
        Sql(Persistence + "Repositories/ConsumerDerivedDescriptorSource.cs", "ConsumerDerivedDescriptorSource", "IsRetainedAsync", "SqlQuery"),
        Sql(Persistence + "Repositories/ConsumerDerivedDescriptorSource.cs", "ConsumerDerivedDescriptorSource", "IsRetainedAsync", "ExecuteSqlRawAsync"),
        Sql(Persistence + "MediaFacts/CatalogCdLayoutRead.cs", "CatalogCdLayoutRead", "ReadAsync", "SqlQuery"),
        // Bounded canonical candidate lookup under the caller snapshot, with exact scoped binding facts.
        Sql(Persistence + "MediaFacts/CatalogCdLayoutRead.cs", "CatalogCdLayoutRead", "FindForReleaseAsync", "SqlQuery"),
        // Restores retained catalog-owned native facts even after their logical outputs were withdrawn.
        Sql(Persistence + "MediaFacts/CatalogCdLayoutRead.cs", "CatalogCdLayoutRead", "ReadRetainedAsync", "SqlQuery"),
        // Worker import authority creates identities under its caller transaction and acquires ordered logical/graph/file locks.
        Sql(Persistence + "MediaFacts/RomCdImportStore.cs", "RomCdImportStore", "LockAsync", "SqlQuery"),
        Sql(Persistence + "MediaFacts/RomCdImportStore.cs", "RomCdImportStore", "LockAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "MediaFacts/RomCdImportStore.cs", "RomCdImportStore", "LockAsync", "ExecuteSqlRawAsync", 2),
        Sql(Persistence + "MediaFacts/CatalogCdLayoutRead.cs", "CatalogCdLayoutRead", "LockScopeAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "MediaFacts/CatalogCdLayoutRead.cs", "CatalogCdLayoutRead", "RomIdsAsync", "SqlQuery"),
        Sql(Persistence + "MediaFacts/CatalogCdLayoutRead.cs", "CatalogCdLayoutRead", "RepresentationIdsAsync", "SqlQuery"),
        Sql(Persistence + "MediaFacts/CatalogCdLayoutRead.cs", "CatalogCdLayoutRead", "HasOwnerAsync", "SqlQuery"),
        // Exact-CUE admission rechecks the actual verified ordinary original, separately from retained graph ownership.
        Sql(Persistence + "MediaFacts/CatalogCdLayoutRead.cs", "CatalogCdLayoutRead", "HasVerifiedOriginalAsync", "SqlQuery"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationRequests.cs", "MediaReconciliationRequests", "StageSubjectCoreAsync", "SqlQuery"),
        Sql(Persistence + "Repositories/Reconciliation/MediaReconciliationSelections.cs", "MediaReconciliationSelections", "PageAsync", "SqlQuery"),
        Sql(Persistence + "MediaFacts/CatalogCdLayoutStore.cs", "CatalogCdLayoutStore", "LockScopeAsync", "ExecuteSqlInterpolatedAsync"),
        // Binding DML lives in the core invoked under PublishAsync's caller-owned savepoint.
        Sql(Persistence + "MediaFacts/CatalogCdLayoutStore.cs", "CatalogCdLayoutStore", "PublishCoreAsync", "SqlQuery", 2),
        Sql(Persistence + "MediaFacts/CatalogCdLayoutStore.cs", "CatalogCdLayoutStore", "WithdrawAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "MediaFacts/MediaRepresentationOwnership.cs", "MediaRepresentationOwnership", "Owned", "FromSqlRaw"),
        Sql(Persistence + "MediaFacts/DiscReferenceAssertionReader.cs", "DiscReferenceAssertionReader", "ReadCoreAsync", "SqlQueryRaw"),
        Sql(Persistence + "MediaFacts/LogicalInspectionReader.cs", "LogicalInspectionReader", "ReadCoreAsync", "SqlQueryRaw", 2),
        // Separate PS1 and Saturn inserts retain their own fact columns under the same caller-owned publication transaction.
        Sql(Persistence + "MediaFacts/MediaCreationFactWriter.cs", "MediaCreationFactWriter", "StageCdBootAsync", "ExecuteSqlInterpolatedAsync", 2),
        Sql(Persistence + "MediaFacts/MediaCreationFactWriter.cs", "MediaCreationFactWriter", "StageCreationAsync", "ExecuteSqlInterpolatedAsync", 3),
        Sql(Persistence + "MediaFacts/MediaFactLockSet.cs", "MediaFactLockSet", "ReadAsync", "ExecuteSqlRawAsync", 3),
        Sql(Persistence + "MediaFacts/MediaFactPublisher.cs", "MediaFactPublisher", "AppendVerificationAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "MediaFacts/MediaFactPublisher.cs", "MediaFactPublisher", "InsertContainerAsync", "ExecuteSqlInterpolatedAsync", 3),
        Sql(Persistence + "MediaFacts/MediaFactPublisher.cs", "MediaFactPublisher", "InsertIdAsync", "SqlQuery"),
        Sql(Persistence + "MediaFacts/MediaFactPublisher.cs", "MediaFactPublisher", "InsertManifestAsync", "ExecuteSqlInterpolatedAsync", 4),
        Sql(Persistence + "MediaFacts/MediaFactPublisher.cs", "MediaFactPublisher", "InsertVerificationAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "MediaFacts/MediaFactPublisher.cs", "MediaFactPublisher", "PublishCoreAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "MediaFacts/MediaFactPublisher.cs", "MediaFactPublisher", "PublishCoreAsync", "ExecuteSqlRawAsync"),
        Sql(Persistence + "MediaFacts/MediaFactReader.cs", "MediaFactReader", "ReadCoreAsync", "ExecuteSqlRawAsync"),
        Sql(Persistence + "MediaFacts/MediaFactReader.cs", "MediaFactReader", "Rows", "SqlQueryRaw"),
        Sql(Persistence + "MediaFacts/MediaRepresentationCleanup.cs", "MediaRepresentationCleanup", "CollectAsync", "SqlQuery"),
        Sql(Persistence + "MediaFacts/MediaRepresentationCleanup.cs", "MediaRepresentationCleanup", "CollectAsync", "SqlQueryRaw"),
        Sql(Persistence + "MediaFacts/RomMediaCreationStore.cs", "RomMediaCreationStore", "StageAsync", "ExecuteSqlInterpolatedAsync", 2),
        Sql(Persistence + "MediaFacts/RomMediaCreationStore.cs", "RomMediaCreationStore", "StageAsync", "ExecuteSqlRawAsync"),
        Sql(Persistence + "PostgreSqlSchemaProvisioner.cs", "PostgreSqlSchemaProvisioner", "RefuseUnknownMigrationsAsync", "SqlQuery"),
        Sql(Persistence + "Repositories/RomCdArchiveDownloadCatalog.cs", "RomCdArchiveDownloadCatalog", "OwnedSnapshotAsync", "ExecuteSqlRawAsync"),
        Sql(Persistence + "Repositories/RomContentCatalog.cs", "RomContentCatalog", "OwnedSnapshotAsync", "ExecuteSqlRawAsync"),
        Sql(Persistence + "Repositories/RomContentCatalog.cs", "RomContentCatalog", "ReadLockedPublicationAsync", "ExecuteSqlRawAsync"),
        Sql(Persistence + "Repositories/RomRepository.cs", "RomRepository", "ListRepresentationsAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/RomRepository.cs", "RomRepository", "ListRepresentationsAsync", "ExecuteSqlRawAsync"),
        Sql(Persistence + "Repositories/RomRepository.cs", "RomRepository", "RecordWholeMeasurementStagedAsync", "ExecuteSqlInterpolatedAsync"),
        Sql(Persistence + "Repositories/RomRepository.cs", "RomRepository", "RecordWholeMeasurementStagedAsync", "SqlQuery"),
        Sql(Persistence + "Repositories/RomRetainedLogicalInspectionReader.cs", "RomRetainedLogicalInspectionReader", "ReadSnapshotAsync", "ExecuteSqlRawAsync"),
        Sql(Persistence + "Repositories/RomRetainedLogicalInspectionReader.cs", "RomRetainedLogicalInspectionReader", "ReadSnapshotAsync", "SqlQuery"),
        Sql(Persistence + "RomdSchemaProbe.cs", "RomdSchemaProbe", "GetPublishedVersionAsync", "SqlQueryRaw", 2)
    ];

    private static readonly ImmutableArray<PackageReference> PersistencePackageReferences =
    [
        Package(Infrastructure + "Romd.Infrastructure.csproj", Npgsql),
        Package(Persistence + "Romd.Persistence.csproj", "Microsoft.AspNetCore.Identity.EntityFrameworkCore"),
        Package(Persistence + "Romd.Persistence.csproj", EfCore),
        Package(Persistence + "Romd.Persistence.csproj", "Npgsql.EntityFrameworkCore.PostgreSQL"),
        Package(Persistence + "Romd.Persistence.csproj", "OpenIddict.EntityFrameworkCore"),
        Package("src/Romd.Worker.Host/Romd.Worker.Host.csproj", "Microsoft.EntityFrameworkCore.Design")
    ];

    [Fact]
    public void ProviderReferences_OutsidePersistenceBoundary_MatchAllowList()
    {
        var observed = GetRuntimeSourceFiles()
            .SelectMany(path => ObserveProviderReferences(File.ReadAllText(path), ToRepositoryRelativePath(path)));

        Format(observed).ShouldBe(
            Format(ProviderReferencesOutsideBoundary),
            "EF Core, provider, and RomdDbContext references outside the persistence boundary are " +
            "PostgreSQL cutover migration debt. New, moved, renamed, or removed sites must be resolved explicitly.");
    }

    [Fact]
    public void HostingEndpoints_ReferenceNoEfCoreProviderOrDbContext()
    {
        var observed = GetRuntimeSourceFiles()
            .Select(path => (Path: path, RelativePath: ToRepositoryRelativePath(path)))
            .Where(file => file.RelativePath.StartsWith(HostingEndpoints, StringComparison.Ordinal))
            .SelectMany(file => ObserveProviderReferences(File.ReadAllText(file.Path), file.RelativePath));

        Format(observed).ShouldBeEmpty(
            "Minimal API endpoints are HTTP adapters: they reach data through application ports, never " +
            "through EF Core, a database provider, or RomdDbContext.");
        Format(ProviderReferencesOutsideBoundary.Where(reference =>
                reference.Path.StartsWith(HostingEndpoints, StringComparison.Ordinal)))
            .ShouldBeEmpty("Endpoint persistence access cannot be allow-listed as migration debt.");
    }

    [Fact]
    public void RawSqlSites_InRuntimeSource_MatchAllowList()
    {
        var observed = GetRuntimeSourceFiles()
            .SelectMany(path => ObserveRawSqlSites(File.ReadAllText(path), ToRepositoryRelativePath(path)));

        Format(GroupOccurrences(observed)).ShouldBe(
            Format(RawSqlSites),
            "Raw SQL is provider-owned. New, moved, renamed, duplicated, stale, or removed sites " +
            "must be resolved explicitly.");
    }

    [Fact]
    public void RawSqlAndProviderSites_OutsidePersistenceBoundary_AreAcceptedProviderAdapters()
    {
        var rawSqlOutsideBoundary = RawSqlSites
            .Select(site => site.Path)
            .Where(path => !IsInsidePersistenceBoundary(path))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var npgsqlOutsideBoundary = ProviderReferencesOutsideBoundary
            .Where(reference => reference.Reference == Npgsql)
            .Select(reference => reference.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();

        rawSqlOutsideBoundary.ShouldAllBe(
            path => AcceptedProviderAdapters.Contains(path),
            "Raw SQL outside the persistence boundary is allowed only in accepted provider adapters.");
        npgsqlOutsideBoundary.ShouldAllBe(
            path => AcceptedProviderAdapters.Contains(path),
            "Npgsql outside the persistence boundary is allowed only in accepted provider adapters.");
        AcceptedProviderAdapters.ShouldAllBe(
            path => !IsInsidePersistenceBoundary(path),
            "Accepted provider adapters describe files outside the persistence boundary.");
    }

    [Fact]
    public void PersistencePackageReferences_InRuntimeProjects_MatchAllowList()
    {
        var observed = GetRuntimeProjectFiles().SelectMany(ObservePackageReferences);

        Format(observed).ShouldBe(
            Format(PersistencePackageReferences),
            "EF Core and database-provider packages belong to the persistence boundary. " +
            "The PostgreSQL cutover asserts that no SQLite package remains under src/.");
    }

    [Fact]
    public void PersistenceAssembly_DoesNotDependOnAspNetCoreHangfireOrOuterLayers()
    {
        string[] references = typeof(RomdDbContext).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var projectFile = XDocument.Load(Path.Combine(FindRepositoryRoot(), Persistence, "Romd.Persistence.csproj"));

        references.ShouldNotContain(reference => reference.StartsWith("Hangfire", StringComparison.Ordinal));
        references.ShouldNotContain(reference =>
            reference.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)
            && reference != "Microsoft.AspNetCore.Identity.EntityFrameworkCore");
        references.ShouldNotContain(reference => reference == "Romd.Infrastructure" || reference == "Romd.Hosting");
        projectFile.Descendants().Where(element => element.Name.LocalName == "FrameworkReference").ShouldBeEmpty(
            "Romd.Persistence must not reference the ASP.NET Core shared framework.");
    }

    [Fact]
    public void ObserveProviderReferences_UsingDirectiveQualifiedNameAndDbContextOutsideBoundary_RecordsEachOnce()
    {
        const string path = "src/Romd.Infrastructure/Experimental/FeatureReader.cs";
        const string source = """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Diagnostics;

            public sealed class FeatureReader(RomdDbContext context)
            {
                // Microsoft.Data.Sqlite in a comment is not a reference.
                public void Detach(object entity) =>
                    context.Entry(entity).State = Microsoft.EntityFrameworkCore.EntityState.Detached;

                public Type ContextType => typeof(RomdDbContext);
            }
            """;

        Format(ObserveProviderReferences(source, path)).ShouldBe(
        [
            $"{path} | {EfCore}",
            $"{path} | {DbContext}"
        ]);
    }

    [Fact]
    public void ObserveProviderReferences_FullyQualifiedProviderWithoutUsing_RecordsProviderRoot()
    {
        const string path = "src/Romd.Hosting/Experimental/FeatureProbe.cs";
        const string source = """
            public sealed class FeatureProbe
            {
                public async Task ProbeAsync(string connectionString)
                {
                    await using var connection = new Npgsql.NpgsqlConnection(connectionString);
                    await connection.OpenAsync();
                }
            }
            """;

        Format(ObserveProviderReferences(source, path)).ShouldBe([$"{path} | {Npgsql}"]);
    }

    [Fact]
    public void ObserveProviderReferences_InsidePersistenceBoundary_RecordsNothing()
    {
        const string source = """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.Data.Sqlite;

            public sealed class FeatureRepository(RomdDbContext context);
            """;

        ObserveProviderReferences(source, Persistence + "Repositories/FeatureRepository.cs").ShouldBeEmpty();
    }

    [Fact]
    public void ObserveRawSqlSites_EfRawSqlAndCommandTextAssignments_RecordsExactOccurrences()
    {
        const string path = "src/Romd.Infrastructure/Experimental/FeatureQuery.cs";
        const string source = """
            public sealed class FeatureQuery
            {
                public IQueryable<Feature> Search(RomdDbContext context, string term)
                {
                    var first = context.Features.FromSqlRaw("SELECT 1");
                    return first.Concat(context.Features.FromSqlRaw("SELECT 2"));
                }

                public async Task<int> ProbeAsync(DbConnection connection)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT 3";
                    await using var initialized = new NpgsqlCommand { CommandText = "SELECT 4" };
                    return await context.Database.ExecuteSqlRawAsync("SELECT 5");
                }
            }
            """;

        Format(GroupOccurrences(ObserveRawSqlSites(source, path))).ShouldBe(
        [
            $"{path} | FeatureQuery.ProbeAsync | CommandText | count=2",
            $"{path} | FeatureQuery.ProbeAsync | ExecuteSqlRawAsync | count=1",
            $"{path} | FeatureQuery.Search | FromSqlRaw | count=2"
        ]);
    }

    [Fact]
    public void ObserveRawSqlSites_MigrationsFolder_RecordsNothing()
    {
        const string source = """
            public partial class AddFeature : Migration
            {
                protected override void Up(MigrationBuilder migrationBuilder) =>
                    migrationBuilder.Sql("CREATE VIRTUAL TABLE Features_fts USING fts5(Name)");
            }
            """;

        ObserveRawSqlSites(source, Persistence + "Migrations/20260101000000_AddFeature.cs").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore", true)]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", true)]
    [InlineData("Microsoft.EntityFrameworkCore.Design", true)]
    [InlineData("Microsoft.AspNetCore.Identity.EntityFrameworkCore", true)]
    [InlineData("OpenIddict.EntityFrameworkCore", true)]
    [InlineData("Microsoft.Data.Sqlite", true)]
    [InlineData("Npgsql", true)]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", true)]
    [InlineData("Hangfire.PostgreSql", false)]
    [InlineData("Microsoft.AspNetCore.Identity", false)]
    [InlineData("Testcontainers.PostgreSql", false)]
    public void IsPersistencePackage_ClassifiesEfCoreAndProviderPackages(string package, bool expected) =>
        IsPersistencePackage(package).ShouldBe(expected);

    private static IEnumerable<ProviderReference> ObserveProviderReferences(string source, string relativePath)
    {
        if (IsInsidePersistenceBoundary(relativePath))
        {
            return [];
        }

        var root = CSharpSyntaxTree.ParseText(source, path: relativePath).GetRoot();
        var nodes = root.DescendantNodes().ToArray();

        var namespaceRoots = nodes
            .Select(GetDottedName)
            .Where(name => name is not null)
            .SelectMany(name => ProviderNamespaceRoots.Where(prefix => IsWithinNamespace(name!, prefix)));
        var dbContextReferences = nodes
            .OfType<IdentifierNameSyntax>()
            .Where(identifier => identifier.Identifier.ValueText == DbContext)
            .Select(_ => DbContext);

        return namespaceRoots
            .Concat(dbContextReferences)
            .Distinct(StringComparer.Ordinal)
            .Select(reference => new ProviderReference(relativePath, reference))
            .ToArray();
    }

    private static IEnumerable<RawSqlSiteKey> ObserveRawSqlSites(string source, string relativePath)
    {
        if (HasPathSegment(relativePath, "Migrations"))
        {
            yield break;
        }

        var root = CSharpSyntaxTree.ParseText(source, path: relativePath).GetRoot();

        foreach (var node in root.DescendantNodes())
        {
            string? operation = GetRawSqlOperation(node);
            if (operation is null)
            {
                continue;
            }

            var containingType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();

            yield return new RawSqlSiteKey(
                relativePath,
                containingType?.Identifier.ValueText ?? "<global>",
                GetContainingMethodName(node),
                operation);
        }
    }

    private static IEnumerable<PackageReference> ObservePackageReferences(string path)
    {
        string relativePath = ToRepositoryRelativePath(path);

        return XDocument.Load(path)
            .Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value)
            .Where(package => package is not null && IsPersistencePackage(package))
            .Select(package => new PackageReference(relativePath, package!))
            .ToArray();
    }

    private static bool IsPersistencePackage(string package) =>
        package.StartsWith(EfCore, StringComparison.Ordinal)
        || package.EndsWith(".EntityFrameworkCore", StringComparison.Ordinal)
        || package == Sqlite
        || IsWithinNamespace(package, Npgsql);

    private static string? GetRawSqlOperation(SyntaxNode node) =>
        node switch
        {
            InvocationExpressionSyntax invocation
                when GetInvokedMethodName(invocation) is { } name && RawSqlOperations.Contains(name) => name,
            AssignmentExpressionSyntax
            {
                Left: MemberAccessExpressionSyntax { Name.Identifier.ValueText: CommandText }
            } => CommandText,
            AssignmentExpressionSyntax
            {
                Left: IdentifierNameSyntax { Identifier.ValueText: CommandText },
                Parent: InitializerExpressionSyntax
            } => CommandText,
            _ => null
        };

    private static string? GetDottedName(SyntaxNode node) =>
        node switch
        {
            UsingDirectiveSyntax directive => directive.Name?.ToString(),
            QualifiedNameSyntax qualified => qualified.ToString(),
            MemberAccessExpressionSyntax memberAccess => memberAccess.ToString(),
            _ => null
        } is { } text
            ? string.Concat(text.Where(character => !char.IsWhiteSpace(character)))
            : null;

    private static bool IsWithinNamespace(string name, string root) =>
        name == root || name.StartsWith(root + ".", StringComparison.Ordinal);

    private static bool IsInsidePersistenceBoundary(string relativePath) =>
        PersistenceBoundaryRoots.Any(root => relativePath.StartsWith(root, StringComparison.Ordinal));

    private static string? GetInvokedMethodName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => null
        };

    private static string GetContainingMethodName(SyntaxNode node)
    {
        SyntaxNode? callable = node.Ancestors().FirstOrDefault(ancestor => ancestor is
            MethodDeclarationSyntax
            or ConstructorDeclarationSyntax
            or DestructorDeclarationSyntax
            or OperatorDeclarationSyntax
            or ConversionOperatorDeclarationSyntax
            or AccessorDeclarationSyntax
            or LocalFunctionStatementSyntax);

        return callable switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax => ".ctor",
            DestructorDeclarationSyntax => ".dtor",
            OperatorDeclarationSyntax operation => $"operator {operation.OperatorToken.ValueText}",
            ConversionOperatorDeclarationSyntax conversion =>
                $"operator {conversion.Type.WithoutTrivia()}",
            AccessorDeclarationSyntax accessor => accessor.Keyword.ValueText,
            LocalFunctionStatementSyntax localFunction => localFunction.Identifier.ValueText,
            _ => "<unknown>"
        };
    }

    private static IReadOnlyDictionary<RawSqlSiteKey, RawSqlSite> GroupOccurrences(
        IEnumerable<RawSqlSiteKey> occurrences) =>
        occurrences
            .GroupBy(key => key)
            .ToDictionary(group => group.Key, group => new RawSqlSite(group.Key, group.Count()));

    private static IReadOnlyList<string> GetRuntimeSourceFiles() => GetRuntimeFiles("*.cs");

    private static IReadOnlyList<string> GetRuntimeProjectFiles() => GetRuntimeFiles("*.csproj");

    private static IReadOnlyList<string> GetRuntimeFiles(string searchPattern)
    {
        string sourceRoot = Path.Combine(FindRepositoryRoot(), RuntimeSourceRoot);

        return Directory.GetFiles(sourceRoot, searchPattern, SearchOption.AllDirectories)
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static bool HasPathSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(segment, StringComparer.Ordinal);

    private static string ToRepositoryRelativePath(string path) =>
        Path.GetRelativePath(FindRepositoryRoot(), path).Replace('\\', '/');

    private static IEnumerable<ProviderReference> Site(string path, params string[] references) =>
        references.Select(reference => new ProviderReference(path, reference));

    private static RawSqlSite Sql(string path, string type, string method, string operation, int count = 1) =>
        new(new RawSqlSiteKey(path, type, method, operation), count);

    private static PackageReference Package(string path, string package) => new(path, package);

    private static string[] Format(IEnumerable<ProviderReference> references) =>
        references
            .Select(reference => $"{reference.Path} | {reference.Reference}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] Format(IReadOnlyDictionary<RawSqlSiteKey, RawSqlSite> sites) => Format(sites.Values);

    private static string[] Format(IEnumerable<RawSqlSite> sites) =>
        sites
            .Select(site => $"{site.Path} | {site.Type}.{site.Method} | {site.Operation} | count={site.Count}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] Format(IEnumerable<PackageReference> packages) =>
        packages
            .Select(package => $"{package.Path} | {package.Package}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Romd.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private sealed record ProviderReference(string Path, string Reference);

    private sealed record RawSqlSiteKey(string Path, string Type, string Method, string Operation);

    private sealed record RawSqlSite(RawSqlSiteKey Key, int Count)
    {
        public string Path => Key.Path;
        public string Type => Key.Type;
        public string Method => Key.Method;
        public string Operation => Key.Operation;
    }

    private sealed record PackageReference(string Path, string Package);
}
