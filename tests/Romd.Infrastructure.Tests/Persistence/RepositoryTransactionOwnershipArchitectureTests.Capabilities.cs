using System.Collections.Immutable;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class RepositoryTransactionOwnershipArchitectureTests
{
    // These exact sites were previously invisible because their declaring type did not end in
    // Repository. Classification explains current ownership; MigrationDebt is not an exception
    // to the application's transaction rule. No path, class name or suffix bypasses observation.
    private static readonly ImmutableArray<ReviewedTransactionBoundary> AdditionalTransactionBoundaries =
    [
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "Artwork acceptance still orchestrates its application transaction in Infrastructure; migrate without moving image processing under the lock.",
            Site("src/Romd.Infrastructure/Artwork/LocalArtworkService.cs", "LocalArtworkService", "PinMediaAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.BatchFlushProtocol,
            "Neutral-source-identity flush protocol persists entries and links before yielding each bounded batch and clearing the shared tracker; writes enlist in a caller transaction when present, otherwise each flush is durable.",
            Site("src/Romd.Infrastructure/Catalog/TitleDerivationService.cs", "TitleDerivationService", "ProcessBatchAsync", "SaveChangesAsync", 2)),
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "Password/security-stamp mutation still owns its transaction in the identity adapter; preserve UserManager and account-session atomicity during migration.",
            Site("src/Romd.Infrastructure/Identity/ConsumerAccountService.cs", "ConsumerAccountService", "ChangePasswordAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "Recurring recovery performs short idempotent pending-version cleanup and orphan backstops, independently of a user command.",
            Site("src/Romd.Infrastructure/Jobs/DatReplacementConvergenceSweepJob.cs", "DatReplacementConvergenceSweepJob", "AgeOutAbandonedPendingVersionsAsync", "BeginTransactionAsync"),
            Site("src/Romd.Infrastructure/Jobs/DatReplacementConvergenceSweepJob.cs", "DatReplacementConvergenceSweepJob", "ApplyOrphanedTitleBackstopAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "Enrichment acceptance still opens its application transaction in the scheduler; durable job deduplication and dispatch must survive staged migration.",
            Site("src/Romd.Infrastructure/Jobs/EnrichmentScheduler.cs", "EnrichmentScheduler", "EnqueueAsync", "BeginTransactionAsync"),
            Site("src/Romd.Infrastructure/Jobs/EnrichmentScheduler.cs", "EnrichmentScheduler", "EnqueueBulkAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "Worker rematerialization commits one bounded metadata/title mutation per iteration.",
            Site("src/Romd.Infrastructure/Jobs/Executors/MetadataRematerializationJobExecutor.cs", "MetadataRematerializationJobExecutor", "ExecuteAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "Export acceptance still owns its application transaction in the scheduler; migrate pending-job checks and durable dispatch together.",
            Site("src/Romd.Infrastructure/Jobs/ExportScheduler.cs", "ExportScheduler", "EnqueueLibraryExportAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "Hangfire state bridge conditionally commits terminal job state and its outbox intents under the execution fence.",
            Site("src/Romd.Infrastructure/Jobs/HangfireJobStateSyncFilter.cs", "HangfireJobStateSyncFilter", "OnStateApplied", "BeginTransaction"),
            Site("src/Romd.Infrastructure/Jobs/HangfireJobStateSyncFilter.cs", "HangfireJobStateSyncFilter", "OnStateApplied", "SaveChanges", 2)),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "Execution-fenced worker mutations coordinate short unit-of-work transactions; never commits the caller's HTTP mutation.",
            Site("src/Romd.Infrastructure/Jobs/JobExecutionMutationGuard.cs", "JobExecutionMutationGuard", "ExecuteAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "Recovery sweep commits bounded title-payload availability batches.",
            Site("src/Romd.Infrastructure/Jobs/TitlePayloadAvailabilitySweepJob.cs", "TitlePayloadAvailabilitySweepJob", "ExecuteAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "Materialization claims and publishes through distinct short transactions around effectful computation; the published generation remains atomic.",
            Site("src/Romd.Infrastructure/Libraries/LibraryMaterializationService.cs", "LibraryMaterializationService", "MaterializeAsync", "BeginTransactionAsync", 2)),
        Boundary(TransactionBoundaryKind.StandalonePublication,
            "Standalone best-effort notifications commit only their own outbox intents through the notifier commit gate; command paths enlist the outbox in their own transaction.",
            Site("src/Romd.Infrastructure/Realtime/OutboxJobNotifier.cs", "OutboxJobNotifier", "NotifyJobUpdatedAsync", "SaveChangesAsync"),
            Site("src/Romd.Infrastructure/Realtime/OutboxJobNotifier.cs", "OutboxJobNotifier", "NotifyTitleEnrichedAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.StandalonePublication,
            "Standalone best-effort stats publication commits its own outbox intent through the notifier commit gate.",
            Site("src/Romd.Infrastructure/Realtime/OutboxStatsNotifier.cs", "OutboxStatsNotifier", "EnqueueAndCommitAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "Platform projection rebuild owns its platform lock/savepoint transaction; child flushes obtain generated release identities before dependent rows and atomic publication.",
            Site("src/Romd.Infrastructure/Source/CatalogProjectionService.cs", "CatalogProjectionService", "InsertChildRowsAsync", "SaveChangesAsync", 2),
            Site("src/Romd.Infrastructure/Source/CatalogProjectionService.cs", "CatalogProjectionService", "RebuildPlatformAsync", "BeginTransactionAsync"),
            Site("src/Romd.Infrastructure/Source/CatalogProjectionService.cs", "CatalogProjectionService", "UpsertReleasesAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "Post-activation retention and its convergence backstop independently commit short source-retention/projection invalidation work.",
            Site("src/Romd.Infrastructure/Source/DatVersionRetentionCoordinator.cs", "DatVersionRetentionCoordinator", "EnforceAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "Descriptor intake and reconciliation persist bounded upload-journal attachments and durable reconciliation demand after external reads.",
            Site("src/Romd.Infrastructure/Source/OriginalDiscDescriptorIntake.cs", "OriginalDiscDescriptorIntake", "IntakeAsync", "BeginTransactionAsync"),
            Site("src/Romd.Infrastructure/Source/OriginalDiscDescriptorIntake.cs", "OriginalDiscDescriptorIntake", "ReconcileAsync", "BeginTransactionAsync", 2)),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "File ledger cleanup owns short metadata-deletion transactions under file locks; CAS reclamation follows the committed ledger decision.",
            Site("src/Romd.Infrastructure/Storage/FileStorageService.cs", "FileStorageService", "DeleteOrphanAsync", "BeginTransactionAsync"),
            Site("src/Romd.Infrastructure/Storage/FileStorageService.cs", "FileStorageService", "DeleteRowIfUnreferencedAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.BootstrapProtocol,
            "Worker-only startup seeds identity/reference data atomically; API hosts never compose the seeder.",
            Site("src/Romd.Persistence/AdminSeeder.cs", "AdminSeeder", "StartAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "Artwork acceptance still owns a persistence transaction and generated job flush; migrate scope snapshot, ownership locks and durable dispatch as one use case.",
            Site("src/Romd.Persistence/Artwork/ArtworkCompletionStore.cs", "ArtworkCompletionStore", "QueueScopeAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/Artwork/ArtworkCompletionStore.cs", "ArtworkCompletionStore", "QueueScopeAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "Source lifecycle acceptance still owns its use-case transaction in Persistence; move orchestration without weakening reviewed impact and source locks.",
            Site("src/Romd.Persistence/Catalog/DatSourceManagement.cs", "DatSourceManagement", "ApplyAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.ReadSnapshot,
            "Reviewed source-removal preview holds its mutation lock while reading impact, then disposes without committing writes.",
            Site("src/Romd.Persistence/Catalog/DatSourceManagement.cs", "DatSourceManagement", "PreviewAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "Proposal refresh flushes inside its caller-owned catalog rebuild transaction; moving that flush needs explicit dependent-read/identity characterization.",
            Site("src/Romd.Persistence/Catalog/DiscSetService.cs", "DiscSetService", "RefreshAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "Descriptor persistence flushes within caller-owned intake/reconciliation transactions; stage generated identities before moving flush ownership.",
            Site("src/Romd.Persistence/Catalog/OriginalDiscLayoutService.cs", "OriginalDiscLayoutService", "ReconcileAsync", "SaveChangesAsync"),
            Site("src/Romd.Persistence/Catalog/OriginalDiscLayoutService.cs", "OriginalDiscLayoutService", "RecordAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "Configuration saves still live in a persistence helper; staged migration must preserve narrow concurrency-conflict translation and interceptor audit evidence.",
            Site("src/Romd.Persistence/ConfigurationSave.cs", "ConfigurationSave", "TrySaveAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.UnitOfWorkAdapter,
            "Application-owned read-snapshot factory implements the requested isolation boundary.",
            Site("src/Romd.Persistence/EfReadSnapshotTransactionFactory.cs", "EfReadSnapshotTransactionFactory", "BeginAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.UnitOfWorkAdapter,
            "Application-owned ITransaction implementation flushes pending changes before its single commit.",
            Site("src/Romd.Persistence/EfUnitOfWork.cs", "EfTransaction", "CommitAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.UnitOfWorkAdapter,
            "Application-owned IUnitOfWork implementation begins execution-fenced transactions and explicitly flushes staged changes.",
            Site("src/Romd.Persistence/EfUnitOfWork.cs", "EfUnitOfWork", "BeginTransactionAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/EfUnitOfWork.cs", "EfUnitOfWork", "FlushAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "Omission persistence requires a caller transaction but still flushes internally; keep revision rotation and omission facts atomic while moving flush ownership.",
            Site("src/Romd.Persistence/Enrichment/EnrichmentWorkbench.cs", "EnrichmentWorkbench", "SetOmissionAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "Authentication session creation still saves in its persistence adapter; preserve security-stamp/session issuance consistency during staging migration.",
            Site("src/Romd.Persistence/Identity/AccountSessionStore.cs", "AccountSessionStore", "StartAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "Consumer connection create/rotate/revoke still own their persistence transactions; staged migration must preserve per-user limits, credential rotation and revocation locks.",
            Site("src/Romd.Persistence/Identity/VfsConnectionStore.cs", "VfsConnectionStore", "CreateAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/Identity/VfsConnectionStore.cs", "VfsConnectionStore", "CreateAsync", "SaveChangesAsync"),
            Site("src/Romd.Persistence/Identity/VfsConnectionStore.cs", "VfsConnectionStore", "RevokeAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/Identity/VfsConnectionStore.cs", "VfsConnectionStore", "RotateAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/Identity/VfsConnectionStore.cs", "VfsConnectionStore", "RotateAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.ReadSnapshot,
            "Media facts are read from one repeatable-read snapshot so returned evidence and file identity describe the same state.",
            Site("src/Romd.Persistence/MediaFacts/MediaFactReader.cs", "MediaFactReader", "ReadCoreAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "Representation garbage collection owns bounded deletion transactions and storage locks independently of user mutations.",
            Site("src/Romd.Persistence/MediaFacts/MediaRepresentationCleanup.cs", "MediaRepresentationCleanup", "CollectAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.ReadSnapshot,
            "Provider discovery reads facts, trusted associations and their fingerprint from one repeatable-read snapshot.",
            Site("src/Romd.Persistence/MetadataProviders/ProviderIdentityStore.cs", "ProviderIdentityStore", "ReadAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "Identity acceptance conditionally stages into a job owner's transaction or opens its own; migrate that ownership split without weakening fingerprint/revision checks.",
            Site("src/Romd.Persistence/MetadataProviders/ProviderIdentityStore.cs", "ProviderIdentityStore", "SaveAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/MetadataProviders/ProviderIdentityStore.cs", "ProviderIdentityStore", "SaveAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.UnitOfWorkAdapter,
            "Application-owned play-activity unit of work supplies serializable session writes and one commit.",
            Site("src/Romd.Persistence/PlayActivityUnitOfWork.cs", "PlayActivityUnitOfWork", "BeginAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.UnitOfWorkAdapter,
            "Play-activity transaction implementation flushes the caller's staged session changes before commit.",
            Site("src/Romd.Persistence/PlayActivityUnitOfWork.cs", "Transaction", "CommitAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.ReadSnapshot,
            "Consumer multi-query projections bind live account library scope and publication facts in one repeatable-read snapshot.",
            Site("src/Romd.Persistence/Queries/LiveConsumerLibraryQuery.cs", "LiveConsumerLibraryQuery", "ReadAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.BootstrapProtocol,
            "Typed catalog import flushes dependency removals and minted aliases inside the importer's catalog-lock transaction; generated keys and unique constraints require the ordering.",
            Site("src/Romd.Persistence/ReferenceData/CatalogAliasReconciliation.cs", "CatalogAliasReconciliation", "LanguageAsync", "SaveChangesAsync", 2),
            Site("src/Romd.Persistence/ReferenceData/CatalogAliasReconciliation.cs", "CatalogAliasReconciliation", "PruneAsync", "SaveChangesAsync"),
            Site("src/Romd.Persistence/ReferenceData/CatalogAliasReconciliation.cs", "CatalogAliasReconciliation", "RegionAsync", "SaveChangesAsync", 2),
            Site("src/Romd.Persistence/ReferenceData/CatalogAliasReconciliation.cs", "CatalogAliasReconciliation", "SystemAsync", "SaveChangesAsync", 2)),
        Boundary(TransactionBoundaryKind.BootstrapProtocol,
            "Built-in catalog artwork records published asset references inside the importer's transaction before effective-catalog publication.",
            Site("src/Romd.Persistence/ReferenceData/CatalogArtworkImport.cs", "CatalogArtworkImport", "ImportAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.BootstrapProtocol,
            "Typed built-in company import flushes dependency facts inside the importer's catalog-lock transaction.",
            Site("src/Romd.Persistence/ReferenceData/CompanyCatalogImport.cs", "CompanyCatalogImport", "ImportAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.BootstrapProtocol,
            "Typed built-in language import flushes generated identities and effective facts inside the importer's catalog-lock transaction.",
            Site("src/Romd.Persistence/ReferenceData/LanguageCatalogImport.cs", "LanguageCatalogImport", "ImportAsync", "SaveChangesAsync", 2)),
        Boundary(TransactionBoundaryKind.BootstrapProtocol,
            "Typed built-in rating-board import flushes dependencies inside the importer's catalog-lock transaction.",
            Site("src/Romd.Persistence/ReferenceData/RatingBoardCatalogImport.cs", "RatingBoardCatalogImport", "ImportAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.BootstrapProtocol,
            "Typed built-in rating import flushes dependencies inside the importer's catalog-lock transaction.",
            Site("src/Romd.Persistence/ReferenceData/RatingCatalogImport.cs", "RatingCatalogImport", "ImportAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "Expired reference reservations are released in a catalog-locked cleanup transaction; CAS collection runs separately.",
            Site("src/Romd.Persistence/ReferenceData/ReferenceAssetLifecycle.cs", "ReferenceCatalogService", "ReleaseExpiredAssetsAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.PublicationFlushProtocol,
            "Reference mutation/import callers own the catalog lock and transaction; publication flushes the rebuilt effective snapshot and compatibility columns before their commit.",
            Site("src/Romd.Persistence/ReferenceData/ReferenceCatalogPublisher.cs", "ReferenceCatalogPublisher", "PublishAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "Reference asset upload still owns a persistence transaction and save; migrate reservation and blob ownership atomically.",
            Site("src/Romd.Persistence/ReferenceData/ReferenceCatalogService.cs", "ReferenceCatalogService", "AddAssetAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/ReferenceData/ReferenceCatalogService.cs", "ReferenceCatalogService", "AddAssetAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.UnitOfWorkAdapter,
            "Application-owned reference mutation session coordinates catalog locking, staged mutation, publication and commit with requested dependency isolation.",
            Site("src/Romd.Persistence/ReferenceData/ReferenceMutationSession.cs", "ReferenceMutationSession", "FlushAsync", "SaveChangesAsync"),
            Site("src/Romd.Persistence/ReferenceData/ReferenceMutationSession.cs", "ReferenceMutationSession", "RunAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.BootstrapProtocol,
            "Typed built-in region import flushes generated identities and effective facts inside the importer's catalog-lock transaction.",
            Site("src/Romd.Persistence/ReferenceData/RegionCatalogImport.cs", "RegionCatalogImport", "ImportAsync", "SaveChangesAsync", 2)),
        Boundary(TransactionBoundaryKind.BootstrapProtocol,
            "Worker built-in catalog import owns one atomic typed-catalog upgrade/publication transaction and refuses downgrades.",
            Site("src/Romd.Persistence/ReferenceData/RomdCatalogImporter.cs", "RomdCatalogImporter", "ImportAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.BootstrapProtocol,
            "Typed built-in system import flushes generated identities and effective facts inside the importer's catalog-lock transaction.",
            Site("src/Romd.Persistence/ReferenceData/SystemCatalogImport.cs", "SystemCatalogImport", "ImportAsync", "SaveChangesAsync", 2)),
        Boundary(TransactionBoundaryKind.ReadSnapshot,
            "Typed reference pages/details read their effective dependent facts in one repeatable-read snapshot.",
            Site("src/Romd.Persistence/ReferenceData/TypedReferenceReaders.cs", "SystemReferenceReader", "GetAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/ReferenceData/TypedReferenceReaders.cs", "SystemReferenceReader", "ListAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.ReadSnapshot,
            "Descriptor retention checks bind the exact current file/representation references in one repeatable-read snapshot.",
            Site("src/Romd.Persistence/Repositories/ConsumerDerivedDescriptorSource.cs", "ConsumerDerivedDescriptorSource", "IsRetainedAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "Lease deferral commits its fenced execution transition independently of application acceptance.",
            Site("src/Romd.Persistence/Repositories/JobExecutionDeferral.cs", "JobExecutionDeferral", "DeferAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.ReadSnapshot,
            "Draft evaluation resolves configuration and candidates under a consistent read snapshot.",
            Site("src/Romd.Persistence/Repositories/LibraryDraftEvaluator.cs", "LibraryDraftEvaluator", "EvaluateAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "Execution capture, verification and completion persist fenced, recoverable worker-attempt transitions in bounded transactions.",
            Site("src/Romd.Persistence/Repositories/Reconciliation/MediaReconciliationExecution.Capture.cs", "MediaReconciliationExecution", "CaptureAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/Repositories/Reconciliation/MediaReconciliationExecution.Completion.cs", "MediaReconciliationExecution", "FailAttemptAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/Repositories/Reconciliation/MediaReconciliationExecution.Completion.cs", "MediaReconciliationExecution", "FinishAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/Repositories/Reconciliation/MediaReconciliationExecution.Completion.cs", "MediaReconciliationExecution", "SupersedeAttemptAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/Repositories/Reconciliation/MediaReconciliationExecution.Verification.cs", "MediaReconciliationExecution", "RecordVerificationAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "Selection advancement, refresh and termination persist fenced, recoverable worker state in bounded transactions.",
            Site("src/Romd.Persistence/Repositories/Reconciliation/MediaReconciliationSelections.cs", "MediaReconciliationSelections", "AdvanceAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/Repositories/Reconciliation/MediaReconciliationSelections.cs", "MediaReconciliationSelections", "FinishAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/Repositories/Reconciliation/MediaReconciliationSelections.cs", "MediaReconciliationSelections", "RefreshAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/Repositories/Reconciliation/MediaReconciliationSelections.cs", "MediaReconciliationSelections", "StopOwnedAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.ReadSnapshot,
            "Selection planning captures demand and evidence in one repeatable-read snapshot before worker transition/effects.",
            Site("src/Romd.Persistence/Repositories/Reconciliation/MediaReconciliationSelections.cs", "MediaReconciliationSelections", "ReadAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.ReadSnapshot,
            "Reconciliation planning captures selection and evidence in one repeatable-read snapshot before external effects.",
            Site("src/Romd.Persistence/Repositories/Reconciliation/MediaReconciliationSnapshotReader.cs", "MediaReconciliationSnapshotReader", "ReadAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.ReadSnapshot,
            "CD download planning binds current file identities and delivery admission in one repeatable-read snapshot.",
            Site("src/Romd.Persistence/Repositories/RomCdArchiveDownloadCatalog.cs", "RomCdArchiveDownloadCatalog", "OwnedSnapshotAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.ReadSnapshot,
            "Content delivery admission binds current file identity and retained logical facts in one repeatable-read snapshot.",
            Site("src/Romd.Persistence/Repositories/RomContentCatalog.cs", "RomContentCatalog", "OwnedSnapshotAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.ReadSnapshot,
            "Logical inspection reads one retained source snapshot so the supplied exact file identity cannot drift between queries.",
            Site("src/Romd.Persistence/Repositories/RomRetainedLogicalInspectionReader.cs", "RomRetainedLogicalInspectionReader", "ReadSnapshotAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.SavepointFlushProtocol,
            "Minting flushes prior caller changes before its narrow savepoint; refused alias insertion rolls back only the mint and never the caller's earlier staged work.",
            Site("src/Romd.Persistence/Repositories/TaxonomyEntryProjection.cs", "TaxonomyEntryProjection", "MintAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.UnitOfWorkAdapter,
            "DbContext override delegates the actual EF save after applying audit/write-guard behavior; it is not an application transaction owner.",
            Site("src/Romd.Persistence/RomDbContext.cs", "RomdDbContext", "SaveChanges", "SaveChanges"),
            Site("src/Romd.Persistence/RomDbContext.cs", "RomdDbContext", "SaveChangesAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "Subscription acceptance/check/application still own adapter transactions and saves; migrate catalog/source locks, review state and durable import dispatch together.",
            Site("src/Romd.Persistence/Subscriptions/DatCatalogEnrollmentService.cs", "DatCatalogEnrollmentService", "ApplyAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/Subscriptions/DatCatalogEnrollmentService.cs", "DatCatalogEnrollmentService", "ApplyAsync", "SaveChangesAsync"),
            Site("src/Romd.Persistence/Subscriptions/DatCatalogEnrollmentService.cs", "DatCatalogEnrollmentService", "CheckCoreAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/Subscriptions/DatCatalogEnrollmentService.cs", "DatCatalogEnrollmentService", "CheckCoreAsync", "SaveChangesAsync", 2),
            Site("src/Romd.Persistence/Subscriptions/DatCatalogEnrollmentService.cs", "DatCatalogEnrollmentService", "StopAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/Subscriptions/DatCatalogEnrollmentService.cs", "DatCatalogEnrollmentService", "StopAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.WorkerProtocol,
            "Scheduled check failure/backoff is recorded in its own short catalog/source-locked recovery transaction.",
            Site("src/Romd.Persistence/Subscriptions/DatCatalogEnrollmentService.cs", "DatCatalogEnrollmentService", "RecordScheduledFailureAsync", "BeginTransactionAsync"),
            Site("src/Romd.Persistence/Subscriptions/DatCatalogEnrollmentService.cs", "DatCatalogEnrollmentService", "RecordScheduledFailureAsync", "SaveChangesAsync")),
        Boundary(TransactionBoundaryKind.MigrationDebt,
            "System import acceptance still orchestrates the use-case transaction in Persistence; migrate enabling and durable upload acceptance together.",
            Site("src/Romd.Persistence/Systems/SystemSetupService.cs", "SystemSetupService", "ImportAsync", "BeginTransactionAsync")),
        Boundary(TransactionBoundaryKind.ReadSnapshot,
            "System setup pages read catalog, ownership and processing facts from one repeatable-read snapshot.",
            Site("src/Romd.Persistence/Systems/SystemSetupService.cs", "SystemSetupService", "ListAsync", "BeginTransactionAsync")),
    ];

    private enum TransactionBoundaryKind
    {
        MigrationDebt,
        UnitOfWorkAdapter,
        ReadSnapshot,
        WorkerProtocol,
        BootstrapProtocol,
        BatchFlushProtocol,
        PublicationFlushProtocol,
        SavepointFlushProtocol,
        StandalonePublication
    }

    [Fact]
    public void AdditionalTransactionBoundaries_HaveReviewedShapeAndDoNotReclassifyRepositoryDebt()
    {
        var sites = AdditionalTransactionBoundaries.SelectMany(boundary => boundary.Sites).ToArray();
        sites.Length.ShouldBe(103);
        sites.Sum(site => site.Count).ShouldBe(115);
        sites.Select(site => site.Key).Distinct().Count().ShouldBe(103);
        var initialKeys = InitialDebt.Select(site => site.Key).ToHashSet();
        sites.ShouldAllBe(site => !initialKeys.Contains(site.Key));
        AdditionalTransactionBoundaries.ShouldAllBe(boundary =>
            !string.IsNullOrWhiteSpace(boundary.Rationale) && !boundary.Sites.IsEmpty);
    }

    [Theory]
    [InlineData("MigrationDebt", 30, 31)]
    [InlineData("UnitOfWorkAdapter", 10, 10)]
    [InlineData("ReadSnapshot", 14, 14)]
    [InlineData("WorkerProtocol", 30, 34)]
    [InlineData("BootstrapProtocol", 13, 19)]
    [InlineData("BatchFlushProtocol", 1, 2)]
    [InlineData("PublicationFlushProtocol", 1, 1)]
    [InlineData("SavepointFlushProtocol", 1, 1)]
    [InlineData("StandalonePublication", 3, 3)]
    public void AdditionalTransactionBoundaries_KeepMigrationDebtDistinctFromIntentionalBoundaries(
        string kind, int expectedSites, int expectedOperations)
    {
        var sites = AdditionalTransactionBoundaries
            .Where(boundary => boundary.Kind.ToString() == kind).SelectMany(boundary => boundary.Sites).ToArray();
        sites.Length.ShouldBe(expectedSites);
        sites.Sum(site => site.Count).ShouldBe(expectedOperations);
    }

    [Theory]
    [InlineData("class FeatureService")]
    [InlineData("record FeatureStore")]
    [InlineData("record class FeatureProjection")]
    [InlineData("readonly record struct FeatureValue")]
    [InlineData("readonly struct FeatureValue")]
    [InlineData("interface IFeature")]
    public void ObserveSource_SaveCapability_IsRecordedRegardlessOfTypeNameOrKind(string declaration)
    {
        const string path = "src/Romd.Infrastructure/Experimental/Feature.cs";
        string source = $$"""
            public {{declaration}}
            {
                public async Task PersistAsync() => await context.SaveChangesAsync();
            }
            """;

        var observed = GroupOccurrences(ObserveSource(source, path));

        var site = observed.Values.ShouldHaveSingleItem();
        site.Path.ShouldBe(path);
        site.Class.ShouldBe(declaration.Split(' ')[^1]);
        site.Method.ShouldBe("PersistAsync");
        site.Operation.ShouldBe("SaveChangesAsync");
        site.Count.ShouldBe(1);
    }

    [Fact]
    public void ObserveSource_ConditionalUnqualifiedAndGenericOperations_AreCounted()
    {
        const string path = "src/Romd.Persistence/Experimental/Feature.cs";
        const string source = """
            public sealed class FeatureStore
            {
                public void Persist()
                {
                    context.SaveChanges();
                    context?.SaveChanges();
                    SaveChangesAsync();
                    context.Database.BeginTransaction();
                    BeginTransactionAsync<T>();
                }
            }
            """;

        var observed = GroupOccurrences(ObserveSource(source, path));

        AssertMatchesReviewedSites(observed.Values,
        [
            Site(path, "FeatureStore", "Persist", "SaveChanges", 2),
            Site(path, "FeatureStore", "Persist", "SaveChangesAsync"),
            Site(path, "FeatureStore", "Persist", "BeginTransaction"),
            Site(path, "FeatureStore", "Persist", "BeginTransactionAsync")
        ]);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("moved")]
    [InlineData("renamed")]
    [InlineData("duplicate")]
    [InlineData("removed")]
    public void ReviewedSiteComparison_CapabilityChanged_RejectsNewMovedRenamedDuplicatedOrStaleSite(string change)
    {
        const string path = "src/Romd.Persistence/FeatureStore.cs";
        var currentPath = change == "moved" ? "src/Romd.Infrastructure/Other/FeatureStore.cs" : path;
        var typeName = change == "renamed" ? "FeatureService" : "FeatureStore";
        var operations = change switch
        {
            "new" => "await context.SaveChangesAsync(); await context.Database.BeginTransactionAsync();",
            "duplicate" => "await context.SaveChangesAsync(); await context.SaveChangesAsync();",
            "removed" => "",
            _ => "await context.SaveChangesAsync();"
        };
        string source = $$"""
            public sealed class {{typeName}}
            {
                public async Task PersistAsync() { {{operations}} }
            }
            """;
        var observed = GroupOccurrences(ObserveSource(source, currentPath));

        Should.Throw<ShouldAssertException>(() => AssertMatchesReviewedSites(observed.Values,
            [Site(path, "FeatureStore", "PersistAsync", "SaveChangesAsync")]));
    }

    private static void AssertMatchesReviewedSites(
        IEnumerable<RepositoryTransactionDebt> actual,
        IEnumerable<RepositoryTransactionDebt> expected) =>
        actual.Select(Format).Order(StringComparer.Ordinal).ToArray().ShouldBe(
            expected.Select(Format).Order(StringComparer.Ordinal).ToArray(),
            "Transaction capabilities require explicit ownership review, independent of the adapter's name. " +
            "New, moved, renamed, duplicated, stale, or resurrected sites must be resolved explicitly.");

    private sealed record ReviewedTransactionBoundary(
        TransactionBoundaryKind Kind,
        string Rationale,
        ImmutableArray<RepositoryTransactionDebt> Sites);

    private static ReviewedTransactionBoundary Boundary(
        TransactionBoundaryKind kind,
        string rationale,
        params RepositoryTransactionDebt[] sites) => new(kind, rationale, [.. sites]);

    private static RepositoryTransactionDebt Site(
        string path, string declaringType, string method, string operation, int count = 1) =>
        new(new RepositoryTransactionDebtKey(path, declaringType, method, operation), count);
}
