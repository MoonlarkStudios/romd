using Romd.Domain.Identity;

namespace Romd.Admin.Application.Users;

internal static class UserRoleParser
{
    private static readonly RomdRoleType[] ValidRoles =
    [
        RomdRoleType.User,
        RomdRoleType.Contributor,
        RomdRoleType.Manager,
        RomdRoleType.Admin
    ];

    public static bool TryParse(string? value, out RomdRoleType role)
    {
        foreach (var candidate in ValidRoles)
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                role = candidate;
                return true;
            }
        }

        role = default;
        return false;
    }
}
