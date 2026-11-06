using System.Collections.Immutable;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class RepositoryTransactionOwnershipArchitectureTests
{
    // Keep the measured adapter baseline and classifications frozen. Retire only removed
    // migration-debt capabilities; intentional protocols require their own ownership review.
    private static readonly ImmutableHashSet<RepositoryTransactionDebtKey> RetiredAdditionalDebt =
        ImmutableHashSet.Create(
            // Removed: the reviewed source lifecycle command owns mutation and durable intents.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Catalog/DatSourceManagement.cs",
                "DatSourceManagement",
                "ApplyAsync",
                "BeginTransactionAsync"),
            // Removed: the rebuild worker owns proposal durability inside its existing savepoint.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Catalog/DiscSetService.cs",
                "DiscSetService",
                "RefreshAsync",
                "SaveChangesAsync"),
            // Removed: the shared authentication command commits staged sessions before host issuance.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Identity/AccountSessionStore.cs",
                "AccountSessionStore",
                "StartAsync",
                "SaveChangesAsync"),
            // Removed: the artwork completion command owns its coupled acceptance commit.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Artwork/ArtworkCompletionStore.cs",
                "ArtworkCompletionStore",
                "QueueScopeAsync",
                "BeginTransactionAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Artwork/ArtworkCompletionStore.cs",
                "ArtworkCompletionStore",
                "QueueScopeAsync",
                "SaveChangesAsync"),
            // Removed: the provider identity service owns the matching acceptance transaction.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/MetadataProviders/ProviderIdentityStore.cs",
                "ProviderIdentityStore",
                "SaveAsync",
                "BeginTransactionAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/MetadataProviders/ProviderIdentityStore.cs",
                "ProviderIdentityStore",
                "SaveAsync",
                "SaveChangesAsync"),
            // Removed: the enrichment commands own staged job acceptance and commit.
            new RepositoryTransactionDebtKey(
                "src/Romd.Infrastructure/Jobs/EnrichmentScheduler.cs",
                "EnrichmentScheduler",
                "EnqueueAsync",
                "BeginTransactionAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Infrastructure/Jobs/EnrichmentScheduler.cs",
                "EnrichmentScheduler",
                "EnqueueBulkAsync",
                "BeginTransactionAsync"),
            // Removed: the reference asset command owns reservation and its durable publication.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/ReferenceData/ReferenceCatalogService.cs",
                "ReferenceCatalogService",
                "AddAssetAsync",
                "BeginTransactionAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/ReferenceData/ReferenceCatalogService.cs",
                "ReferenceCatalogService",
                "AddAssetAsync",
                "SaveChangesAsync"),
            // Removed: the local artwork pin command owns its publication transaction.
            new RepositoryTransactionDebtKey(
                "src/Romd.Infrastructure/Artwork/LocalArtworkService.cs",
                "LocalArtworkService",
                "PinMediaAsync",
                "BeginTransactionAsync"),
            // Removed: subscription Apply owns its coupled job, dispatch and subscription acceptance.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Subscriptions/DatCatalogEnrollmentService.cs",
                "DatCatalogEnrollmentService",
                "ApplyAsync",
                "BeginTransactionAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Subscriptions/DatCatalogEnrollmentService.cs",
                "DatCatalogEnrollmentService",
                "ApplyAsync",
                "SaveChangesAsync"),
            // Removed: the System Import command owns enablement and upload acceptance.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Systems/SystemSetupService.cs",
                "SystemSetupService",
                "ImportAsync",
                "BeginTransactionAsync"),
            // Removed: the export application command owns acceptance and its durable dispatch.
            new RepositoryTransactionDebtKey(
                "src/Romd.Infrastructure/Jobs/ExportScheduler.cs",
                "ExportScheduler",
                "EnqueueLibraryExportAsync",
                "BeginTransactionAsync"),
            // Removed: subscription check/create commands own candidate and status durability.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Subscriptions/DatCatalogEnrollmentService.cs",
                "DatCatalogEnrollmentService",
                "CheckCoreAsync",
                "BeginTransactionAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Subscriptions/DatCatalogEnrollmentService.cs",
                "DatCatalogEnrollmentService",
                "CheckCoreAsync",
                "SaveChangesAsync"),
            // Removed: the stop command owns removal of subscription tracking and its commit.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Subscriptions/DatCatalogEnrollmentService.cs",
                "DatCatalogEnrollmentService",
                "StopAsync",
                "BeginTransactionAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Subscriptions/DatCatalogEnrollmentService.cs",
                "DatCatalogEnrollmentService",
                "StopAsync",
                "SaveChangesAsync"),
            // Removed: the existing descriptor worker flushes identities/bindings and commits staged layouts and demands.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Catalog/OriginalDiscLayoutService.cs",
                "OriginalDiscLayoutService",
                "RecordAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Catalog/OriginalDiscLayoutService.cs",
                "OriginalDiscLayoutService",
                "ReconcileAsync",
                "SaveChangesAsync"),
            // Removed: no command, adapter or test called the legacy concurrency-save helper.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/ConfigurationSave.cs",
                "ConfigurationSave",
                "TrySaveAsync",
                "SaveChangesAsync"),
            // Removed: the omission command flushes staged intent before reading its response.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Enrichment/EnrichmentWorkbench.cs",
                "EnrichmentWorkbench",
                "SetOmissionAsync",
                "SaveChangesAsync"),
            // Removed: Consumer commands own credential lifecycle transactions through their security port.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Identity/VfsConnectionStore.cs",
                "VfsConnectionStore",
                "CreateAsync",
                "BeginTransactionAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Identity/VfsConnectionStore.cs",
                "VfsConnectionStore",
                "CreateAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Identity/VfsConnectionStore.cs",
                "VfsConnectionStore",
                "RotateAsync",
                "BeginTransactionAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Identity/VfsConnectionStore.cs",
                "VfsConnectionStore",
                "RotateAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Identity/VfsConnectionStore.cs",
                "VfsConnectionStore",
                "RevokeAsync",
                "BeginTransactionAsync"),
            // Removed: prepared password changes execute in the Consumer command's security transaction.
            new RepositoryTransactionDebtKey(
                "src/Romd.Infrastructure/Identity/ConsumerAccountService.cs",
                "ConsumerAccountService",
                "ChangePasswordAsync",
                "BeginTransactionAsync"));

    [Fact]
    public void MeasuredMigrationDebt_HasNoRemainingCapabilities()
    {
        var remaining = InitialDebt
            .Where(site => !RetiredDebt.Contains(site.Key))
            .Concat(CurrentAdditionalSites(
                AdditionalTransactionBoundaries
                    .Where(boundary => boundary.Kind == TransactionBoundaryKind.MigrationDebt)
                    .ToImmutableArray(),
                RetiredAdditionalDebt))
            .ToArray();

        remaining.ShouldBeEmpty();
        remaining.Sum(site => site.Count).ShouldBe(0);
    }

    [Fact]
    public void CurrentAdditionalSites_RetiredMigrationDebt_PreservesOtherReviewedCapabilities()
    {
        var retired = Site("migration.cs", "Store", "StageAsync", "SaveChangesAsync", 2);
        var remaining = Site("migration.cs", "Store", "OtherAsync", "SaveChangesAsync");
        var protocol = Site("worker.cs", "Worker", "ClaimAsync", "BeginTransactionAsync");
        ImmutableArray<ReviewedTransactionBoundary> boundaries =
        [
            Boundary(TransactionBoundaryKind.MigrationDebt, "Pending migration", retired, remaining),
            Boundary(TransactionBoundaryKind.WorkerProtocol, "Execution claim", protocol)
        ];

        var current = CurrentAdditionalSites(boundaries, ImmutableHashSet.Create(retired.Key)).ToArray();

        AssertMatchesReviewedSites(current, [remaining, protocol]);
        boundaries.SelectMany(boundary => boundary.Sites).ShouldBe([retired, remaining, protocol]);
    }

    [Theory]
    [InlineData("UnitOfWorkAdapter")]
    [InlineData("ReadSnapshot")]
    [InlineData("WorkerProtocol")]
    [InlineData("BootstrapProtocol")]
    [InlineData("BatchFlushProtocol")]
    [InlineData("PublicationFlushProtocol")]
    [InlineData("SavepointFlushProtocol")]
    [InlineData("StandalonePublication")]
    public void CurrentAdditionalSites_RetiredIntentionalProtocol_RejectsTheRetirement(string kind)
    {
        var site = Site("protocol.cs", "Protocol", "CommitAsync", "SaveChangesAsync");
        var boundary = Boundary(Enum.Parse<TransactionBoundaryKind>(kind), "Reviewed protocol", site);

        Should.Throw<ShouldAssertException>(() =>
            CurrentAdditionalSites([boundary], ImmutableHashSet.Create(site.Key)).ToArray());
    }

    [Fact]
    public void CurrentAdditionalSites_UnknownRetirement_RejectsTheRetirement()
    {
        var site = Site("unknown.cs", "Store", "StageAsync", "SaveChangesAsync");

        Should.Throw<ShouldAssertException>(() =>
            CurrentAdditionalSites([], ImmutableHashSet.Create(site.Key)).ToArray());
    }

    [Fact]
    public void CurrentAdditionalSites_RetiredSiteStillObserved_RejectsTheCapability()
    {
        var site = Site("migration.cs", "Store", "StageAsync", "SaveChangesAsync");
        var current = CurrentAdditionalSites(
            [Boundary(TransactionBoundaryKind.MigrationDebt, "Pending migration", site)],
            ImmutableHashSet.Create(site.Key));

        Should.Throw<ShouldAssertException>(() => AssertMatchesReviewedSites([site], current));
    }

    private static IEnumerable<RepositoryTransactionDebt> CurrentAdditionalSites(
        ImmutableArray<ReviewedTransactionBoundary> boundaries,
        ImmutableHashSet<RepositoryTransactionDebtKey> retired)
    {
        var migrationKeys = boundaries.Where(boundary => boundary.Kind == TransactionBoundaryKind.MigrationDebt)
            .SelectMany(boundary => boundary.Sites).Select(site => site.Key).ToImmutableHashSet();
        retired.ShouldAllBe(key => migrationKeys.Contains(key),
            "Only measured migration-debt capabilities may be retired; keep intentional protocols and unknown sites guarded.");
        return boundaries.SelectMany(boundary => boundary.Sites).Where(site => !retired.Contains(site.Key));
    }
}
