using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>
///     Migration guard for repository-owned saves and transactions. The frozen
///     baseline records known debt; it does not approve that debt. Migrating a
///     site requires adding its key to <see cref="RetiredDebt"/> without
///     changing <see cref="InitialDebt"/>.
/// </summary>
public sealed class RepositoryTransactionOwnershipArchitectureTests
{
    private static readonly ImmutableArray<string> RepositorySourceRoots =
        ["src/Romd.Persistence", "src/Romd.Infrastructure"];

    private static readonly ImmutableHashSet<string> TransactionOperations =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "SaveChanges",
            "SaveChangesAsync",
            "BeginTransaction",
            "BeginTransactionAsync");

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
        // Neutral-source-identity cutover (#152): UpdateGameTitlesBatchAsync's save moved into
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
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/LibraryRepository.cs",
                "LibraryRepository",
                "AddAsync",
                "BeginTransactionAsync"),
            new RepositoryTransactionDebtKey(
                "src/Romd.Persistence/Repositories/LibraryRepository.cs",
                "LibraryRepository",
                "AddAsync",
                "SaveChangesAsync"));

    // #129: these worker protocol operations own their short transactions by design. They do not
    // commit a caller's application mutation: acceptance stages dispatch in the caller's transaction.
    private static readonly ImmutableArray<RepositoryTransactionDebt> WorkerProtocolTransactions =
    [
        Debt("ClaimedJobRepository.cs", "ClaimedJobRepository", "TryClaimExecutionAsync", "BeginTransactionAsync"),
        Debt("ClaimedJobRepository.cs", "ClaimedJobRepository", "TryUpdateClaimedAsync", "BeginTransactionAsync"),
        Debt("JobDispatchRepository.cs", "JobDispatchRepository", "AcknowledgeAsync", "BeginTransactionAsync"),
        Debt("JobItemRepository.cs", "JobItemRepository", "AddRangeAsync", "BeginTransactionAsync")
    ];

    // The legacy immediate title write must hold its parent revision lock through child writes.
    // Retire this adapter together with UpdateAsync's frozen save site once every caller stages
    // its mutation in an application-owned transaction. New use cases must use staged methods.
    private static readonly ImmutableArray<RepositoryTransactionDebt> LegacyAggregateConcurrencyTransactions =
    [
        Debt("TitleRepository.cs", "TitleRepository", "UpdateAsync", "BeginTransactionAsync")
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
    public void RepositoryTransactionOwnershipDebt_MatchesInitialDebtMinusExplicitRetirements()
    {
        var initialKeys = InitialDebt.Select(site => site.Key).ToImmutableHashSet();
        RetiredDebt.ShouldAllBe(site => initialKeys.Contains(site));

        var observed = ObserveRepositoryTransactionDebt();
        observed.Keys.Where(RetiredDebt.Contains).ShouldBeEmpty(
            "A retired transaction-ownership site must never be resurrected.");

        var expected = InitialDebt
            .Where(site => !RetiredDebt.Contains(site.Key))
            .Concat(WorkerProtocolTransactions)
            .Concat(LegacyAggregateConcurrencyTransactions)
            .Select(Format)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = observed.Values
            .Select(Format)
            .Order(StringComparer.Ordinal)
            .ToArray();

        actual.ShouldBe(
            expected,
            "Repository transaction ownership is migration debt. " +
            "New, moved, renamed, duplicated, stale, or resurrected sites must be resolved explicitly.");
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

    [Fact]
    public void ObserveSource_NonRepositoryAndValueTypeDeclarations_AreNotRecorded()
    {
        const string source = """
            public sealed class FeatureService
            {
                public async Task PersistAsync() => await context.SaveChangesAsync();
            }

            public readonly record struct ValueRepository
            {
                public async Task PersistAsync() => await context.SaveChangesAsync();
            }

            public readonly struct StructRepository
            {
                public async Task PersistAsync() => await context.SaveChangesAsync();
            }

            public interface ContractRepository
            {
                public async Task PersistAsync() => await context.SaveChangesAsync();
            }
            """;

        ObserveSource(source, "src/Romd.Infrastructure/Experimental/OtherTypes.cs")
            .ShouldBeEmpty();
    }

    private static IReadOnlyDictionary<RepositoryTransactionDebtKey, RepositoryTransactionDebt>
        ObserveRepositoryTransactionDebt()
        => GroupOccurrences(GetRepositorySourceFiles().SelectMany(ObserveFile));

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
            if (containingType is null || !IsRepositoryReferenceType(containingType))
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

    private static bool IsRepositoryReferenceType(TypeDeclarationSyntax declaration) =>
        declaration.Identifier.ValueText.EndsWith("Repository", StringComparison.Ordinal)
        && (declaration is ClassDeclarationSyntax
            || (declaration is RecordDeclarationSyntax record
                && !record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)));

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

    private static IReadOnlyList<string> GetRepositorySourceFiles()
    {
        string repositoryRoot = FindRepositoryRoot();

        return RepositorySourceRoots
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
