using ErrorOr;
using Romd.Application.Common.Pagination;

namespace Romd.Admin.Application.Source.Dat;

public interface IDatReplacementReview
{
    Task<ErrorOr<DatDocumentInspection>> InspectAsync(Stream candidate, CancellationToken ct);
    /// <summary>Changed entries of the reviewed pair; a <c>null</c> <paramref name="datId" /> reviews a first import.</summary>
    Task<ErrorOr<PagedList<DatEntryChange>>> ListChangesAsync(int? datId, string expectedName, Stream candidate,
        DatChangePageRequest page, CancellationToken ct);
    /// <summary>One changed entry of the reviewed pair with a page of its changed files.</summary>
    Task<ErrorOr<DatEntryChangeDetail>> GetEntryChangeAsync(int? datId, string expectedName, Stream candidate,
        string entryName, DatChangePageRequest page, CancellationToken ct);
    Task<ErrorOr<DatReplacementPreview>> PreviewInitialAsync(string expectedName, Stream candidate, CancellationToken ct);
    Task<ErrorOr<DatReplacementPreview>> PreviewAsync(int datId, Stream candidate, CancellationToken ct);
}

public sealed record DatEntryChange(string Name, string Change, int FilesAdded, int FilesRemoved, int FilesChanged);
public sealed record DatReplacementPreview(
    string ActiveSha256, string CandidateSha256, string? ActiveVersion, string? CandidateVersion,
    bool Unchanged, int ActiveEntries, int CandidateEntries, int EntriesAdded, int EntriesRemoved,
    int EntriesChanged, int FilesAdded, int FilesRemoved, int FilesChanged, int HashesChanged,
    int ActiveBiosEntries, int CandidateBiosEntries, IReadOnlyList<DatEntryChange> Changes, bool Truncated, int ActiveFiles = 0, int CandidateFiles = 0);

public sealed record DatFieldChange(string Field, string? Before, string? After);
public sealed record DatFileChange(string Name, string Kind, string Change, bool ChecksumsChanged,
    IReadOnlyList<DatFieldChange> Fields);
public sealed record DatEntryChangeDetail(string Name, string Change, IReadOnlyList<DatFieldChange> Fields,
    PagedList<DatFileChange> Files, string ActiveSha256, string CandidateSha256);

public sealed record DatDocumentInspection(string Name, int Bytes, DatReplacementPreview Preview);
