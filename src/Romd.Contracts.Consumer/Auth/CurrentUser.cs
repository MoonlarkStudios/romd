namespace Romd.Contracts.Consumer.Auth;

public sealed record CurrentUser(
    Guid Id,
    string Email,
    string Username,
    IReadOnlyList<string> Roles);
