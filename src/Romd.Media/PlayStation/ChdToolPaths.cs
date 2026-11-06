namespace Romd.Media.PlayStation;

/// <summary>Detects links in a source or output path, including all of its named ancestors.</summary>
internal static class ChdToolPaths
{
    internal static bool Linked(string path)
    {
        FileSystemInfo? item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        while (item is not null)
        { if (item.LinkTarget is not null) return true; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent; }
        return false;
    }
}
