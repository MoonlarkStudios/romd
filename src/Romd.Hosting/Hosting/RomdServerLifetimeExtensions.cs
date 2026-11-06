namespace Romd.Hosting;

public static class RomdServerLifetimeExtensions
{
    /// <summary>Run a server, treating a requested stop before startup completes as orderly cancellation.</summary>
    public static async Task RunRomdServerAsync(this IHost host)
    {
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        // Capture before disposal; a stopped startup is distinct from failure
        // during shutdown or disposal of a host that never finished starting.
        var stopping = lifetime.ApplicationStopping;
        var started = lifetime.ApplicationStarted;
        try
        {
            try
            {
                await host.StartAsync();
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested && !started.IsCancellationRequested)
            {
                return;
            }
            await host.WaitForShutdownAsync();
        }
        finally
        {
            // Match the framework's RunAsync ownership. Disposal is outside the
            // startup cancellation filter so an independent disposal error is fatal.
            if (host is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else
                host.Dispose();
        }
    }
}
