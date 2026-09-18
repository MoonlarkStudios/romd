using Hangfire;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using Romd.Infrastructure.Jobs;

namespace Romd.Infrastructure.Identity;

/// <summary>
///     Removes expired and invalid OpenIddict tokens and authorizations so the storage tables do
///     not grow without bound. Runs on the worker, which owns recurring job execution.
/// </summary>
public sealed class RomdOpenIddictPruneJob(
    IOpenIddictTokenManager tokenManager,
    IOpenIddictAuthorizationManager authorizationManager,
    TimeProvider timeProvider,
    ILogger<RomdOpenIddictPruneJob> logger)
{
    // Keep a margin past the 30 day refresh token lifetime before pruning so in-flight and recently
    // expired entries are retained for replay detection.
    private static readonly TimeSpan Retention = TimeSpan.FromDays(45);

    [AutomaticRetry(Attempts = 1)]
    [Queue(JobQueues.Default)]
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        var threshold = timeProvider.GetUtcNow() - Retention;

        // Tokens reference authorizations, so prune tokens first to satisfy the foreign key.
        await tokenManager.PruneAsync(threshold, ct);
        await authorizationManager.PruneAsync(threshold, ct);

        logger.LogInformation(
            "Pruned OpenIddict tokens and authorizations created before {Threshold:o}",
            threshold);
    }
}
