using Romd.Application.Common.Readiness;
using Romd.Infrastructure.Storage;

namespace Romd.Infrastructure.Readiness;

/// <summary>Configured image assets must have passed startup verification before serving content.</summary>
public sealed class ChdAssetsReadinessCheck(ChdDeploymentRuntime runtime) : IReadinessCheck
{
    public string Name => ReadinessCheckNames.NativeMedia;
    public bool IsCritical => true;

    public Task<ReadinessCheckStatus> EvaluateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(runtime.IsReady ? ReadinessCheckStatus.Healthy : ReadinessCheckStatus.Unready);
    }
}
