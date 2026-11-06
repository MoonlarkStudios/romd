namespace Romd.Domain.Identity;

/// <summary>
///     Defines the role hierarchy for Romd users.
///     Higher values indicate more permissions.
/// </summary>
public enum RomdRoleType
{
    /// <summary>
    ///     Read-only access, filtered by Library View.
    /// </summary>
    User = 0,

    /// <summary>
    ///     Can upload ROMs (only matching existing DAT entries).
    /// </summary>
    Contributor = 1,

    /// <summary>
    ///     Can modify content (DATs, platforms, titles).
    /// </summary>
    Manager = 2,

    /// <summary>
    ///     Full access including user management.
    /// </summary>
    Admin = 3
}
