using Romd.Contracts.Common.Models;
namespace Romd.Contracts.Management.Libraries;

public sealed record LibraryEvaluationRequest(
    LibraryConfiguration Configuration,
    string View = "matching",
    string? Query = null,
    string? Cursor = null, int Limit = 48);

public sealed record LibraryEvaluationTitle(
    string Id, string Name, string PlatformName, string? CoverUrl,
    bool WasEligible, bool IsEligible, bool IsOwned, bool IsPlayable,
    string? Reason, string? RatingBoard, string? RatingCategory, int? RatingAge,
    int AttachedCollectionCount);

public sealed record LibraryEvaluationReason(string Reason, int Count);

public sealed record LibraryEvaluation(
    DateTimeOffset EvaluatedAt,
    int SavedCount, int MatchingCount, int AddedCount, int RemovedCount,
    IReadOnlyList<LibraryEvaluationReason> RemovalReasons,
    Page<LibraryEvaluationTitle> Page);
