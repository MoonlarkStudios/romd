namespace Romd.Contracts.Management.Libraries;

public sealed record LibraryEvaluationRequest(
    LibraryConfigurationDto Configuration,
    string View = "matching",
    string? Search = null,
    string? Cursor = null);

public sealed record LibraryEvaluationTitleDto(
    string Id, string Name, string PlatformName, string? CoverUrl,
    bool WasEligible, bool IsEligible, bool IsOwned, bool IsPlayable,
    string? Reason, string? RatingBoard, string? RatingCategory, int? RatingAge,
    int AttachedCollectionCount);

public sealed record LibraryEvaluationReasonDto(string Reason, int Count);

public sealed record LibraryEvaluationDto(
    DateTimeOffset EvaluatedAt,
    int SavedCount, int MatchingCount, int AddedCount, int RemovedCount,
    IReadOnlyList<LibraryEvaluationReasonDto> RemovalReasons,
    IReadOnlyList<LibraryEvaluationTitleDto> Items, string? NextCursor);
