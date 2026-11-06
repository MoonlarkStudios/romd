using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;

namespace Romd.Infrastructure.Identity;

/// <summary>
///     Implementation of ICurrentUser using HttpContext and Identity claims.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private ClaimsPrincipal? User => httpContextAccessor.HttpContext?.User;

    public Guid? UserId
    {
        get
        {
            string? userIdClaim =
                User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
        }
    }

    public string? Email =>
        User?.FindFirst(ClaimTypes.Email)?.Value
        ?? User?.FindFirst(JwtRegisteredClaimNames.Email)?.Value
        ?? User?.FindFirst("email")?.Value;

    public string? UserName =>
        User?.FindFirst("preferred_username")?.Value
        ?? User?.FindFirst(ClaimTypes.Name)?.Value
        ?? User?.FindFirst("name")?.Value;

    public IReadOnlyList<string> Roles =>
        User is null
            ? []
            : User.FindAll(ClaimTypes.Role)
                .Concat(User.FindAll("role"))
                .Select(claim => claim.Value)
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.Ordinal)
                .ToList();

    public RomdRoleType Role
    {
        get
        {
            if (User == null || !IsAuthenticated)
            {
                return RomdRoleType.User;
            }

            // Check roles in descending order of privilege
            if (User.IsInRole(RomdRoleType.Admin.ToString()))
            {
                return RomdRoleType.Admin;
            }

            if (User.IsInRole(RomdRoleType.Manager.ToString()))
            {
                return RomdRoleType.Manager;
            }

            if (User.IsInRole(RomdRoleType.Contributor.ToString()))
            {
                return RomdRoleType.Contributor;
            }

            return RomdRoleType.User;
        }
    }

    public int? LibraryId
    {
        get
        {
            string? libraryClaim = User?.FindFirst("LibraryId")?.Value;
            return int.TryParse(libraryClaim, out int libraryId) ? libraryId : null;
        }
    }

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated ?? false;

    public bool HasRole(RomdRoleType minimumRole) => Role >= minimumRole;
}
