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
        string volumeRoot = Path.GetPathRoot(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("Could not determine the data volume root.");
        return new DriveInfo(volumeRoot).AvailableFreeSpace;
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void AssertDirectoryReadable(string path) =>
        _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToList();
}
