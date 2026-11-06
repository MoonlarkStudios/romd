using Microsoft.Extensions.Hosting;
using Romd.Application.Common.Configuration;

namespace Romd.Infrastructure.Identity;

/// <summary>
///     Worker startup initializer that creates the OpenIddict signing key during first-run init.
///     API hosts also create it on demand (atomically), so startup ordering does not matter.
/// </summary>
public sealed class OpenIddictSigningKeyInitializer(IRomdOptions options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        OpenIddictSigningKey.EnsureCreated(options.DataDirectory);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
