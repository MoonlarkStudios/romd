using Microsoft.AspNetCore.Identity;

namespace Romd.Persistence.Identity;

/// <summary>
///     Application role entity extending ASP.NET Core Identity.
/// </summary>
public sealed class RomdIdentityRole : IdentityRole<Guid>
{
    /// <summary>
    ///     EF Core constructor.
    /// </summary>
    public RomdIdentityRole()
    {
    }

    /// <summary>
    ///     Creates a new role with the specified name.
    /// </summary>
    public RomdIdentityRole(string roleName)
        : base(roleName)
    {
        Id = Guid.NewGuid();
        NormalizedName = roleName.ToUpperInvariant();
    }
}
