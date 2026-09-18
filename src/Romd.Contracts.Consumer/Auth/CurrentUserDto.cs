namespace Romd.Contracts.Consumer.Auth;

public sealed record CurrentUserDto(
    Guid Id,
    string Email,
    string Username,
    IReadOnlyList<string> Roles);
