namespace Romd.Infrastructure.Jobs;

public sealed class JobWorkspace : IAsyncDisposable
{
    internal static async Task<FileStream> AcquireExecutionLockAsync(
        string dataDirectory, Guid jobId, CancellationToken ct)
    {
        string directory = System.IO.Path.Combine(dataDirectory, "temp", "job-locks");
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, $"{jobId:N}.lock");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // The OS releases this lock when a worker dies. Keep the lock file itself: unlinking
                // it could let a third process lock a new inode while the next owner holds the old one.
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33 or 35)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
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
        }
        catch
        {
            // Cleanup failure is non-fatal
        }

        return ValueTask.CompletedTask;
    }
}
