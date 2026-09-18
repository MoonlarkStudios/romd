namespace Romd.Contracts.Management.Auth;

/// <summary>
///     Request to change the current user's password.
/// </summary>
public sealed record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);
