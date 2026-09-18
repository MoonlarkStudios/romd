using Microsoft.Extensions.Hosting;

namespace Romd.Infrastructure.Identity;

public sealed class ServerInstanceIdentityInitializer(ServerInstanceIdentity identity) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        identity.Initialize();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
