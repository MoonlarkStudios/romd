namespace Romd.Infrastructure.Jobs;

public sealed class JobWorkspace : IAsyncDisposable
{
    internal static FileStream? TryAcquireExecutionLock(string dataDirectory, Guid jobId)
    {
        string directory = System.IO.Path.Combine(dataDirectory, "temp", "job-locks");
        Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(System.IO.Path.Combine(directory, $"{jobId:N}.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33 or 35)
        {
            return null;
        }
    }

    internal static async Task<FileStream> AcquireExecutionLockAsync(
        string dataDirectory, Guid jobId, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Keep the lock inode after release; replacing it could admit a third owner.
            if (TryAcquireExecutionLock(dataDirectory, jobId) is { } acquired)
                return acquired;
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    public string Path { get; }

    internal JobWorkspace(string path)
    {
        Path = path;
    }

    internal static JobWorkspace Create(string basePath, Guid jobId)
    {
        var path = System.IO.Path.Combine(basePath, "temp", "jobs", jobId.ToString("N"));
        Directory.CreateDirectory(path);
        return new JobWorkspace(path);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
            DeleteExtractionCheckpoint(Path);
        }
        catch
        {
            // Cleanup failure is non-fatal
        }

        return ValueTask.CompletedTask;
    }

    internal static void DeleteExtractionCheckpoint(string workspace)
    {
        string checkpoint = Executors.ArchiveExtractionJournal.PathFor(workspace);
        File.Delete(checkpoint);
        File.Delete(checkpoint + ".pending");
    }
}
