using Romd.Application.Common.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using Romd.Persistence.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Romd.Infrastructure.Identity;

/// <summary>Reconciles durable admin registrations on the worker, including after a restart.</summary>
public sealed class ConsumerWebAppProvisioningWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<ConsumerWebAppProvisioningWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        do
        {
            try
            {
                await ProvisionPendingAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "Could not provision consumer web app registrations. Retrying on the next pass.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProvisionPendingAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registrations = scope.ServiceProvider.GetRequiredService<ConsumerWebAppRegistrationStore>();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        foreach (var registration in await registrations.ListPendingAsync(ct))
        {
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = registration.ClientId,
                ClientType = ClientTypes.Public,
                ApplicationType = ApplicationTypes.Web,
                ConsentType = ConsentTypes.Implicit,
                DisplayName = registration.DisplayName,
                RedirectUris = { new Uri(registration.CallbackUri, UriKind.Absolute) },
                Permissions =
                {
                    Permissions.Endpoints.Authorization,
                    Permissions.Endpoints.Token,
                    Permissions.Endpoints.Revocation,
                    Permissions.GrantTypes.AuthorizationCode,
                    Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code
                },
                Requirements = { Requirements.Features.ProofKeyForCodeExchange }
            };
            descriptor.Permissions.UnionWith(ConsumerGrantScopes.All.Select(scope => Permissions.Prefixes.Scope + scope));
            var existing = await applications.FindByClientIdAsync(registration.ClientId, ct);
            if (existing is null)
                await applications.CreateAsync(descriptor, ct);
            else
                await applications.UpdateAsync(existing, descriptor, ct);

            // Disabling a pending row prevents this compare-and-set from making it active.
            await registrations.MarkProvisionedAsync(registration.ClientId, clock.GetUtcNow(), ct);
        }
    }
}
