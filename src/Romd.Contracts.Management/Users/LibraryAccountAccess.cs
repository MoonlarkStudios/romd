namespace Romd.Contracts.Management.Users;

public sealed record LibraryAccountAccess(
    string State, string? LibraryId, string? LibraryName, DateTimeOffset? PublishedAt,
    int? OwnedGames, bool? SelectedGameVisible);
