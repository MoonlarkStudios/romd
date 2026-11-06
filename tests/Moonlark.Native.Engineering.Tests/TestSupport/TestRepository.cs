using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Tests.TestSupport;

/// <summary>A disposable directory; tests never write into the source checkout.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    internal TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("moonlark-eng-").FullName;

    internal string Path { get; }

    public void Dispose()
    {
        ClearReadOnlyFiles(Path);
        Directory.Delete(Path, recursive: true);
    }

    private static void ClearReadOnlyFiles(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            return;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            if ((attributes & FileAttributes.Directory) != 0)
                ClearReadOnlyFiles(path);
            else if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }
}

internal static class TestRepository
{
    internal static string Root { get; } = RepositoryRoot.Find(AppContext.BaseDirectory).Value;

    /// <summary>Only process essentials, so agent or developer shell overrides never leak into checks under test.</summary>
    internal static IReadOnlyDictionary<string, string> CleanEnvironment { get; } =
        new[] { "PATH", "HOME", "TMPDIR" }
            .Where(name => Environment.GetEnvironmentVariable(name) is not null)
            .ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name)!, StringComparer.Ordinal);

    /// <summary>Copies repository-relative files or directories into <paramref name="destination"/>.</summary>
    internal static void CopyTo(string destination, params string[] relativePaths)
    {
        foreach (string relative in relativePaths)
        {
            string source = Path.Combine(Root, relative);
            string target = Path.Combine(destination, relative);
            if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
                continue;
            }
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string copy = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
        }
    }

    /// <summary>Fixture-local LF settings also apply when production checks invoke Git directly.</summary>
    internal static string Git(string source, params string[] arguments)
    {
        _ = ProcessRunner.Run(["git", "-C", source, "config", "--local", "core.autocrlf", "false"], CleanEnvironment).Value;
        _ = ProcessRunner.Run(["git", "-C", source, "config", "--local", "core.eol", "lf"], CleanEnvironment).Value;
        return ProcessRunner.Run(["git", "-C", source, .. arguments], CleanEnvironment).Value;
    }
}

/// <summary>A committed synthetic upstream checkout with a pin that matches it exactly.</summary>
internal static class SyntheticSource
{
    internal static (string Source, LibchdrPin Pin) Create(string parent)
    {
        string source = Path.Combine(parent, "source");
        foreach (string header in Authorities.HeaderPaths)
        {
            string path = Path.Combine(source, header);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "synthetic " + header);
        }
        File.WriteAllText(Path.Combine(source, ".gitignore"), "ignored\n");
        File.WriteAllText(Path.Combine(source, "implementation.c"), "int checked_source = 1;\n");
        _ = ProcessRunner.Run(["git", "init", "-q", source], TestRepository.CleanEnvironment).Value;
        TestRepository.Git(source, "add", ".");
        TestRepository.Git(source, "-c", "user.name=Synthetic Test", "-c", "user.email=synthetic@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "-qm", "synthetic test source");
        LibchdrPin real = Authorities.ReadLibchdr(TestRepository.Root).Value.Pin;
        return (source, real with
        {
            Commit = TestRepository.Git(source, "rev-parse", "HEAD"),
            UpstreamVersion = TestRepository.Git(source, "describe", "--always", "--tags", "--long"),
            Headers = Authorities.HeaderPaths.ToDictionary(name => name, name => Digest.Sha256File(Path.Combine(source, name)), StringComparer.Ordinal),
        });
    }
}
