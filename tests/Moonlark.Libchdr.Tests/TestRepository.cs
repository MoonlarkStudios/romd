namespace Moonlark.Libchdr.Tests;

/// <summary>Finds the source checkout without loading native code, for tests that only read repository files.</summary>
internal static class TestRepository
{
    internal static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "native", "libchdr", "libchdr.json")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Tests require the source checkout and its verified synthetic fixtures; none are silently skipped.");
    }
}
