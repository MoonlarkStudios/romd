using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Guardrail for the ADR decision "DbContext Concurrency: Scoped Contexts Are Single-Flight"
///     (docs/decisions/admin-use-case-transaction-event-boundaries.md, issue #87). Admin
///     application query handlers and Infrastructure persistence repositories resolve one scoped
///     RomdDbContext, so <c>Task.WhenAll</c> in those trees risks concurrent EF operations on a
///     single context. Sequential awaits are the rule; parallel reads require explicitly separate
///     scopes and an explicit consistency statement.
/// </summary>
public sealed class DbContextSingleFlightBoundaryTests
{
    private const string AdminApplicationRoot = "src/Romd.Admin.Application";
    private const string PersistenceRepositoriesRoot = "src/Romd.Persistence/Repositories";

    private static readonly Regex TaskWhenAllPattern = new(
        @"\bTask\s*\.\s*WhenAll\b",
        RegexOptions.CultureInvariant);

    // Empty by design after #87. Add an entry (repository-relative path -> justification) only
    // for a Task.WhenAll that provably never reaches a scoped DbContext, and cite the ADR
    // decision in the justification.
    private static readonly IReadOnlyDictionary<string, string> AllowedTaskWhenAllFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Romd.Admin.Application/Diagnostics/Queries/GetOperationalDiagnostics/GetOperationalDiagnostics.cs"] =
                "Each operational probe creates and retains an independent DI scope, and the sections are " +
                "intentionally independent point-in-time health samples with no cross-section consistency requirement."
        };

    [Fact]
    public void AdminQueryHandlersAndPersistenceRepositories_DoNotUseTaskWhenAll()
    {
        var scannedFiles = GetScannedSourceFiles();
        scannedFiles.ShouldNotBeEmpty();

        var violations = scannedFiles
            .Where(file => TaskWhenAllPattern.IsMatch(File.ReadAllText(file)))
            .Select(ToRepositoryRelativePath)
            .Where(relativePath => !AllowedTaskWhenAllFiles.ContainsKey(relativePath))
            .Order(StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void TaskWhenAllAllowlist_ContainsOnlyScannedFilesThatStillUseTaskWhenAll()
    {
        var scannedFilesByRelativePath = GetScannedSourceFiles()
            .ToDictionary(ToRepositoryRelativePath, file => file, StringComparer.Ordinal);

        var staleEntries = AllowedTaskWhenAllFiles.Keys
            .Where(relativePath =>
                !scannedFilesByRelativePath.TryGetValue(relativePath, out string? file) ||
                !TaskWhenAllPattern.IsMatch(File.ReadAllText(file)))
            .Order(StringComparer.Ordinal)
            .ToList();

        staleEntries.ShouldBeEmpty();
    }

    private static IReadOnlyList<string> GetScannedSourceFiles()
    {
        var adminQueryFiles = Directory
            .GetFiles(FindRepositoryDirectory(AdminApplicationRoot), "*.cs", SearchOption.AllDirectories)
            .Where(IsRepositorySourceFile)
            .Where(file => ToRepositoryRelativePath(file).Contains("/Queries/", StringComparison.Ordinal));
        var repositoryFiles = Directory
            .GetFiles(FindRepositoryDirectory(PersistenceRepositoriesRoot), "*.cs", SearchOption.TopDirectoryOnly);

        return adminQueryFiles
            .Concat(repositoryFiles)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string FindRepositoryDirectory(string relativePath)
    {
        string path = Path.Combine(FindRepositoryRoot(), relativePath);

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Could not find repository directory '{relativePath}'.");
        }

        return path;
    }

    private static string ToRepositoryRelativePath(string path) =>
        Path.GetRelativePath(FindRepositoryRoot(), path).Replace('\\', '/');

    private static bool IsRepositorySourceFile(string path)
    {
        string relativePath = ToRepositoryRelativePath(path);

        return !relativePath.Contains("/bin/", StringComparison.Ordinal)
               && !relativePath.Contains("/obj/", StringComparison.Ordinal);
    }

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
}
