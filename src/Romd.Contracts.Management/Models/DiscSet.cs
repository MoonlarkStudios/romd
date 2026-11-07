namespace Romd.Contracts.Management.Models;

public sealed record DiscSetMember(string EntryKey, string Name, int Ordinal, string? ReleaseId, string? Issue);
public sealed record DiscSetDefinition(string Name, int? ExpectedDiscCount,
    IReadOnlyList<DiscSetMember> Members, IReadOnlyList<string> ReviewReasons, string RuleVersion);
public sealed record DiscSet(string Id, string CatalogSourceId, string? SystemKey, Guid Revision,
    string Decision, DiscSetDefinition Proposal, DiscSetDefinition? Correction,
    Guid? DecidedBy, DateTimeOffset? DecidedAt, string? Reason);
public sealed record DiscSetMemberEdit(string EntryKey, int Ordinal);
public sealed record DecideDiscSetRequest(string Decision, string? Name,
    int? ExpectedDiscCount, IReadOnlyList<DiscSetMemberEdit>? Members, string? Reason);
