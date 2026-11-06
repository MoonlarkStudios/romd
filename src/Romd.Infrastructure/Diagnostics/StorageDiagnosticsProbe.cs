namespace Romd.Infrastructure.Diagnostics;

public interface IStorageDiagnosticsProbe
{
    long GetAvailableFreeSpace(string path);
    bool DirectoryExists(string path);
    void AssertDirectoryReadable(string path);
}

public sealed class FileSystemStorageDiagnosticsProbe : IStorageDiagnosticsProbe
{
    public long GetAvailableFreeSpace(string path)
    {
        // Keep the mount-bearing path: '/' need not share the data directory's filesystem.
        return new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace;
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void AssertDirectoryReadable(string path) =>
        _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToList();
}
