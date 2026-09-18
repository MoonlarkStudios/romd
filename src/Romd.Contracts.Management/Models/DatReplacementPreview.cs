namespace Romd.Contracts.Management.Models;

public sealed record DatEntryChange(string Name, string Change, int FilesAdded, int FilesRemoved, int FilesChanged);
public sealed record DatReplacementPreview(
    string ActiveSha256, string CandidateSha256, string? ActiveVersion, string? CandidateVersion,
    bool Unchanged, int ActiveEntries, int CandidateEntries, int EntriesAdded, int EntriesRemoved,
    int EntriesChanged, int FilesAdded, int FilesRemoved, int FilesChanged, int HashesChanged,
    int ActiveBiosEntries, int CandidateBiosEntries, IReadOnlyList<DatEntryChange> Changes, bool Truncated, int ActiveFiles = 0, int CandidateFiles = 0);
