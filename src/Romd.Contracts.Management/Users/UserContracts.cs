namespace Romd.Contracts.Management.Users;

/// <summary>
///     User information.
/// </summary>
public sealed record UserDto(
    Guid Id,
    string Email,
    IReadOnlyList<string> Roles,
    string? LibraryId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    bool IsSuspended = false,
    bool RequiresActivation = false,
    DateTimeOffset? LastSignedInAt = null);

/// <summary>
///     Request to create a new user.
/// </summary>
public sealed record CreateUserRequest(
    string Email,
    string Password,
    string Role,
    string? LibraryId = null,
    bool RequiresActivation = false);

/// <summary>
///     Request to update a user.
/// </summary>
public sealed record UpdateUserRequest(
    string? Email,
    string? NewPassword);

/// <summary>
///     Request to assign a role to a user.
/// </summary>
public sealed record AssignRoleRequest(string Role);

/// <summary>
///     Request to assign a library to a user.
/// </summary>
public sealed record AssignLibraryRequest(string? LibraryId);
