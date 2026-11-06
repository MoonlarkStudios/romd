using Romd.Domain.Identity;

namespace Romd.Application.Common.Security;

/// <summary>
///     Provides information about the currently authenticated user.
/// </summary>
public interface ICurrentUser
{
    /// <summary>
    ///     The user's unique identifier.
    ///     Null if not authenticated.
    /// </summary>
    Guid? UserId { get; }

    /// <summary>
    ///     The user's email address.
    ///     Null if not authenticated or absent from the token.
    /// </summary>
    string? Email { get; }

    /// <summary>
    ///     The user's username (preferred_username / name claim).
    ///     Null if not authenticated or absent from the token.
    /// </summary>
    string? UserName { get; }

    /// <summary>
    ///     The user's assigned roles.
    /// </summary>
    IReadOnlyList<string> Roles { get; }

    /// <summary>
    ///     The user's highest role.
    /// </summary>
    RomdRoleType Role { get; }

    /// <summary>
    ///     The user's assigned Library ID.
    ///     Null means the user has no consumer catalog scope.
    /// </summary>
    int? LibraryId { get; }

    /// <summary>
    ///     Whether the user is authenticated.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    ///     Checks if the user has at least the specified role.
    /// </summary>
    bool HasRole(RomdRoleType minimumRole);
}
