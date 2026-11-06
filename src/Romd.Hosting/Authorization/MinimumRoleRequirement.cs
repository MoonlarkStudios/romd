using Microsoft.AspNetCore.Authorization;
using Romd.Domain.Identity;

namespace Romd.Host.Authorization;

/// <summary>
///     Requirement for minimum role authorization.
/// </summary>
public sealed class MinimumRoleRequirement : IAuthorizationRequirement
{
    public MinimumRoleRequirement(RomdRoleType minimumRole)
    {
        MinimumRole = minimumRole;
    }

    public RomdRoleType MinimumRole { get; }
}
