namespace Romd.Contracts.Management.Libraries;

public sealed record LibraryCollectionDto(
    string Id,
    string Name,
    int MatchingCount,
    int TotalCount);