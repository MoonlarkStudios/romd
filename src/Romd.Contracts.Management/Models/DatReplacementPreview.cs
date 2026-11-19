namespace Romd.Contracts.Management.Models;

public sealed record DatEntryChange(string Id, string Name, string Change, int FilesAdded, int FilesRemoved, int FilesChanged, DatEntryReviewImpact? Impact = null);
public sealed record DatReplacementPreview(
    string ActiveSha256, string CandidateSha256, string? ActiveVersion, string? CandidateVersion,
    bool Unchanged, int ActiveEntries, int CandidateEntries, int EntriesAdded, int EntriesRemoved,
    int EntriesChanged, int FilesAdded, int FilesRemoved, int FilesChanged, int HashesChanged,
    int ActiveBiosEntries, int CandidateBiosEntries, IReadOnlyList<DatEntryChange> Changes, bool Truncated, int ActiveFiles = 0, int CandidateFiles = 0,
    DatReviewImpact? Impact = null);

public sealed record DatReviewImpact(
    int NeedsReviewEntries, int RemovedWithStoredRoms, int RemovedWithTitleLinks,
    int LostRomMatches, int NewRomMatches, int Sha1ChangedEntries, int Sha1ChangedFiles,
    int MetadataOnlyEntries, int OtherChecksumOnlyEntries, int DiskChangedEntries);

public sealed record DatEntryReviewImpact(
    int StoredRoms, int LostRomMatches, int NewRomMatches, string? TitleLink, bool TitleLinkAtRisk,
    int Sha1ChangedFiles, bool MetadataOnly, bool OtherChecksumOnly, bool NeedsReview,
    string? TitleId, int TitleOtherSourceLinks);

public sealed record DatFileReviewImpact(bool StoredBefore, bool StoredAfter, bool LosesCatalogMatch, bool GainsCatalogMatch);
