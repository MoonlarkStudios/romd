namespace Romd.Contracts.Management.Auth;

/// <summary>
///     Information about the currently authenticated user.
/// </summary>
public sealed record CurrentUserResponse(
    Guid Id,
    string Email,
    IReadOnlyList<string> Roles,
    string? LibraryId);
