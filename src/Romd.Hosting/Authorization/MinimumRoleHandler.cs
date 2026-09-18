using Microsoft.AspNetCore.Authorization;
using Romd.Domain.Identity;

namespace Romd.Host.Authorization;

/// <summary>
///     Handler for minimum role requirements.
/// </summary>
public sealed class MinimumRoleHandler : AuthorizationHandler<MinimumRoleRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        MinimumRoleRequirement requirement)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return Task.CompletedTask;
        }

        // Check roles in descending order of privilege
        var userRole = GetHighestRole(context);

        if (userRole >= requirement.MinimumRole)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private static RomdRoleType GetHighestRole(AuthorizationHandlerContext context)
    {
        if (context.User.IsInRole(RomdRoleType.Admin.ToString()))
            return RomdRoleType.Admin;

        if (context.User.IsInRole(RomdRoleType.Manager.ToString()))
            return RomdRoleType.Manager;

        if (context.User.IsInRole(RomdRoleType.Contributor.ToString()))
            return RomdRoleType.Contributor;

        // Default role for authenticated users
        return RomdRoleType.User;
    }
}
