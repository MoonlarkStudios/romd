namespace Romd.Hosting;

/// <summary>
/// Owns a dedicated ASPNETCORE_TEMP directory for the lifetime of the admin host.
/// The lock file is deliberately permanent: unlinking it would let two processes lock different inodes.
/// </summary>
public sealed class RequestBufferDirectoryLease : IHostedLifecycleService, IDisposable
{
    private readonly string? _directory;
    private readonly ILogger<RequestBufferDirectoryLease> _logger;
    private FileStream? _lease;

    public RequestBufferDirectoryLease(string? directory, ILogger<RequestBufferDirectoryLease> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_directory))
            return Task.CompletedTask;

        Directory.CreateDirectory(_directory);
        try
        {
            _lease = new FileStream(Path.Combine(_directory, ".romd-request-buffers.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"Cannot exclusively own ASPNETCORE_TEMP '{_directory}'. Use a dedicated directory per admin process; stop the previous owner before restarting.",
                exception);
        }

        try
        {
            int removed = 0;
            foreach (string path in Directory.EnumerateFiles(_directory, "ASPNETCORE_*.tmp"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Do not follow links or touch directories/other applications' files.
                if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    continue;
                File.Delete(path);
                removed++;
            }
            _logger.LogInformation("Removed {Count} abandoned multipart request buffers from {Directory}", removed, _directory);
        }
        catch
        {
            Dispose();
            throw;
        }

        return Task.CompletedTask;
    }

    // Keep ownership throughout server shutdown, including any outstanding request disposal.
    // DI disposes this service only after hosted services (including the HTTP server) have stopped.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        _lease?.Dispose();
        _lease = null;
    }
}
