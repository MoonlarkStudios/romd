namespace Moonlark.Native.Engineering.Core;

/// <summary>Keeps local outputs inside the ignored artifacts tree, refusing redirected components before any mutation.</summary>
internal static class ArtifactsPath
{
    private const string Outside = "Local outputs must remain under the ignored artifacts directory";

    internal static Result<string> ValidateDirectory(string output, string root)
    {
        string artifacts = Path.Combine(Path.GetFullPath(root), "artifacts");
        string candidate = Path.GetFullPath(output);
        if (!IsWithin(candidate, artifacts))
            return new Failure(Outside);
        string current = artifacts;
        foreach (string component in (string[])["", .. Components(candidate)[Components(artifacts).Length..]])
        {
            if (component.Length > 0)
                current = Path.Combine(current, component);
            if (IsLink(current))
                return new Failure("artifacts output must not contain a symlink: " + current);
            if (File.Exists(current))
                return new Failure("artifacts output must be a directory: " + current);
        }
        string resolved = Resolve(candidate);
        return IsWithin(resolved, Path.Combine(Resolve(root), "artifacts")) ? resolved : new Failure(Outside);
    }

    /// <summary>Refuses an existing symlink or non-regular leaf (directory, FIFO, socket, device) before a default log is opened for appending.</summary>
    internal static Failure? ValidateLogFile(string path)
    {
        if (IsLink(path))
            return new Failure("Default artifacts log must not be a symlink");
        return Check.That(!Path.Exists(path) || IsRegularFile(path), "Default artifacts log must be a regular file");
    }

    internal static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>An existing regular file. .NET exposes no Unix file type, so Unix asks test(1), whose -f is S_ISREG.</summary>
    internal static bool IsRegularFile(string path) =>
        File.Exists(path) && (OperatingSystem.IsWindows()
            ? !IsLink(path)
            : ProcessRunner.Execute(["test", "-f", path], UnixSystemTools).ExitCode == 0);

    /// <summary>Component-wise containment. Unix compares ordinally, so a case variant of artifacts is outside it even on case-insensitive volumes.</summary>
    internal static bool IsWithin(string path, string directory)
    {
        string[] inner = Components(path);
        string[] outer = Components(directory);
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return string.Equals(Path.GetPathRoot(Path.GetFullPath(path)), Path.GetPathRoot(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase)
            && inner.Length >= outer.Length && outer.Zip(inner).All(pair => comparer.Equals(pair.First, pair.Second));
    }

    private static readonly IReadOnlyDictionary<string, string> UnixSystemTools =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = "/usr/bin:/bin" };

    /// <summary>Resolves every symlink in an absolute path; a missing remainder is appended unresolved.</summary>
    internal static string Resolve(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full) ?? throw new IOException("Path has no root: " + full);
        string[] parts = Components(full);
        for (int index = 0; index < parts.Length; index++)
        {
            string next = Path.Combine(current, parts[index]);
            var info = new FileInfo(next);
            if (info.LinkTarget is not null)
            {
                FileSystemInfo target = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
                next = Resolve(target.FullName);
            }
            else if (!info.Exists && !Directory.Exists(next))
            {
                return Path.Combine([current, .. parts[index..]]);
            }
            current = next;
        }
        return current;
    }

    /// <summary>The components of a normalized absolute path below its root.</summary>
    private static string[] Components(string path)
    {
        string full = Path.GetFullPath(path);
        return full[(Path.GetPathRoot(full)?.Length ?? 0)..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
    }
}
