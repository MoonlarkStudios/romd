using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Romd.Admin.Application.Storage.Files;
using Romd.Infrastructure.Storage;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>
///     Every CAS write follows one path: <c>FileStorageService</c> stages content, then publishes it and
///     records its file row under the file's mutation lock inside the caller's transaction. These exact
///     allow-lists fail when anything else takes file locks, writes the content store, or creates file
///     rows. Representation publication also holds ordered locks across ownership release, while
///     all CAS and file-row mutations still go through the ledger.
/// </summary>
public sealed class FileLedgerBoundaryArchitectureTests
{
    [Fact]
    public void LegacyFileCreationAndPublicationConveniences_AreAbsentFromPortsAndAdapters()
    {
        typeof(IFileRepository).GetMethod("AddAsync").ShouldBeNull();
        typeof(FileRepository).GetMethod("AddAsync").ShouldBeNull();
        foreach (var type in new[] { typeof(IFileStorageService), typeof(FileStorageService) })
            type.GetMethods().Select(method => method.Name)
                .ShouldNotContain(name => name == "StoreAsync" || name == "PublishAsync");
    }

    private const string Ledger = "src/Romd.Infrastructure/Storage/FileStorageService.cs";

    private static readonly ImmutableHashSet<string> FileLockIdentifiers =
        ImmutableHashSet.Create(StringComparer.Ordinal, "IFileMutationLock", "FileMutationLock");

    private static readonly ImmutableHashSet<string> ContentStoreMethods = ImmutableHashSet.Create(
        StringComparer.Ordinal, "StageAsync", "StoreAsync", "DeleteAsync", "CleanupTemporaryFilesAsync", "EnumerateAsync",
        "RetrieveAsync", "OpenRandomAccessAsync", "OpenSynchronousRaw", "ExistsAsync", "VerifyAsync", "GetInfoAsync");

