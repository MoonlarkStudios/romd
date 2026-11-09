using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Management.Models;

public sealed record OriginalDiscLayout(string Id, Guid Revision, string State, string? Issue,
    string EntryPoint, string DescriptorSha256, ByteCount DescriptorSizeBytes, Guid SourceJobId,
    string SourceRelativePath, string? SystemKey, string? ReleaseId, string? Fingerprint,
    IReadOnlyList<OriginalDiscDependency> Dependencies, Page<OriginalDiscLayoutAudit> Audit);
public sealed record OriginalDiscDependency(string Path, string? RomId, string? Sha256, ByteCount? SizeBytes);
public sealed record OriginalDiscLayoutAudit(Guid Revision, string Action, Guid? Actor, DateTimeOffset At, string? Reason);
public sealed record OriginalDiscLayoutDecisionRequest(string Action, string? ReleaseId,
    IReadOnlyList<OriginalDiscDependencyChoice>? Dependencies, string? Reason, string? ReplacesLayoutId = null, Guid? ReplacesRevision = null);
public sealed record OriginalDiscDependencyChoice(string Path, string RomId);
public sealed record OriginalDiscLayoutCandidate(string ReleaseId, string Name, string Fingerprint, string? SystemKey, string SystemName,
    IReadOnlyList<OriginalDiscDependencyCandidate> Files, IReadOnlyList<OriginalDiscRequirement> Requirements, string? CurrentLayoutId, Guid? CurrentLayoutRevision);
public sealed record OriginalDiscDependencyCandidate(string RomId, string Name, string Sha256, ByteCount SizeBytes);

public sealed record OriginalDiscRequirement(string Name, string? Sha1, ByteCount SizeBytes, bool OriginalAvailable,
    bool SatisfiedByDescriptor);
