using ErrorOr;
using Romd.Admin.Application.Ingestion.Jobs;

namespace Romd.Admin.Application.Source.Dat;

public interface IDatReplacementReview
{
    Task<ErrorOr<DatDocumentInspection>> InspectAsync(Stream candidate, CancellationToken ct);
    Task<ErrorOr<DatChangePage>> ChangesAsync(int? datId, string expectedName, Stream candidate, DatChangeQuery query, CancellationToken ct);
    Task<ErrorOr<DatReplacementPreview>> PreviewInitialAsync(string expectedName, Stream candidate, CancellationToken ct);
    Task<ErrorOr<DatReplacementPreview>> PreviewAsync(int datId, Stream candidate, CancellationToken ct);
    Task<ErrorOr<ReplaceDatJobCreationResult>> ApplyAsync(int datId, Stream candidate,
        string activeHash, string candidateHash, CancellationToken ct);
}

public sealed record DatEntryChange(string Name, string Change, int FilesAdded, int FilesRemoved, int FilesChanged);
public sealed record DatReplacementPreview(
    string ActiveSha256, string CandidateSha256, string? ActiveVersion, string? CandidateVersion,
    bool Unchanged, int ActiveEntries, int CandidateEntries, int EntriesAdded, int EntriesRemoved,
    int EntriesChanged, int FilesAdded, int FilesRemoved, int FilesChanged, int HashesChanged,
    int ActiveBiosEntries, int CandidateBiosEntries, IReadOnlyList<DatEntryChange> Changes, bool Truncated, int ActiveFiles = 0, int CandidateFiles = 0);

public sealed record DatChangeQuery(string ActiveSha256, string CandidateSha256,
    string? Search = null, string? Change = null, int Offset = 0, string? EntryName = null);
public sealed record DatFieldChange(string Field, string? Before, string? After);
public sealed record DatFileChange(string Name, string Kind, string Change, bool ChecksumsChanged,
    IReadOnlyList<DatFieldChange> Fields);
public sealed record DatChangePage(string ActiveSha256, string CandidateSha256, int Offset,
    int PageSize, int Total, IReadOnlyList<DatEntryChange> Entries, string? EntryName,
    IReadOnlyList<DatFieldChange> EntryFields, IReadOnlyList<DatFileChange> Files);

public sealed record DatDocumentInspection(string Name, int Bytes, DatReplacementPreview Preview);
