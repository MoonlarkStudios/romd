namespace Romd.Domain.Catalog;

// Natural source identities and fingerprints survive projection rebuilds. No release identity is replaced.
public sealed record DiscSetMember(string EntryKey, string Name, string Fingerprint, int Ordinal, int TitleId);
public sealed record DiscSetDefinition(string Name, int? ExpectedDiscCount,
    IReadOnlyList<DiscSetMember> Members, IReadOnlyList<string> ReviewReasons, string RuleVersion);

public sealed record DiscSetSourceClaim(int SourceId, string EntryKey, string Name, string Fingerprint,
    int ReleaseId, int TitleId, bool Conflict);
public sealed record DiscSetProposal(int Source, string Key, DiscSetDefinition Value);
