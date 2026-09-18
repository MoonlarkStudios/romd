namespace Romd.Host.Authorization;

/// <summary>
///     Policy names for role-based authorization.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>
    ///     Requires the User role or higher.
    /// </summary>
    public const string RequireUser = "RequireUser";

    /// <summary>
    ///     Requires the Contributor role or higher.
    /// </summary>
    public const string RequireContributor = "RequireContributor";

    /// <summary>
    ///     Requires the Manager role or higher.
    /// </summary>
    public const string RequireManager = "RequireManager";

    /// <summary>
    ///     Requires the Admin role.
    /// </summary>
    public const string RequireAdmin = "RequireAdmin";
}
