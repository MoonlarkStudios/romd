namespace Romd.Contracts.Management.Models;

public sealed record DiscSetMemberDto(string EntryKey, string Name, int Ordinal, string? ReleaseId, string? Issue);
public sealed record DiscSetDefinitionDto(string Name, int? ExpectedDiscCount,
    IReadOnlyList<DiscSetMemberDto> Members, IReadOnlyList<string> ReviewReasons, string RuleVersion);
public sealed record DiscSetDto(string Id, string CatalogSourceId, string? SystemKey, Guid Revision,
    string Decision, DiscSetDefinitionDto Proposal, DiscSetDefinitionDto? Correction,
    Guid? DecidedBy, DateTimeOffset? DecidedAt, string? Reason);
public sealed record DiscSetMemberEdit(string EntryKey, int Ordinal);
public sealed record DecideDiscSetRequest(string Decision, string? Name,
    int? ExpectedDiscCount, IReadOnlyList<DiscSetMemberEdit>? Members, string? Reason);
