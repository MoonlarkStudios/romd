namespace Romd.Admin.Application.Users;

public sealed record ManagedUser(
    Guid Id,
    string Email,
    IReadOnlyList<string> Roles,
    int? LibraryId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    bool IsSuspended = false,
    bool RequiresActivation = false,
    DateTimeOffset? LastSignedInAt = null);
