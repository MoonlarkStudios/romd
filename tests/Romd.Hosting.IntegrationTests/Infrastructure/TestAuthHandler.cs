using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Domain.Identity;
using Romd.Host.Authorization;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

/// <summary>
///     Test-only authentication that injects a principal from request headers, so endpoint tests can
///     assert authorization without minting real tokens. It returns NoResult when its header is
///     absent, letting real OpenIddict-token requests fall through to the genuine validation scheme.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserIdHeader = "X-Test-UserId";
    public const string EmailHeader = "X-Test-Email";
    public const string RolesHeader = "X-Test-Roles";
    public const string LibraryIdHeader = "X-Test-LibraryId";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserIdHeader, out var userId) || string.IsNullOrEmpty(userId))
        {
            // Not a test-authenticated request; let the real schemes (OpenIddict) handle it.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };

        if (Request.Headers.TryGetValue(EmailHeader, out var email) && !string.IsNullOrEmpty(email))
        {
            claims.Add(new Claim(ClaimTypes.Email, email.ToString()));
        }

        if (Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            foreach (var role in roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        if (Request.Headers.TryGetValue(LibraryIdHeader, out var libraryId) && !string.IsNullOrEmpty(libraryId))
        {
            claims.Add(new Claim("LibraryId", libraryId.ToString()));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public static class TestAuthenticationExtensions
{
    /// <summary>
    ///     Registers the test scheme and re-registers the Romd authorization policies so they accept
    ///     the test scheme alongside the production schemes. Call from ConfigureTestServices.
    /// </summary>
    public static IServiceCollection AddTestAuthentication(
        this IServiceCollection services,
        params string[] productionSchemes)
    {
        services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

        string[] schemes = [.. productionSchemes, TestAuthHandler.SchemeName];

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthorizationPolicies.RequireUser, p => Configure(p, RomdRoleType.User, schemes));
            options.AddPolicy(AuthorizationPolicies.RequireContributor, p => Configure(p, RomdRoleType.Contributor, schemes));
            options.AddPolicy(AuthorizationPolicies.RequireManager, p => Configure(p, RomdRoleType.Manager, schemes));
            options.AddPolicy(AuthorizationPolicies.RequireAdmin, p => Configure(p, RomdRoleType.Admin, schemes));
        });

        return services;
    }

    /// <summary>Adds the test-user headers a request needs to authenticate via the test scheme.</summary>
    public static HttpClient WithTestUser(
        this HttpClient client,
        Guid userId,
        string? email = null,
        IEnumerable<RomdRoleType>? roles = null,
        int? libraryId = null)
    {
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId.ToString());
        if (email is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, email);
        }

        if (roles is not null)
        {
            client.DefaultRequestHeaders.Add(
                TestAuthHandler.RolesHeader,
                string.Join(',', roles.Select(role => role.ToString())));
        }

        if (libraryId is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.LibraryIdHeader, libraryId.Value.ToString());
        }

        return client;
    }

    private static void Configure(AuthorizationPolicyBuilder policy, RomdRoleType minimumRole, string[] schemes)
    {
        policy.AddAuthenticationSchemes(schemes);
        policy.AddRequirements(new MinimumRoleRequirement(minimumRole));
    }
}
