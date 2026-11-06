using Microsoft.AspNetCore.Identity;

namespace Romd.Persistence.Identity;

/// <summary>
///     Application user entity extending ASP.NET Core Identity.
/// </summary>
public sealed class RomdUser : IdentityUser<Guid>
{
    /// <summary>
    ///     Optional Library that restricts content access.
    ///     Null means the user has no consumer catalog scope.
    /// </summary>
    public int? LibraryId { get; set; }
    public bool IsSuspended { get; set; }
    public bool RequiresActivation { get; set; }
    public DateTimeOffset? LastSignedInAt { get; set; }

    /// <summary>
    ///     When the user was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    ///     When the user was last updated.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    ///     Creates a new user with default values.
    /// </summary>
    public static RomdUser Create(string userName, string email, TimeProvider? timeProvider = null)
    {
        return new RomdUser
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            Email = email,
            EmailConfirmed = true,
            CreatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow()
        };
    }
}
