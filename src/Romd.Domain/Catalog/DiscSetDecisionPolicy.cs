namespace Romd.Domain.Catalog;

public enum DiscSetDecisionProblem { None, Decision, Review, Members, Source, Scope, Changed }

public sealed record DiscSetMemberChange(string EntryKey, int Ordinal);

/// <summary>Owns the submitted values independently of the transport's mutable collections.</summary>
public sealed class DiscSetDecisionInput(string? decision, string? name, int? expectedDiscCount,
    IEnumerable<DiscSetMemberChange?>? members, string? reason)
{
    public string? Decision { get; } = decision;
    public string? Name { get; } = name;
    public int? ExpectedDiscCount { get; } = expectedDiscCount;
    public IReadOnlyList<DiscSetMemberChange?>? Members { get; } = members is null ? null : Array.AsReadOnly(
        members.Select(member => member is null ? null : new DiscSetMemberChange(member.EntryKey, member.Ordinal)).ToArray());
    public string? Reason { get; } = reason;
}

/// <summary>The current decision and definitions, copied from a protected persistence read.</summary>
public sealed class DiscSetDecisionFacts(int sourceId, string decision, DiscSetDefinition proposal, DiscSetDefinition? correction)
{
    public int SourceId { get; } = sourceId;
    public string Decision { get; } = decision;
    public DiscSetDefinition Proposal { get; } = Own(proposal);
    public DiscSetDefinition? Correction { get; } = correction is null ? null : Own(correction);
    public DiscSetDefinition Effective => Correction ?? Proposal;

    internal static DiscSetDefinition Own(DiscSetDefinition value) => new(value.Name, value.ExpectedDiscCount,
        Array.AsReadOnly(value.Members.Select(member => member with { }).ToArray()),
        Array.AsReadOnly(value.ReviewReasons.ToArray()), value.RuleVersion);
}

/// <summary>A bounded, owned index. Ambiguous claims are rejected rather than overwritten.</summary>
public sealed class DiscSetClaims
{
    private readonly Dictionary<(int SourceId, string EntryKey), DiscSetSourceClaim[]> _claims;
    public DiscSetClaims(IEnumerable<DiscSetSourceClaim> claims)
    {
        _claims = claims.Select(claim => claim with { }).GroupBy(claim => (claim.SourceId, claim.EntryKey))
            .ToDictionary(group => group.Key, group => group.ToArray());
    }
    public DiscSetSourceClaim? Find(int sourceId, string entryKey) => !_claims.TryGetValue((sourceId, entryKey), out var matches)
        ? null : matches.Length == 1 ? matches[0]
        : throw new InvalidOperationException("More than one source claim names the same disc-set member.");
}

public sealed record DiscSetResolvedMember(string EntryKey, string Name, int Ordinal, int? ReleaseId, string? Issue);
public sealed record DiscSetEligibility(bool IsValid, bool IsEligible);

public sealed class DiscSetDecisionChange
{
    internal DiscSetDecisionChange(string decision, DiscSetDefinition? correction, DiscSetDefinition proposal, bool replaceCorrection)
    {
        Decision = decision;
        ReplaceCorrection = replaceCorrection;
        Correction = correction is null ? null : DiscSetDecisionFacts.Own(correction);
        TitleIds = Array.AsReadOnly(proposal.Members.Select(member => member.TitleId)
            .Concat(Correction?.Members.Select(member => member.TitleId) ?? []).Distinct().ToArray());
    }
    public string Decision { get; }
    public bool ReplaceCorrection { get; }
    public DiscSetDefinition? Correction { get; }
    public IReadOnlyList<int> TitleIds { get; }
}

public sealed class DiscSetDecisionResult
{
    private DiscSetDecisionResult(DiscSetDecisionProblem problem, DiscSetDecisionChange? change)
    { Problem = problem; Change = change; }
    public DiscSetDecisionProblem Problem { get; }
    public DiscSetDecisionChange? Change { get; }
    internal static DiscSetDecisionResult Reject(DiscSetDecisionProblem problem) => new(problem, null);
    internal static DiscSetDecisionResult Apply(DiscSetDecisionChange change) => new(DiscSetDecisionProblem.None, change);
}

/// <summary>Disc-set decisions and eligibility over explicit owned facts; no clocks, locks or persistence.</summary>
public static class DiscSetDecisionPolicy
{
    public static DiscSetDecisionProblem ValidateRequest(DiscSetDecisionInput input) =>
        input.Decision is not ("accept" or "reject" or "correct" or "reset") || input.Reason?.Length > 1000
            ? DiscSetDecisionProblem.Decision : DiscSetDecisionProblem.None;

