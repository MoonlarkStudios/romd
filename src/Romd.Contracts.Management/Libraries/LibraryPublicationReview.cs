namespace Romd.Contracts.Management.Libraries;

public sealed record LibraryPublicationReview(
    Guid Revision,
    DateTimeOffset CheckedAt,
    DateTimeOffset? PublishedAt,
    int PublishedGames,
    int ProjectedGames,
    int AssignedAccounts,
    int AddedGames,
    int RemovedGames,
    IReadOnlyList<LibraryPublicationGame> AddedExamples,
    IReadOnlyList<LibraryPublicationGame> RemovedExamples,
    int ShelvesEmptied,
    IReadOnlyList<string> ShelvesEmptiedExamples);

public sealed record LibraryPublicationGame(string Id, string Name, string? Reason);

public sealed record LibraryPublicationAttempt(
    Guid JobId, string Phase, bool IsTerminal, string? Outcome,
    DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, int IncludedCount, int ExcludedCount);

public sealed record LibraryPublicationStatus(LibraryPublicationAttempt? LatestAttempt);
