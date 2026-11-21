namespace Romd.Contracts.Management.Libraries;

/// <summary>One title's eligibility under saved rules and visibility in the published library.</summary>
public sealed record LibraryTitleVisibility(
    string TitleId,
    string TitleName,
    bool HasPublication,
    bool IsPublished,
    bool NeedsPublish,
    bool IsEligible,
    bool IsOwned,
    string? Reason,
    string? RatingBoard,
    string? RatingCategory,
    int? RatingAge);