    public static IReadOnlyCollection<string> RequiredEntryKeys(DiscSetDecisionInput input, DiscSetDecisionFacts facts) =>
        Array.AsReadOnly(facts.Proposal.Members.Select(member => member.EntryKey)
            .Concat(input.Decision == "reject" ? facts.Correction?.Members.Select(member => member.EntryKey) ?? [] : [])
            .Concat(input.Decision == "correct" && ValidCorrection(input) ? input.Members!.Select(member => member!.EntryKey) : [])
            .Distinct(StringComparer.Ordinal).ToArray());

    public static DiscSetDecisionResult Decide(DiscSetDecisionInput input, DiscSetDecisionFacts facts, DiscSetClaims claims)
    {
        var invalid = ValidateRequest(input);
        if (invalid != DiscSetDecisionProblem.None)
            return DiscSetDecisionResult.Reject(invalid);
        var correction = facts.Correction;
        string decision;
        if (input.Decision is "accept" or "correct")
        {
            if (input.Decision == "accept")
            {
                if (facts.Proposal.ReviewReasons.Count != 0)
                    return DiscSetDecisionResult.Reject(DiscSetDecisionProblem.Review);
                correction = facts.Proposal;
            }
            else
            {
                if (!ValidCorrection(input))
                    return DiscSetDecisionResult.Reject(DiscSetDecisionProblem.Members);
                var members = new List<DiscSetMember>();
                foreach (var edit in input.Members!.OrderBy(edit => edit!.Ordinal))
                {
                    var claim = claims.Find(facts.SourceId, edit!.EntryKey);
                    if (claim is null || claim.Conflict)
                        return DiscSetDecisionResult.Reject(DiscSetDecisionProblem.Source);
                    members.Add(new(claim.EntryKey, claim.Name, claim.Fingerprint, edit.Ordinal, claim.TitleId));
                }
                if (members.Select(member => member.TitleId).Distinct().Count() != 1
                    || members.Select(member => member.Fingerprint).Distinct().Count() != members.Count
                    || input.ExpectedDiscCount < members.Max(member => member.Ordinal))
                    return DiscSetDecisionResult.Reject(DiscSetDecisionProblem.Scope);
                correction = new(input.Name!.Trim(), input.ExpectedDiscCount, members, [], "manual-v1");
            }
            if (Resolve(correction, facts.SourceId, claims).Any(member => member.Issue is not null))
                return DiscSetDecisionResult.Reject(DiscSetDecisionProblem.Changed);
            decision = "accepted";
        }
        else if (input.Decision == "reject")
            decision = "rejected";
        else
        { decision = "proposed"; correction = null; }
        return DiscSetDecisionResult.Apply(new(decision, correction, facts.Proposal, input.Decision != "reject"));
    }

    private static bool ValidCorrection(DiscSetDecisionInput input) =>
        !string.IsNullOrWhiteSpace(input.Name) && input.Name.Length <= 500 && input.ExpectedDiscCount is not (< 1 or > 99)
        && input.Members is { Count: >= 1 and <= 99 } edits
        && edits.All(edit => edit is not null && !string.IsNullOrWhiteSpace(edit.EntryKey) && edit.EntryKey.Length <= 500 && edit.Ordinal is >= 1 and <= 99)
        && edits.Select(edit => edit!.Ordinal).Distinct().Count() == edits.Count
        && edits.Select(edit => edit!.EntryKey).Distinct(StringComparer.Ordinal).Count() == edits.Count;

    public static IReadOnlyList<DiscSetResolvedMember> Resolve(DiscSetDefinition definition, int sourceId, DiscSetClaims claims) =>
        Array.AsReadOnly(definition.Members.Select(member =>
        {
            var claim = claims.Find(sourceId, member.EntryKey);
            string? issue = claim is null ? "SourceUnavailable" : claim.Fingerprint != member.Fingerprint ? "ContentChanged"
                : claim.TitleId != member.TitleId || claim.Conflict ? "TitleConflict" : null;
            return new DiscSetResolvedMember(member.EntryKey, member.Name, member.Ordinal, issue is null ? claim!.ReleaseId : null, issue);
        }).ToArray());

    public static DiscSetEligibility Eligibility(DiscSetDecisionFacts facts, DiscSetClaims claims, IReadOnlySet<int> eligibleReleaseIds)
    {
        var definition = facts.Effective;
        var resolved = Resolve(definition, facts.SourceId, claims);
        bool valid = facts.Decision == "accepted" && facts.Correction is not null && definition.Members.Count is >= 1 and <= 99
            && definition.Members.Select(member => member.TitleId).Distinct().Count() == 1
            && resolved.All(member => member.Issue is null);
        return new(valid, valid && definition.Members.All(member => eligibleReleaseIds.Contains(claims.Find(facts.SourceId, member.EntryKey)!.ReleaseId)));
    }
}
