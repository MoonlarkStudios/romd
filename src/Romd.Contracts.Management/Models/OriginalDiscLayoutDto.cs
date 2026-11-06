using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Management.Models;

public sealed record OriginalDiscLayoutDto(string Id, Guid Revision, string State, string? Issue,
    string EntryPoint, string DescriptorSha256, ByteCount DescriptorSizeBytes, Guid SourceJobId,
    string SourceRelativePath, string? SystemKey, string? ReleaseId, string? Fingerprint,
    IReadOnlyList<OriginalDiscDependencyDto> Dependencies, Page<OriginalDiscLayoutAuditDto> Audit);
public sealed record OriginalDiscDependencyDto(string Path, string? RomId, string? Sha256, ByteCount? SizeBytes);
public sealed record OriginalDiscLayoutAuditDto(Guid Revision, string Action, Guid? Actor, DateTimeOffset At, string? Reason);
public sealed record OriginalDiscLayoutDecisionRequest(string Action, string? ReleaseId,
    IReadOnlyList<OriginalDiscDependencyChoice>? Dependencies, string? Reason, string? ReplacesLayoutId = null, Guid? ReplacesRevision = null);
public sealed record OriginalDiscDependencyChoice(string Path, string RomId);
public sealed record OriginalDiscLayoutCandidateDto(string ReleaseId, string Name, string Fingerprint, string? SystemKey, string SystemName,
    IReadOnlyList<OriginalDiscDependencyCandidateDto> Files, IReadOnlyList<OriginalDiscRequirementDto> Requirements, string? CurrentLayoutId, Guid? CurrentLayoutRevision);
public sealed record OriginalDiscDependencyCandidateDto(string RomId, string Name, string Sha256, ByteCount SizeBytes);

public sealed record OriginalDiscRequirementDto(string Name, string? Sha1, ByteCount SizeBytes, bool OriginalAvailable,
    bool SatisfiedByDescriptor);
