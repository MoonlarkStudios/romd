using Romd.Contracts.Common.Models;
namespace Romd.Contracts.Management.Libraries;

public sealed record LibraryEvaluationRequest(
    LibraryConfigurationDto Configuration,
    string View = "matching",
    string? Query = null,
    string? Cursor = null, int Limit = 48);

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
    Page<LibraryEvaluationTitleDto> Page);
