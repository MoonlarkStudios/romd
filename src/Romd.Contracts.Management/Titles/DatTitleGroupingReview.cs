namespace Romd.Contracts.Management.Titles;

public sealed record DatTitleGroupingReview(DatTitleGroupingSuggestion? Suggestion);

public sealed record DatTitleGroupingSuggestion(
    string ParentTitleId, string ParentTitleName, string ParentTitleRevision, string CloneTitleRevision, string DatName, string? DatVersion,
    IReadOnlyList<string> ReleaseNames, string Evidence);
