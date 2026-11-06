using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>
///     Capability guard for saves and transactions across Persistence and Infrastructure,
///     independent of declaring-type names or kinds. The frozen repository baseline records
///     known debt; it does not approve that debt. Migrating a baseline site requires adding
///     its key to <see cref="RetiredDebt"/> without changing <see cref="InitialDebt"/>.
///     Previously unobserved capabilities are classified separately with exact site counts.
/// </summary>
public sealed partial class RepositoryTransactionOwnershipArchitectureTests
{
    private static readonly ImmutableArray<string> PersistenceSourceRoots =
        ["src/Romd.Persistence", "src/Romd.Infrastructure"];

    private static readonly ImmutableHashSet<string> TransactionOperations =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "SaveChanges",
            "SaveChangesAsync",
            "BeginTransaction",
            "BeginTransactionAsync",
            "ExecuteInTransactionAsync");

    private static readonly ImmutableArray<RepositoryTransactionDebt> InitialDebt =
    [
        Debt("BiosRepository.cs", "BiosRepository", "AddRangeAsync", "SaveChangesAsync"),
        Debt("BiosRepository.cs", "BiosRepository", "AddGameMappingsAsync", "SaveChangesAsync"),
        Debt("BulkEnrichmentJobRepository.cs", "BulkEnrichmentJobRepository", "AddAsync", "SaveChangesAsync"),
        Debt("BulkEnrichmentJobRepository.cs", "BulkEnrichmentJobRepository", "UpdateAsync", "SaveChangesAsync"),
        Debt("CollectionRepository.cs", "CollectionRepository", "AddAsync", "SaveChangesAsync"),
        Debt("CollectionRepository.cs", "CollectionRepository", "UpdateAsync", "SaveChangesAsync"),
        Debt("DatRepository.cs", "DatRepository", "AddAsync", "SaveChangesAsync"),
        Debt("DatRepository.cs", "DatRepository", "AddGamesBatchAsync", "SaveChangesAsync"),
        Debt("DatRepository.cs", "DatRepository", "AddGameRegionsBatchAsync", "SaveChangesAsync"),
        Debt("DatRepository.cs", "DatRepository", "AddGameLanguagesBatchAsync", "SaveChangesAsync"),
        Debt("DatRepository.cs", "DatRepository", "LinkDatRomsToExistingRomFilesAsync", "SaveChangesAsync"),
        // Neutral-source-identity cutover: UpdateGameTitlesBatchAsync's save moved into
        // WriteTitleSourceLinksAsync; the derivation facade then absorbed the batch-ingest entry
        // upsert (UpsertSourceEntriesAsync retired into TitleDerivationService, a non-repository
        // catalog service outside this ledger's scan).
        Debt("DatRepository.cs", "DatRepository", "WriteTitleSourceLinksAsync", "SaveChangesAsync"),
        Debt("EnrichmentJobRepository.cs", "EnrichmentJobRepository", "AddAsync", "SaveChangesAsync"),
        Debt("EnrichmentJobRepository.cs", "EnrichmentJobRepository", "UpdateAsync", "SaveChangesAsync"),
        Debt("ExportJobRepository.cs", "ExportJobRepository", "AddAsync", "SaveChangesAsync"),
        Debt("ExportJobRepository.cs", "ExportJobRepository", "UpdateAsync", "SaveChangesAsync"),
        Debt("FileRepository.cs", "FileRepository", "AddAsync", "SaveChangesAsync"),
        Debt("FileRepository.cs", "FileRepository", "DeleteAsync", "SaveChangesAsync"),
        Debt("JobItemRepository.cs", "JobItemRepository", "AddRangeAsync", "SaveChangesAsync"),
        Debt("JobRepository.cs", "JobRepository", "UpdateAsync", "SaveChangesAsync"),
        Debt("LanguageRepository.cs", "LanguageRepository", "AddAsync", "SaveChangesAsync"),
        Debt("LanguageRepository.cs", "LanguageRepository", "AddRangeAsync", "SaveChangesAsync"),
        Debt("LanguageRepository.cs", "LanguageRepository", "AddAliasAsync", "SaveChangesAsync"),
        Debt("LanguageRepository.cs", "LanguageRepository", "AddAliasesBatchAsync", "SaveChangesAsync"),
        Debt("LibraryRepository.cs", "LibraryRepository", "AddAsync", "BeginTransactionAsync"),
        Debt("LibraryRepository.cs", "LibraryRepository", "AddAsync", "SaveChangesAsync", 2),
        Debt("LibraryRepository.cs", "LibraryRepository", "UpdateAsync", "BeginTransactionAsync"),
        Debt("LibraryRepository.cs", "LibraryRepository", "UpdateAsync", "SaveChangesAsync", 2),
        Debt("LibraryRepository.cs", "LibraryRepository", "SetDefaultAsync", "BeginTransactionAsync"),
        Debt(
            "LibraryRepository.cs",
            "LibraryRepository",
            "TryReplaceMaterializedProjectionsAndActivateAsync",
            "BeginTransactionAsync"),
        Debt(
            "LibraryRepository.cs",
            "LibraryRepository",
            "TryReplaceMaterializedProjectionsAndActivateAsync",
            "SaveChangesAsync"),
        Debt(
            "LibraryRepository.cs",
            "LibraryRepository",
            "TryReplaceMaterializedProjectionsAndMarkConfigurationInvalidAsync",
            "BeginTransactionAsync"),
        Debt("MaterializationJobRepository.cs", "MaterializationJobRepository", "AddAsync", "SaveChangesAsync"),
        Debt("MaterializationJobRepository.cs", "MaterializationJobRepository", "UpdateAsync", "SaveChangesAsync"),
        Debt("PlatformAliasRepository.cs", "PlatformAliasRepository", "AddAsync", "SaveChangesAsync"),
        Debt("PlatformAliasRepository.cs", "PlatformAliasRepository", "AddRangeAsync", "SaveChangesAsync"),
        Debt("PlatformFieldDefaultRepository.cs", "PlatformFieldDefaultRepository", "SetAsync", "SaveChangesAsync"),
        Debt("PlatformFieldDefaultRepository.cs", "PlatformFieldDefaultRepository", "ClearAsync", "SaveChangesAsync"),
        Debt("PlatformRepository.cs", "PlatformRepository", "AddAsync", "SaveChangesAsync"),
        Debt("PlatformRepository.cs", "PlatformRepository", "AddRangeAsync", "SaveChangesAsync"),
        Debt("RegionRepository.cs", "RegionRepository", "AddAsync", "SaveChangesAsync"),
        Debt("RegionRepository.cs", "RegionRepository", "AddRangeAsync", "SaveChangesAsync"),
        Debt("RegionRepository.cs", "RegionRepository", "AddAliasAsync", "SaveChangesAsync"),
        Debt("RegionRepository.cs", "RegionRepository", "AddAliasesBatchAsync", "SaveChangesAsync"),
        Debt("ReplaceDatJobRepository.cs", "ReplaceDatJobRepository", "AddAsync", "SaveChangesAsync"),
        Debt("ReplaceDatJobRepository.cs", "ReplaceDatJobRepository", "UpdateAsync", "SaveChangesAsync"),
        Debt("RomRepository.cs", "RomRepository", "AddAsync", "SaveChangesAsync"),
        Debt("TitleRepository.cs", "TitleRepository", "AddAsync", "SaveChangesAsync"),
        Debt("TitleRepository.cs", "TitleRepository", "AddRangeAsync", "SaveChangesAsync"),
        Debt("TitleRepository.cs", "TitleRepository", "UpdateAsync", "SaveChangesAsync"),
        Debt("UploadJobRepository.cs", "UploadJobRepository", "AddAsync", "SaveChangesAsync"),
        Debt("UploadJobRepository.cs", "UploadJobRepository", "UpdateAsync", "SaveChangesAsync")
    ];

    private static readonly ImmutableHashSet<RepositoryTransactionDebtKey> RetiredDebt =
        ImmutableHashSet.Create(
            // Removed: the ingest owner flushes payload identities, links and taxonomy junctions at their phase boundaries.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/DatRepository.cs",
                "DatRepository",
                "AddGamesBatchAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/DatRepository.cs",
                "DatRepository",
                "AddGameRegionsBatchAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/DatRepository.cs",
                "DatRepository",
                "AddGameLanguagesBatchAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/DatRepository.cs",
                "DatRepository",
                "LinkDatRomsToExistingRomFilesAsync",
                "SaveChangesAsync"),
            // Removed: Application taxonomy owners flush staged row/alias identities inside the existing mint protocol.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/RegionRepository.cs",
                "RegionRepository",
                "AddAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/RegionRepository.cs",
                "RegionRepository",
                "AddAliasAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/LanguageRepository.cs",
                "LanguageRepository",
                "AddAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/LanguageRepository.cs",
                "LanguageRepository",
                "AddAliasAsync",
                "SaveChangesAsync"),
            // Removed: the existing derivation batch owner flushes staged title identities.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/TitleRepository.cs",
                "TitleRepository",
                "AddRangeAsync",
                "SaveChangesAsync"),
            // Removed: the upload provenance sink owns each fenced batch's flush and commit.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/JobItemRepository.cs",
                "JobItemRepository",
                "AddRangeAsync",
                "SaveChangesAsync"),
            // Removed: Application acceptance owners replaced the three repository-owned job saves.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/BulkEnrichmentJobRepository.cs",
                "BulkEnrichmentJobRepository",
                "AddAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/EnrichmentJobRepository.cs",
                "EnrichmentJobRepository",
                "AddAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/MaterializationJobRepository.cs",
                "MaterializationJobRepository",
                "AddAsync",
                "SaveChangesAsync"),
            // Removed: every file publisher stages identities for an explicit owning flush and commit.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/FileRepository.cs",
                "FileRepository",
                "AddAsync",
                "SaveChangesAsync"),
            // Removed: all upload and replacement acceptances stage work for their Application owner.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/UploadJobRepository.cs",
                "UploadJobRepository",
                "AddAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/ReplaceDatJobRepository.cs",
                "ReplaceDatJobRepository",
                "AddAsync",
                "SaveChangesAsync"),
            // Removed: the alias command owns flush and commit, reading its insertion's identity from a receipt.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/PlatformAliasRepository.cs",
                "PlatformAliasRepository",
                "AddAsync",
                "SaveChangesAsync"),
            // Removed: BIOS grouping flushes its staged identities before staging mappings in the caller's transaction.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/BiosRepository.cs",
                "BiosRepository",
                "AddRangeAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/BiosRepository.cs",
                "BiosRepository",
                "AddGameMappingsAsync",
                "SaveChangesAsync"),
            // Removed: the metadata policy command flushes all removals before staging replacements.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/PlatformFieldDefaultRepository.cs",
                "PlatformFieldDefaultRepository",
                "SetAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/PlatformFieldDefaultRepository.cs",
                "PlatformFieldDefaultRepository",
                "ClearAsync",
                "SaveChangesAsync"),
            // Removed: the materialization owner flushes and commits staged publication before releasing its receipt.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/LibraryRepository.cs",
                "LibraryRepository",
                "TryReplaceMaterializedProjectionsAndActivateAsync",
                "BeginTransactionAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/LibraryRepository.cs",
                "LibraryRepository",
                "TryReplaceMaterializedProjectionsAndActivateAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/LibraryRepository.cs",
                "LibraryRepository",
                "TryReplaceMaterializedProjectionsAndMarkConfigurationInvalidAsync",
                "BeginTransactionAsync"),
            // Removed: export creation stages work for the application-owned acceptance commit.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/ExportJobRepository.cs",
                "ExportJobRepository",
                "AddAsync",
                "SaveChangesAsync"),
            // Removed: checkpoint coordinators flush staged state inside their execution-owned boundaries.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/UploadJobRepository.cs",
                "UploadJobRepository",
                "UpdateAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/ReplaceDatJobRepository.cs",
                "ReplaceDatJobRepository",
                "UpdateAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/ExportJobRepository.cs",
                "ExportJobRepository",
                "UpdateAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/MaterializationJobRepository.cs",
                "MaterializationJobRepository",
                "UpdateAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/EnrichmentJobRepository.cs",
                "EnrichmentJobRepository",
                "UpdateAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/BulkEnrichmentJobRepository.cs",
                "BulkEnrichmentJobRepository",
                "UpdateAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/JobRepository.cs",
                "JobRepository",
                "UpdateAsync",
                "SaveChangesAsync"),
            // Removed: current library commands stage writes; reference catalog and DAT ingest own creation.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/LibraryRepository.cs",
                "LibraryRepository",
                "UpdateAsync",
                "BeginTransactionAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/LibraryRepository.cs",
                "LibraryRepository",
                "UpdateAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/LibraryRepository.cs",
                "LibraryRepository",
                "SetDefaultAsync",
                "BeginTransactionAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/PlatformRepository.cs",
                "PlatformRepository",
                "AddAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/PlatformRepository.cs",
                "PlatformRepository",
                "AddRangeAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/PlatformAliasRepository.cs",
                "PlatformAliasRepository",
                "AddRangeAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/DatRepository.cs",
                "DatRepository",
                "AddAsync",
                "SaveChangesAsync"),
            // Removed: title creation returns its own generated identity after the application flushes staging.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/TitleRepository.cs",
                "TitleRepository",
                "AddAsync",
                "SaveChangesAsync"),
            // Removed: move/merge commands flush staged assignments before dependent reads.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/DatRepository.cs",
                "DatRepository",
                "WriteTitleSourceLinksAsync",
                "SaveChangesAsync"),
            // Fresh media publication stages a complete graph in the caller's transaction; logical creation no longer saves here.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/RomRepository.cs",
                "RomRepository",
                "AddAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/LibraryRepository.cs",
                "LibraryRepository",
                "AddAsync",
                "BeginTransactionAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/LibraryRepository.cs",
                "LibraryRepository",
                "AddAsync",
                "SaveChangesAsync"),
            // Removed: file rows are deleted only by the ledger, inside its own transaction.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/FileRepository.cs",
                "FileRepository",
                "DeleteAsync",
                "SaveChangesAsync"),
            // Removed: collection item commands stage purpose-specific item writes under a
            // collection row lock inside an application-owned transaction.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/CollectionRepository.cs",
                "CollectionRepository",
                "UpdateAsync",
                "SaveChangesAsync"),
            // Removed: collection creation exposes its generated identity only after the application's flush.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/CollectionRepository.cs",
                "CollectionRepository",
                "AddAsync",
                "SaveChangesAsync"),
            // Removed: title updates are purpose-specific staged writes in application transactions.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/TitleRepository.cs",
                "TitleRepository",
                "UpdateAsync",
                "SaveChangesAsync"),
            // Removed: unregistered regions and languages are inserted only by AddAsync, which mints their key.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/RegionRepository.cs",
                "RegionRepository",
                "AddRangeAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/LanguageRepository.cs",
                "LanguageRepository",
                "AddRangeAsync",
                "SaveChangesAsync"),
            // Removed: nothing added aliases in bulk; an ingest adds each auto-created row's alias in its transaction.
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/RegionRepository.cs",
                "RegionRepository",
                "AddAliasesBatchAsync",
                "SaveChangesAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/LanguageRepository.cs",
                "LanguageRepository",
                "AddAliasesBatchAsync",
                "SaveChangesAsync"));

    // These worker protocol operations own their short transactions by design. They do not
    // commit a caller's application mutation: acceptance stages dispatch in the caller's transaction.
    private static readonly ImmutableArray<RepositoryTransactionDebt> WorkerProtocolTransactions =
    [
        // The execution coordinator flushes mapped state before restoring credentials under the same row lock.
        Debt("ClaimedJobRepository.cs", "ClaimedJobRepository", "SaveAsync", "SaveChangesAsync"),
        Site("src/Romd.Infrastructure/Jobs/JobRunner.cs", "JobRunner", "CheckpointAsync", "BeginTransactionAsync"),
        Site("src/Romd.Infrastructure/Jobs/ExportArtifactCleanupJob.cs", "ExportArtifactCleanupJob", "ExecuteAsync", "BeginTransactionAsync"),
        Site("src/Romd.Infrastructure/Jobs/DatReplacementConvergenceSweepJob.cs", "DatReplacementConvergenceSweepJob", "FailStaleReplaceJobsAsync", "BeginTransactionAsync"),
        Debt("ClaimedJobRepository.cs", "ClaimedJobRepository", "TryClaimExecutionAsync", "BeginTransactionAsync"),
        Debt("ClaimedJobRepository.cs", "ClaimedJobRepository", "TryUpdateClaimedAsync", "BeginTransactionAsync"),
        Debt("JobDispatchRepository.cs", "JobDispatchRepository", "AcknowledgeAsync", "BeginTransactionAsync"),
        // The fresh-scope upload sink owns provenance durability before signaling progress.
        Site("src/Romd.Infrastructure/Jobs/Executors/JobItemSink.cs", "JobItemSink", "FlushAsync", "BeginTransactionAsync")
    ];

    [Fact]
    public void InitialDebt_HasFrozenMeasuredShape()
    {
        InitialDebt.Length.ShouldBe(52);
        InitialDebt.Sum(site => site.Count).ShouldBe(54);
        InitialDebt.Select(site => site.Key).Distinct().Count().ShouldBe(52);
        InitialDebt.Select(site => site.Class).Distinct(StringComparer.Ordinal).Count().ShouldBe(20);
        InitialDebt
            .Where(site => site.Operation is "SaveChanges" or "SaveChangesAsync")
            .Sum(site => site.Count)
            .ShouldBe(49);
        InitialDebt
            .Where(site => site.Operation is "BeginTransaction" or "BeginTransactionAsync")
            .Sum(site => site.Count)
            .ShouldBe(5);
    }

    [Fact]
    public void WorkerProtocolTransactions_HaveExactReviewedShapeWithoutReclassifyingMeasuredDebt()
    {
        WorkerProtocolTransactions.Length.ShouldBe(8);
        WorkerProtocolTransactions.Sum(site => site.Count).ShouldBe(8);
        WorkerProtocolTransactions.Select(site => site.Key).Distinct().Count().ShouldBe(8);
        var measured = InitialDebt.Select(site => site.Key)
            .Concat(AdditionalTransactionBoundaries.SelectMany(boundary => boundary.Sites).Select(site => site.Key))
            .ToImmutableHashSet();
        WorkerProtocolTransactions.ShouldAllBe(site => !measured.Contains(site.Key));
    }

    [Fact]
    public void RepositoryTransactionOwnershipDebt_MatchesInitialDebtMinusExplicitRetirements()
    {
        var initialKeys = InitialDebt.Select(site => site.Key).ToImmutableHashSet();
        RetiredDebt.ShouldAllBe(site => initialKeys.Contains(site));

        var observed = ObserveTransactionOwnershipSites();
        observed.Keys.Where(RetiredDebt.Contains).ShouldBeEmpty(
            "A retired transaction-ownership site must never be resurrected.");
        observed.Keys.Where(RetiredAdditionalDebt.Contains).ShouldBeEmpty(
            "A retired adapter transaction-ownership site must never be resurrected.");

        var expected = InitialDebt
            .Where(site => !RetiredDebt.Contains(site.Key))
            .Concat(WorkerProtocolTransactions)
            .Concat(CurrentAdditionalSites(AdditionalTransactionBoundaries, RetiredAdditionalDebt))
            .Concat(ReviewedProtocolExtensions.SelectMany(boundary => boundary.Sites));

        AssertMatchesReviewedSites(observed.Values, expected);
    }

    [Fact]
    public void TitlePorts_AggregateWrites_AreStaged()
    {
        Type[] titlePorts =
        [
            typeof(ITitleAggregateReader),
            typeof(ITitleMetadataStore),
            typeof(ITitleMediaStore),
            typeof(ITitleMergeStore),
            typeof(ITitleCreationStore),
            typeof(ITitleOrphanCleanup),
            typeof(ITitleRowLock)
        ];
        var methods = titlePorts.SelectMany(port => port.GetMethods()).ToArray();
        var immediateWrites = methods
            .Where(method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(Title)
                || typeof(IEnumerable<Title>).IsAssignableFrom(parameter.ParameterType)))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .Where(name => !name.EndsWith("StagedAsync", StringComparison.Ordinal))
            .ToArray();

        immediateWrites.ShouldBeEmpty(
            "Title writes stage state for their owning application or derivation batch. Do not reintroduce " +
            "immediate repository updates that save or open transactions on a title port.");
        // Neither the immediate update nor the whole-aggregate staged update may return.
        methods.ShouldNotContain(method => method.Name == "UpdateAsync" || method.Name == "UpdateStagedAsync");
    }

    [Fact]
    public void ObserveSource_ClassRepositoryOutsidePersistenceFolder_RecordsExactOccurrence()
    {
        const string path = "src/Romd.Infrastructure/Experimental/FeatureRepository.cs";
        const string source = """
            public sealed class FeatureRepository
            {
                public async Task PersistAsync()
                {
                    await context.SaveChangesAsync();
                    await context.SaveChangesAsync();
                }
            }
            """;

        var observed = GroupOccurrences(ObserveSource(source, path));

        observed.Values.ShouldHaveSingleItem().ShouldBe(
            new RepositoryTransactionDebt(
                new RepositoryTransactionDebtKey(
                    path,
                    "FeatureRepository",
                    "PersistAsync",
                    "SaveChangesAsync"),
                2));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("record class")]
    public void ObserveSource_RecordClassRepositoryOutsidePersistenceFolder_RecordsExactOccurrence(
        string recordDeclaration)
    {
        const string path = "src/Romd.Infrastructure/Experimental/FeatureRepository.cs";
        string source = $$"""
            public sealed {{recordDeclaration}} FeatureRepository
            {
                public async Task StartAsync()
                {
                    await context.Database.BeginTransactionAsync();
                }
            }
            """;

        var observed = GroupOccurrences(ObserveSource(source, path));

        observed.Values.ShouldHaveSingleItem().ShouldBe(
            new RepositoryTransactionDebt(
                new RepositoryTransactionDebtKey(
                    path,
                    "FeatureRepository",
                    "StartAsync",
                    "BeginTransactionAsync"),
                1));
    }

    private static IReadOnlyDictionary<RepositoryTransactionDebtKey, RepositoryTransactionDebt>
        ObserveTransactionOwnershipSites()
        => GroupOccurrences(GetPersistenceSourceFiles().SelectMany(ObserveFile));

    private static IReadOnlyDictionary<RepositoryTransactionDebtKey, RepositoryTransactionDebt>
        GroupOccurrences(IEnumerable<RepositoryTransactionDebt> occurrences) =>
        occurrences
            .GroupBy(site => site.Key)
            .ToDictionary(
                group => group.Key,
                group => new RepositoryTransactionDebt(group.Key, group.Count()));

    private static IEnumerable<RepositoryTransactionDebt> ObserveFile(string path) =>
        ObserveSource(File.ReadAllText(path), ToRepositoryRelativePath(path));

    private static IEnumerable<RepositoryTransactionDebt> ObserveSource(
        string source,
        string relativePath)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: relativePath);
        var root = syntaxTree.GetRoot();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string? operation = GetInvokedMethodName(invocation);
            if (operation is null || !TransactionOperations.Contains(operation))
            {
                continue;
            }

            var containingType = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (containingType is null)
            {
                continue;
            }

            yield return new RepositoryTransactionDebt(
                new RepositoryTransactionDebtKey(
                    relativePath,
                    containingType.Identifier.ValueText,
                    GetContainingMethodName(invocation),
                    operation),
                1);
        }
    }

    private static string? GetInvokedMethodName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => null
        };

    private static string GetContainingMethodName(InvocationExpressionSyntax invocation)
    {
        SyntaxNode? callable = invocation.Ancestors().FirstOrDefault(node => node is
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

    private static IReadOnlyList<string> GetPersistenceSourceFiles()
    {
        string repositoryRoot = FindRepositoryRoot();

        return PersistenceSourceRoots
            .SelectMany(sourceRoot => Directory.GetFiles(
                Path.Combine(repositoryRoot, sourceRoot),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static bool HasPathSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(segment, StringComparer.Ordinal);

    private static string ToRepositoryRelativePath(string path) =>
        Path.GetRelativePath(FindRepositoryRoot(), path).Replace('\\', '/');

    private static RepositoryTransactionDebt Debt(
        string fileName,
        string className,
        string method,
        string operation,
        int count = 1) =>
        new(
            new RepositoryTransactionDebtKey(
                $"src/Romd.Persistence/Repositories/{fileName}",
                className,
                method,
                operation),
            count);

    private static string Format(RepositoryTransactionDebt site) =>
        $"{site.Path} | {site.Class}.{site.Method} | {site.Operation} | count={site.Count}";

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

    private sealed record RepositoryTransactionDebt(
        RepositoryTransactionDebtKey Key,
        int Count)
    {
        public string Path => Key.Path;
        public string Class => Key.Class;
        public string Method => Key.Method;
        public string Operation => Key.Operation;
    }

    private sealed record RepositoryTransactionDebtKey(
        string Path,
        string Class,
        string Method,
        string Operation);
}
