namespace Romd.Hosting.IntegrationTests.Infrastructure;

/// <summary>
///     Resolves repository paths for source-scan and committed-snapshot tests. The repository root
///     is the nearest ancestor of the test output directory that contains <c>Romd.sln</c>.
/// </summary>
internal static class RepositoryPaths
{
    private static readonly Lazy<string> Root = new(LocateRoot);

    public static string FindRepositoryRoot() => Root.Value;

    public static string FindRepositoryFile(string relativePath)
    {
        string path = Path.Combine(FindRepositoryRoot(), relativePath);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
        }

        return path;
    }

    public static string FindRepositoryDirectory(string relativePath)
    {
        string path = Path.Combine(FindRepositoryRoot(), relativePath);

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Could not find repository directory '{relativePath}'.");
        }

        return path;
    }

    public static string ToRepositoryRelativePath(string path) =>
        Path.GetRelativePath(FindRepositoryRoot(), path).Replace('\\', '/');

    /// <summary>True for tracked source, false for build output under <c>bin/</c> or <c>obj/</c>.</summary>
    public static bool IsRepositorySourceFile(string path)
    {
        string relativePath = ToRepositoryRelativePath(path);

        return !relativePath.Contains("/bin/", StringComparison.Ordinal)
               && !relativePath.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string LocateRoot()
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
