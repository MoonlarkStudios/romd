namespace Romd.Hosting;

/// <summary>
///     Writes a readiness marker file once the host's start-up hosted services have run, and removes it on
///     graceful shutdown. Registered last in the worker, the marker therefore appears only after migrations
///     and seeders complete, so the API hosts can gate on <c>depends_on: service_healthy</c> against a ready
///     schema. The marker path must be a container-local, ephemeral location (e.g. a tmpfs-mounted
///     <c>/tmp</c>) so that a crash + restart re-derives readiness rather than reporting a stale healthy state.
/// </summary>
internal sealed class RomdReadinessSignal(string markerPath, ILogger<RomdReadinessSignal> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"));
        logger.LogInformation("Readiness marker written to {MarkerPath}", markerPath);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            File.Delete(markerPath);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to remove readiness marker {MarkerPath}", markerPath);
        }

        return Task.CompletedTask;
    }
}

public static class RomdReadinessSignalExtensions
{
    public const string MarkerPathConfigurationKey = "Romd:ReadinessMarkerPath";

    /// <summary>
    ///     Registers the readiness marker writer when <c>Romd:ReadinessMarkerPath</c> is configured; otherwise
    ///     a no-op (so direct, non-containerized hosting and tests are unaffected). Register this <em>last</em>
    ///     so its start-up runs after database initialization and seeding.
    /// </summary>
    public static IServiceCollection AddRomdReadinessSignal(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string? markerPath = configuration[MarkerPathConfigurationKey];
        if (string.IsNullOrWhiteSpace(markerPath))
        {
            return services;
        }

        services.AddHostedService(provider =>
            new RomdReadinessSignal(markerPath, provider.GetRequiredService<ILogger<RomdReadinessSignal>>()));

        return services;
    }
}
