using Microsoft.Extensions.Hosting;
using Romd.Infrastructure.Storage;

namespace Romd.Infrastructure.Readiness;

/// <summary>Completes asset verification before the host accepts CHD work or reads.</summary>
public sealed class ChdAssetsStartupCheck : IHostedService
{
    private readonly ChdDeploymentRuntime _runtime;

    public ChdAssetsStartupCheck(ChdDeploymentRuntime runtime) => _runtime = runtime;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _runtime.Verify(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _runtime.Invalidate();
        return Task.CompletedTask;
    }
}