    /// <summary>The lock's implementation, registration, ledger, and representation publication/source-pin composition.</summary>
    private static readonly ImmutableHashSet<string> FileLockUsers = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "src/Romd.Persistence/Repositories/FileMutationLock.cs",
        // Original-layout confirmation pins exact dependency files under their ledger locks.
        "src/Romd.Persistence/Catalog/OriginalDiscLayoutService.cs",
        "src/Romd.Persistence/MediaFacts/RomCdImportStore.cs", // Worker catalog/ROM/graph authority before publishing incoming ledger files.
        "src/Romd.Infrastructure/DependencyInjection.cs",
        "src/Romd.Admin.Application/Source/Rom/RomRepresentationPublisher.cs",
        // Convergence acceptance/replacement pins the source under its ROM lock; bulk acceptance
        // locks ROMs first and distinct hashes in ordinal order. Publication/release uses that same order.
        // Whole-disc capture/publication locks all logical outputs, then distinct physical sources in order.
        // Re-verification pins and releases one exact retained CHD under its ROM lock, then its file-ledger lock.
        // Retained-proof reevaluation pins its ROM first and acquires distinct artifact hashes in ordinal order.
        // Reconciliation locks the complete current/captured/target graph union before sorted artifact mutation locks.
        "src/Romd.Persistence/Repositories/Reconciliation/MediaReconciliationAuthority.cs",
        Ledger);

    /// <summary>
    ///     Files outside <c>Romd.Storage</c> that use the content store, with the store operations each may
    ///     invoke. Only the ledger writes; startup cleanup removes abandoned staging files; the rest read.
    /// </summary>
    private static readonly ImmutableDictionary<string, ImmutableHashSet<string>> ContentStoreUsers =
        new Dictionary<string, ImmutableHashSet<string>>
        {
            [Ledger] = Methods("StageAsync", "DeleteAsync", "EnumerateAsync", "RetrieveAsync", "ExistsAsync"),
            ["src/Romd.Infrastructure/Storage/ContentStoreStartupCleanup.cs"] = Methods("CleanupTemporaryFilesAsync"),
            ["src/Romd.Infrastructure/Storage/ArtworkDeliveryService.cs"] = Methods("RetrieveAsync"),
            ["src/Romd.Infrastructure/Storage/ConsumerContentArtifactResolver.cs"] = Methods("RetrieveAsync"),
            ["src/Romd.Infrastructure/Storage/ConsumerMediaArtifactResolver.cs"] = Methods("RetrieveAsync"),
            ["src/Romd.Infrastructure/Storage/RomContentReader.cs"] = Methods("OpenRandomAccessAsync", "OpenSynchronousRaw"),
            // Selected CD archive members only; all publication and deletion still use the ledger.
            ["src/Romd.Infrastructure/Storage/RomCdArchiveDownloadReader.cs"] = Methods("OpenRandomAccessAsync"),
            ["src/Romd.Infrastructure/Storage/ConsumerVfsContentResolver.cs"] = Methods("OpenRandomAccessAsync"),
            ["src/Romd.Infrastructure/Storage/ReadOnlyReferenceBlobStore.cs"] = Methods("RetrieveAsync"),
            ["src/Romd.Infrastructure/Storage/ReferenceBlobStore.cs"] = Methods()
        }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>The repository that writes rows for the ledger, and the entity's own mapping.</summary>
    private static readonly ImmutableHashSet<string> FileRowWriters = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "src/Romd.Persistence/Repositories/FileRepository.cs",
        "src/Romd.Persistence/Entities/FileEntityPersistence.cs");

    private static readonly ImmutableHashSet<string> FileRepositoryMutations =
        ImmutableHashSet.Create(StringComparer.Ordinal, "AddAsync", "AddStagedAsync", "DeleteStagedAsync", "RecordArtifactChecksumsAsync");

    [Fact]
    public void LogicalRomArtifactJoins_AreCentralized()
    {
        var violations = RuntimeSources().Where(source => source.Path.StartsWith("src/Romd.Persistence/", StringComparison.Ordinal))
            .Where(source => source.Path is not "src/Romd.Persistence/Queries/RomContentQuery.cs" and
                not "src/Romd.Persistence/Repositories/BiosRepository.cs")
            .Where(source => CombinesLogicalRomsAndArtifacts(source.Root))
            .Select(source => source.Path).ToArray();
        violations.ShouldBeEmpty();
    }

    private static bool CombinesLogicalRomsAndArtifacts(SyntaxNode root) =>
        root.DescendantNodes().Where(node => node is InvocationExpressionSyntax or QueryExpressionSyntax)
            .Any(node => ReferencesAny(node, ["RomFiles"]) && ReferencesAny(node, ["Files"]));

    [Theory]
    [InlineData("db.Files.Join(db.RomFiles, a => a.Id, r => r.FileId, (a, r) => a)")]
    [InlineData("from a in db.Files join r in db.RomFiles on a.Id equals r.FileId select a")]
    [InlineData("db.RomFiles.Select(r => db.Files.First(a => a.Id == r.FileId))")]
    [InlineData("from r in db.RomFiles select db.Files.First(a => a.Id == r.FileId)")]
    public void LogicalRomArtifactGuard_DetectsReversedAndCorrelatedQueries(string expression) =>
        CombinesLogicalRomsAndArtifacts(Parse($"class A {{ object M(Db db) => {expression}; }}")).ShouldBeTrue();

    [Fact]
    public void LogicalRomArtifactGuard_AllowsIndependentQueries() =>
        CombinesLogicalRomsAndArtifacts(Parse("class A { void M(Db db) { var a = db.Files.Count(); var r = db.RomFiles.Count(); } }")).ShouldBeFalse();

    [Fact]
    public void FileLocks_AreTakenOnlyByLedgerAndRepresentationPublication() =>
        RuntimeSources()
            .Where(source => ReferencesAny(source.Root, FileLockIdentifiers))
            .Select(source => source.Path)
            .ShouldBe(FileLockUsers.Order(StringComparer.Ordinal));

    [Fact]
    public void ContentStoreUsers_MatchAllowListAndOnlyTheLedgerWrites()
    {
        var observed = RuntimeSources()
            .Where(source => !source.Path.StartsWith("src/Romd.Storage/", StringComparison.Ordinal))
            .Where(source => ReferencesAny(source.Root, ["IContentAddressableStore"]))
            .ToDictionary(source => source.Path, source => InvokedStoreMethods(source.Root), StringComparer.Ordinal);

        observed.Keys.Order(StringComparer.Ordinal).ShouldBe(ContentStoreUsers.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, methods) in observed)
            methods.ShouldBeSubsetOf(ContentStoreUsers[path], $"{path} may invoke only its allowed store operations.");
    }

    [Fact]
    public void FileRows_AreWrittenOnlyForTheLedger() =>
        RuntimeSources()
            .Where(source => WritesFileRows(source.Root))
            .Select(source => source.Path)
            .ShouldBe(FileRowWriters.Order(StringComparer.Ordinal));

    [Fact]
    public void FileRepositoryMutations_AreInvokedOnlyByTheLedger() =>
        RuntimeSources()
            .Where(source => InvokedMethods(source.Root, ["IFileRepository", "FileRepository"], FileRepositoryMutations).Count > 0)
            .Select(source => source.Path)
            .ShouldBe([Ledger]);

    [Fact]
    public void InvokedRepositoryMutations_DetectsChecksumCapture() =>
        InvokedMethods(Parse("""
            class OtherPublisher(IFileRepository files)
            {
                Task M() => files.RecordArtifactChecksumsAsync(id, hash, size, md5, sha1, ct);
            }
            """), ["IFileRepository", "FileRepository"], FileRepositoryMutations)
            .ShouldBe(["RecordArtifactChecksumsAsync"]);

    [Fact]
    public void InvokedRepositoryMutations_DetectsStagedCreation() =>
        InvokedMethods(Parse("class OtherPublisher(IFileRepository files) { Task M() => files.AddStagedAsync(file, ct); }"),
            ["IFileRepository", "FileRepository"], FileRepositoryMutations).ShouldBe(["AddStagedAsync"]);

    [Fact]
    public void WritesFileRows_DetectsEachWayToWriteTheFilesTable()
    {
        WritesFileRows(Parse("class A { void M(Db db) => db.Files.Add(file); }")).ShouldBeTrue();
        WritesFileRows(Parse("class A { void M(Db db) => db.Files.Remove(file); }")).ShouldBeTrue();
        WritesFileRows(Parse("class A { Task M(Db db) => db.Files.Where(f => f.Id == 1).ExecuteDeleteAsync(); }")).ShouldBeTrue();
        WritesFileRows(Parse("class A { object M() => new FileEntityPersistence(); }")).ShouldBeTrue();
        WritesFileRows(Parse("""class A { string S = "INSERT INTO romd.\"Files\" (\"Sha256\") VALUES (1)"; }""")).ShouldBeTrue();
        WritesFileRows(Parse("""class A { string S = "DELETE FROM romd.\"Files\" WHERE 1 = 1"; }""")).ShouldBeTrue();
        WritesFileRows(Parse("class A { Task M(Db db) => db.Files.ExecuteUpdateAsync(s => s); }")).ShouldBeTrue();
        WritesFileRows(Parse("""class A { string S = "UPDATE romd.\"Files\" SET \"Size\" = 0"; }""")).ShouldBeTrue();
        WritesFileRows(Parse("class A { void M(Db db) => db.RomFiles.Add(file); }")).ShouldBeFalse();
        WritesFileRows(Parse("class A { Task M(Db db) => db.Titles.ExecuteUpdateAsync(s => s); }")).ShouldBeFalse();
        WritesFileRows(Parse("class A { Task M(Db db) => db.DatFiles.Where(f => f.Id == 1).ExecuteDeleteAsync(); }")).ShouldBeFalse();
    }

    [Fact]
    public void InvokedStoreMethods_CountsOnlyCallsOnTheStore() =>
        InvokedStoreMethods(Parse("""
            class A(IContentAddressableStore store, IFileStorageService files)
            {
                private readonly IContentAddressableStore _cas = store;
                async Task M() { await files.StageAsync(null); await store.RetrieveAsync(k); await this._cas.StoreAsync(s); }
            }
            """)).Order(StringComparer.Ordinal).ShouldBe(["RetrieveAsync", "StoreAsync"]);

    [Fact]
    public void InvokedStoreMethods_DetectsSynchronousRawOpens() =>
        InvokedStoreMethods(Parse("""
            class A(IContentAddressableStore store)
            {
                object M() => store.OpenSynchronousRaw(k);
            }
            """)).ShouldBe(["OpenSynchronousRaw"]);

    [Fact]
    public void ReferencesAny_DetectsInlineAndQualifiedConstruction() =>
        ReferencesAny(Parse("class A { object M(Db db) => new Romd.Persistence.Repositories.FileMutationLock(db); }"),
            FileLockIdentifiers).ShouldBeTrue();

    private static ImmutableHashSet<string> Methods(params string[] names) =>
        ImmutableHashSet.Create(StringComparer.Ordinal, names);

    private static bool ReferencesAny(SyntaxNode root, IEnumerable<string> identifiers)
    {
        var names = identifiers.ToHashSet(StringComparer.Ordinal);
        return root.DescendantNodes().OfType<IdentifierNameSyntax>().Any(name => names.Contains(name.Identifier.ValueText));
    }

    private static IReadOnlySet<string> InvokedStoreMethods(CompilationUnitSyntax root) =>
        InvokedMethods(root, ["IContentAddressableStore", "Romd.Storage.IContentAddressableStore"], ContentStoreMethods);

    /// <summary>The given methods invoked on names the file declares with one of <paramref name="types" />.</summary>
    private static IReadOnlySet<string> InvokedMethods(CompilationUnitSyntax root, IReadOnlyCollection<string> types,
        IReadOnlySet<string> methods)
    {
        bool Declared(TypeSyntax? type) => type is not null && types.Contains(type.ToString());
        var receivers = root.DescendantNodes().Select(node => node switch
        {
            ParameterSyntax parameter when Declared(parameter.Type) => parameter.Identifier.ValueText,
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } variable
                when Declared(declaration.Type) => variable.Identifier.ValueText,
            PropertyDeclarationSyntax property when Declared(property.Type) => property.Identifier.ValueText,
            _ => null
        }).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(invocation => invocation.Expression is MemberAccessExpressionSyntax access &&
                                  ReceiverName(access.Expression) is { } receiver && receivers.Contains(receiver)
                ? access.Name.Identifier.ValueText
                : null)
            .OfType<string>()
            .Where(methods.Contains)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string? ReceiverName(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member => member.Name.Identifier.ValueText,
        _ => null
    };

    private static bool WritesFileRows(CompilationUnitSyntax root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(invocation =>
            invocation.Expression is MemberAccessExpressionSyntax access &&
            (access.Name.Identifier.ValueText is "Add" or "AddRange" or "AddAsync" or "AddRangeAsync" or "Remove" or "RemoveRange" &&
             access.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Files" } ||
             access.Name.Identifier.ValueText is "ExecuteDelete" or "ExecuteDeleteAsync" or "ExecuteUpdate" or "ExecuteUpdateAsync" &&
             QueriesFiles(access.Expression))) ||
        root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Any(creation => creation.Type.ToString().EndsWith("FileEntityPersistence", StringComparison.Ordinal)) ||
        root.DescendantTokens().Any(token =>
            (token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.InterpolatedStringTextToken) ||
             token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken) || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)) &&
            (token.ValueText.Contains("INTO romd.\"Files\"", StringComparison.OrdinalIgnoreCase) ||
             token.ValueText.Contains("FROM romd.\"Files\"", StringComparison.OrdinalIgnoreCase) &&
             token.ValueText.Contains("DELETE", StringComparison.OrdinalIgnoreCase) ||
             token.ValueText.Contains("UPDATE romd.\"Files\"", StringComparison.OrdinalIgnoreCase)));

    /// <summary>True when a query chain such as <c>db.Files.Where(...)</c> starts from the Files set.</summary>
    private static bool QueriesFiles(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Files" } => true,
        MemberAccessExpressionSyntax member => QueriesFiles(member.Expression),
        InvocationExpressionSyntax invocation => QueriesFiles(invocation.Expression),
        _ => false
    };

    private static CompilationUnitSyntax Parse(string source) => CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

    private static IEnumerable<(string Path, CompilationUnitSyntax Root)> RuntimeSources()
    {
        string root = FindRepositoryRoot();
        return Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !path.Split('/').Any(segment => segment is "bin" or "obj" or "Migrations"))
            .Order(StringComparer.Ordinal)
            .Select(path => (path, Parse(File.ReadAllText(Path.Combine(root, path)))));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Romd.sln")))
                return directory.FullName;
        throw new InvalidOperationException("Could not find the repository root.");
    }
}
