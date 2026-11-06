namespace Romd.Media.Probe;

internal static class MediaProbePaths
{
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static void Validate(string[] paths)
    {
        Check.That(paths.All(path => path is not null && Path.IsPathFullyQualified(path) && path.Length <= 2048 &&
            Path.GetFullPath(path) == path), "absolute normalized qualification paths");
        for (int index = 0; index < paths.Length; index++)
        {
            Check.That(!paths.Take(index).Any(path => string.Equals(path, paths[index], Comparison)),
                "distinct qualification input/output paths");
            for (string? current = paths[index]; current is not null; current = Path.GetDirectoryName(current))
            {
                var item = Directory.Exists(current) ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current);
                Check.That(item.LinkTarget is null, "no symbolic-link aliases in qualification paths");
                if (current == Path.GetPathRoot(current))
                    break;
            }
        }
    }

    internal static bool Overlaps(string first, string second) => Contains(first, second) || Contains(second, first);

    internal static bool Contains(string parent, string path) => string.Equals(parent, path, Comparison) ||
        path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, Comparison);

    internal static void OutsideManagedStorage(string dataDirectory, params string[] paths)
    {
        string[] managed = [Path.Combine(dataDirectory, "temp"), Path.Combine(dataDirectory, "cas")];
        Check.That(!paths.Any(path => managed.Any(root => Overlaps(root, path))),
            "qualification controls and evidence are isolated from managed temporary storage and CAS");
    }
}
