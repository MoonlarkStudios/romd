namespace Romd.Admin.Application.Titles;

public interface IDatTitleGroupingReader
{
    Task<DatTitleGroupingRead?> ReadAsync(int cloneTitleId, CancellationToken cancellationToken);
}

public sealed record DatTitleGroupingRead(DatTitleGroupingEvidence? Suggestion);

public sealed record DatTitleGroupingEvidence(
    int ParentTitleId, string ParentTitleName, Guid ParentTitleRevision, Guid CloneTitleRevision, string DatName, string? DatVersion,
    IReadOnlyList<string> ReleaseNames, string Fingerprint);
