namespace Romd.Contracts.Management.Libraries;

public sealed record LibraryCollection(
    string Id,
    string Name,
    int MatchingCount,
    int TotalCount);
