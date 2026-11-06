namespace Romd.Domain.Catalog;

public sealed class IdentityAssessment
{
    internal IdentityAssessment(string candidateId, int score, bool eligible, IEnumerable<string> evidence,
        IEnumerable<string> conflicts)
    {
        CandidateId = candidateId;
        Score = score;
        Eligible = eligible;
        Evidence = Array.AsReadOnly(evidence.ToArray());
        Conflicts = Array.AsReadOnly(conflicts.ToArray());
    }

    public string CandidateId { get; }
    public int Score { get; }
    public bool Eligible { get; }
    public IReadOnlyList<string> Evidence { get; }
    public IReadOnlyList<string> Conflicts { get; }
}

/// <summary>Closed policy outcomes; state strings are read-only projections for the existing decision contract.</summary>
public abstract class IdentityDecision
{
    private IdentityDecision(string explanation, IEnumerable<IdentityAssessment> candidates)
    {
        Explanation = explanation;
        Candidates = Array.AsReadOnly(candidates.ToArray());
    }

    public abstract string State { get; }
    public abstract string? CandidateId { get; }
    public string Explanation { get; }
    public IReadOnlyList<IdentityAssessment> Candidates { get; }

    public sealed class AutoAccepted : IdentityDecision
    {
        internal AutoAccepted(IdentityAssessment candidate, string explanation, IEnumerable<IdentityAssessment> candidates)
            : base(explanation, candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            CandidateId = candidate.CandidateId;
        }

        public override string State => "AutoAccepted";
        public override string CandidateId { get; }
    }

    public sealed class NeedsReview : IdentityDecision
    {
        internal NeedsReview(IdentityAssessment? candidate, string explanation, IEnumerable<IdentityAssessment> candidates)
            : base(explanation, candidates) => CandidateId = candidate?.CandidateId;

        public override string State => "NeedsReview";
        public override string? CandidateId { get; }
    }

    public sealed class NoMatch : IdentityDecision
    {
        internal NoMatch(string explanation) : base(explanation, []) { }

        public override string State => "NoMatch";
        public override string? CandidateId => null;
    }
}
