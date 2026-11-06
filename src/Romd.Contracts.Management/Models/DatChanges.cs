using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Management.Models;

public sealed record DatFieldChange(string Field, string? Before, string? After);
public sealed record DatFileChange(string Name, string Kind, string Change, bool ChecksumsChanged,
    IReadOnlyList<DatFieldChange> Fields);
public sealed record DatEntryChangeDetail(string Id, string Name, string Change, IReadOnlyList<DatFieldChange> Fields,
    Page<DatFileChange> Files, string ActiveSha256, string CandidateSha256);
