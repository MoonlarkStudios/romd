namespace Romd.Domain.Catalog;

public sealed class IdentityFacts
{
    public IdentityFacts(string name, IReadOnlyList<string> names, IReadOnlyList<int> years,
        IReadOnlyList<string> platforms, string provenance)
    {
        Name = name;
        Names = Array.AsReadOnly(names.ToArray());
        Years = Array.AsReadOnly(years.ToArray());
        Platforms = Array.AsReadOnly(platforms.ToArray());
        Provenance = provenance;
    }

    public string Name { get; }
    public IReadOnlyList<string> Names { get; }
    public IReadOnlyList<int> Years { get; }
    public IReadOnlyList<string> Platforms { get; }
    public string Provenance { get; }
}

public sealed class IdentityCandidate
{
    public IdentityCandidate(string id, string name, IReadOnlyList<string> aliases,
        IReadOnlyList<int> years, IReadOnlyList<string> platforms)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Name = name;
        Aliases = Array.AsReadOnly(aliases.ToArray());
        Years = Array.AsReadOnly(years.ToArray());
        Platforms = Array.AsReadOnly(platforms.ToArray());
    }

    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<string> Aliases { get; }
    public IReadOnlyList<int> Years { get; }
    public IReadOnlyList<string> Platforms { get; }
}

/// <summary>Orchestration supplies a currently trusted bridge and fences its source when publishing.</summary>
public sealed record TrustedIdentityBridgeEvidence(string CandidateId, string Evidence);

public sealed class IdentitySearchEvidence
{
    public IdentitySearchEvidence(IEnumerable<IdentityCandidate> candidates, bool complete,
        TrustedIdentityBridgeEvidence? trustedBridge = null)
    {
        Candidates = Array.AsReadOnly(candidates.ToArray());
        if (trustedBridge is not null && !Candidates.Any(candidate => candidate.Id == trustedBridge.CandidateId))
            throw new ArgumentException("Trusted bridge evidence must identify an observed candidate.", nameof(trustedBridge));
        Complete = complete;
        TrustedBridge = trustedBridge;
    }

    public IReadOnlyList<IdentityCandidate> Candidates { get; }
    public bool Complete { get; }
    public TrustedIdentityBridgeEvidence? TrustedBridge { get; }
}
