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
    private static readonly TimeSpan _retryDelay = TimeSpan.FromMinutes(1);

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
        IReadOnlyList<ConsumerWebAppRegistration> pending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var registrations = scope.ServiceProvider.GetRequiredService<ConsumerWebAppRegistrationStore>();
            pending = await registrations.ListPendingAsync(clock.GetUtcNow(), ct);
        }

        foreach (var registration in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ProvisionAsync(registration, ct);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogError(error, "Could not provision consumer web app {ClientId}.", registration.ClientId);
                await ScheduleRetryAsync(registration.ClientId, ct);
            }
        }
    }

    private async Task ProvisionAsync(ConsumerWebAppRegistration registration, CancellationToken ct)
    {
        if (!ConsumerWebAppRegistration.IsClientId(registration.ClientId) ||
            !ConsumerWebAppRegistration.IsValidDisplayName(registration.DisplayName) ||
            !ConsumerWebAppRegistration.TryReadCallback(registration.CallbackUri, out var callback) ||
            !ConsumerWebAppRegistration.IsValidBrowserOrigin(registration.BrowserOrigin))
            throw new InvalidOperationException("The consumer web app registration contains invalid details.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var registrations = scope.ServiceProvider.GetRequiredService<ConsumerWebAppRegistrationStore>();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = registration.ClientId,
            ClientType = ClientTypes.Public,
            ApplicationType = ApplicationTypes.Web,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = registration.DisplayName,
            RedirectUris = { callback },
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
        descriptor.Permissions.UnionWith(ConsumerGrantScopes.ThirdParty.Select(scope => Permissions.Prefixes.Scope + scope));
        var existing = await applications.FindByClientIdAsync(registration.ClientId, ct);
        if (existing is null)
            await applications.CreateAsync(descriptor, ct);
        else
            await applications.UpdateAsync(existing, descriptor, ct);

        // The conditional update cannot publish a disabled registration.
        await registrations.MarkProvisionedAsync(registration.ClientId, clock.GetUtcNow(), ct);
    }

    private async Task ScheduleRetryAsync(string clientId, CancellationToken ct)
    {
        try
        {
            // OpenIddict writes can leave a failed entity tracked in the provisioning scope.
            await using var scope = scopeFactory.CreateAsyncScope();
            var registrations = scope.ServiceProvider.GetRequiredService<ConsumerWebAppRegistrationStore>();
            await registrations.ScheduleProvisioningRetryAsync(clientId, clock.GetUtcNow() + _retryDelay, ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            logger.LogError(error, "Could not record the provisioning retry for consumer web app {ClientId}.", clientId);
        }
    }
}
