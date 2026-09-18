namespace Romd.Contracts.Management.Models;

public sealed record DatChangeRequest(string ActiveSha256, string CandidateSha256,
    string? Search = null, string? Change = null, int Offset = 0, string? EntryName = null);
public sealed record DatFieldChange(string Field, string? Before, string? After);
public sealed record DatFileChange(string Name, string Kind, string Change, bool ChecksumsChanged,
    IReadOnlyList<DatFieldChange> Fields);
public sealed record DatChangePage(string ActiveSha256, string CandidateSha256, int Offset,
    int PageSize, int Total, IReadOnlyList<DatEntryChange> Entries, string? EntryName,
    IReadOnlyList<DatFieldChange> EntryFields, IReadOnlyList<DatFileChange> Files);
