using Microsoft.Extensions.Hosting;
using Romd.Infrastructure.Storage;

namespace Romd.Infrastructure.Readiness;

/// <summary>Validates the packaged child before worker dispatch and readiness.</summary>
public sealed class MediaWorkerStartupCheck(MediaWorkerStartupValidator validator) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var result = await validator.ValidateAsync(cancellationToken);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"The dedicated media child failed startup validation ({result.Failure}).");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
