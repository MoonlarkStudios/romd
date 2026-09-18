using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Romd.Infrastructure.Identity;

/// <summary>
///     Worker-owned seeder for the OpenIddict clients: the console (device flow) and the admin and
///     consumer SPAs (authorization code + PKCE). Reconciles each client on every run so permission
///     and redirect-URI changes are picked up.
/// </summary>
public sealed class RomdOpenIddictApplicationSeeder(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration)
    : IHostedService
{
    public const string ConsoleClientId = RomdOpenIddictClients.Console;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var applicationManager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        foreach (var descriptor in BuildDescriptors())
        {
            var existing = await applicationManager.FindByClientIdAsync(descriptor.ClientId!, cancellationToken);
            if (existing is null)
            {
                await applicationManager.CreateAsync(descriptor, cancellationToken);
            }
            else
            {
                await applicationManager.UpdateAsync(existing, descriptor, cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private IEnumerable<OpenIddictApplicationDescriptor> BuildDescriptors()
    {
        yield return BuildConsoleDescriptor();
        yield return BuildSpaDescriptor(
            RomdOpenIddictClients.AdminSpa,
            "ROMD Admin",
            "Romd:Auth:AdminSpa",
            defaultOrigin: "http://localhost:5137");
        yield return BuildSpaDescriptor(
            RomdOpenIddictClients.ConsumerSpa,
            "ROMD",
            "Romd:Auth:ConsumerSpa",
            defaultOrigin: "http://localhost:5174");
    }

    private static OpenIddictApplicationDescriptor BuildConsoleDescriptor() =>
        new()
        {
            ClientId = RomdOpenIddictClients.Console,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Explicit,
            DisplayName = "ROMD Console",
            Permissions =
            {
                Permissions.Endpoints.DeviceAuthorization,
                Permissions.Endpoints.Revocation,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.DeviceCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.Scopes.Email,
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                Permissions.Scopes.Profile,
                Permissions.Scopes.Roles
            }
        };

    private OpenIddictApplicationDescriptor BuildSpaDescriptor(
        string clientId,
        string displayName,
        string configSection,
        string defaultOrigin)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = displayName,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.EndSession,
                Permissions.Endpoints.Revocation,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + Scopes.OpenId,
                Permissions.Scopes.Email,
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                Permissions.Scopes.Profile,
                Permissions.Scopes.Roles
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
        };

        foreach (var uri in ResolveUris($"{configSection}:RedirectUris", $"{defaultOrigin}/auth/callback"))
        {
            descriptor.RedirectUris.Add(uri);
        }

        foreach (var uri in ResolveUris($"{configSection}:PostLogoutRedirectUris", defaultOrigin))
        {
            descriptor.PostLogoutRedirectUris.Add(uri);
        }

        return descriptor;
    }

    private IEnumerable<Uri> ResolveUris(string configKey, string fallback)
    {
        var configured = configuration.GetSection(configKey).Get<string[]>();
        var values = configured is { Length: > 0 } ? configured : [fallback];

        return values.Select(value => new Uri(value, UriKind.Absolute));
    }
}
