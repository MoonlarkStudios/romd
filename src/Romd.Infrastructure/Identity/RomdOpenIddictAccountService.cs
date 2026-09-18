using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using Romd.Domain.Identity;
using Romd.Persistence.Identity;
using Romd.Persistence;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Romd.Infrastructure.Identity;

public sealed class RomdOpenIddictAccountService(
    RomdDbContext dbContext,
    IPasswordHasher<RomdUser> passwordHasher,
    IOptions<IdentityOptions> identityOptions)
{
    /// <summary>
    ///     Validates credentials and returns the user, or null if invalid or not allowed to sign in.
    ///     Used by the interactive login and device verification endpoints.
    /// </summary>
    public async Task<RomdUser?> ValidateCredentialsAsync(
        string login,
        string password,
        CancellationToken ct = default)
    {
        string normalizedLogin = Normalize(login);
        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate =>
                    candidate.NormalizedUserName == normalizedLogin
                    || candidate.NormalizedEmail == normalizedLogin,
                ct);
        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
        {
            return null;
        }

        var result = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed || !CanSignIn(user) || IsLockedOut(user))
        {
            return null;
        }

        var signedInAt = DateTimeOffset.UtcNow;
        await dbContext.Users.Where(candidate => candidate.Id == user.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.LastSignedInAt, signedInAt), ct);
        user.LastSignedInAt = signedInAt;
        return user;
    }

    /// <summary>
    ///     Builds the OpenIddict principal for an already-identified user, enforcing per-client
    ///     eligibility and stamping the host surface audience as the token resource. Returns null if
    ///     the user no longer exists, cannot sign in, or is not eligible for the client.
    /// </summary>
    public async Task<ClaimsPrincipal?> CreatePrincipalAsync(
        string userId,
        IEnumerable<string> scopes,
        string audience,
        string clientId,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(userId, out var id))
        {
            return null;
        }

        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, ct);
        if (user is null || !CanSignIn(user) || IsLockedOut(user))
        {
            return null;
        }

        return await CreatePrincipalAsync(user, scopes, audience, clientId, ct);
    }

    /// <summary>
    ///     Builds the OpenIddict principal for a validated user. Applies per-client eligibility and
    ///     sets the token resource to the host surface audience. Returns null if not eligible.
    /// </summary>
    public async Task<ClaimsPrincipal?> CreatePrincipalAsync(
        RomdUser user,
        IEnumerable<string> scopes,
        string audience,
        string clientId,
        CancellationToken ct = default)
    {
        var roles = await GetUserRolesAsync(user.Id, ct);
        if (!IsEligibleForClient(roles, clientId))
        {
            return null;
        }

        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity
            .SetClaim(Claims.Subject, user.Id.ToString())
            .SetClaim("romd_account_stamp", user.SecurityStamp)
            .SetClaim(Claims.Email, user.Email ?? string.Empty)
            .SetClaim(Claims.Name, user.UserName ?? string.Empty)
            .SetClaim(Claims.PreferredUsername, user.UserName ?? string.Empty)
            .SetClaims(Claims.Role, roles.ToImmutableArray());

        if (user.LibraryId.HasValue)
        {
            identity.SetClaim("LibraryId", user.LibraryId.Value.ToString());
        }

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        principal.SetResources(audience);
        principal.SetDestinations(GetDestinations);

        return principal;
    }

    public async Task<bool> IsCurrentAccountStampAsync(Guid id, string? stamp, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(stamp)) return false;
        return await dbContext.Users.AsNoTracking().AnyAsync(user => user.Id == id &&
            !user.IsSuspended && !user.RequiresActivation && user.SecurityStamp == stamp, ct);
    }

    // The admin SPA client only admits staff (Contributor or higher); the consumer SPA and console
    // admit any authenticated user (staff may use consumer surfaces, a one-way crossover).
    private static bool IsEligibleForClient(IReadOnlyList<string> roles, string clientId)
    {
        if (clientId != RomdOpenIddictClients.AdminSpa)
        {
            return true;
        }

        return roles.Any(role =>
            role == nameof(RomdRoleType.Contributor)
            || role == nameof(RomdRoleType.Manager)
            || role == nameof(RomdRoleType.Admin));
    }

    private async Task<IReadOnlyList<string>> GetUserRolesAsync(Guid userId, CancellationToken ct)
    {
        var roleIds = dbContext.Set<IdentityUserRole<Guid>>()
            .AsNoTracking()
            .Where(userRole => userRole.UserId == userId)
            .Select(userRole => userRole.RoleId);

        return await dbContext.Set<RomdIdentityRole>()
            .AsNoTracking()
            .Where(role => roleIds.Contains(role.Id))
            .Select(role => role.Name ?? string.Empty)
            .Where(role => role.Length > 0)
            .ToListAsync(ct);
    }

    private bool CanSignIn(RomdUser user)
    {
        if (user.IsSuspended || user.RequiresActivation) return false;
        var signInOptions = identityOptions.Value.SignIn;

        if (signInOptions.RequireConfirmedEmail && !user.EmailConfirmed)
        {
            return false;
        }

        if (signInOptions.RequireConfirmedPhoneNumber && !user.PhoneNumberConfirmed)
        {
            return false;
        }

        return !signInOptions.RequireConfirmedAccount || user.EmailConfirmed || user.PhoneNumberConfirmed;
    }

    private static bool IsLockedOut(RomdUser user) =>
        user.LockoutEnabled && user.LockoutEnd is { } lockoutEnd && lockoutEnd > DateTimeOffset.UtcNow;

    private static IEnumerable<string> GetDestinations(Claim claim)
    {
        switch (claim.Type)
        {
            case Claims.Subject:
                yield return Destinations.AccessToken;
                yield return Destinations.IdentityToken;
                yield break;

            case Claims.Name or Claims.PreferredUsername:
                yield return Destinations.AccessToken;
                if (claim.Subject!.HasScope(Scopes.Profile))
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;

            case Claims.Email:
                yield return Destinations.AccessToken;
                if (claim.Subject!.HasScope(Scopes.Email))
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;

            case Claims.Role:
                yield return Destinations.AccessToken;
                if (claim.Subject!.HasScope(Scopes.Roles))
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}
