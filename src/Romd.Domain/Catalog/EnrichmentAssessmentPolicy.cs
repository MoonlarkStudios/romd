using System.Linq.Expressions;
namespace Romd.Domain.Catalog;

public enum EnrichmentRequirementState { Satisfied, Automatic, NeedsReview, Blocked, Processing }
// Values form the PostgreSQL facts-function contract; never reorder or reuse.
public enum EnrichmentRequirementKind { Identity = 0, Metadata = 1, Artwork = 2, Operation = 3 }
// Names form the PostgreSQL facts-function text contract; never rename or reuse.
public enum EnrichmentRequirementCause
{
    MatchDecisionRequired, NoMatchFound, ProviderUnconfigured, ProviderDisabled, ProviderUnavailable, Discover,
    Working, NotProvided, ProviderSuppressed, AwaitingIdentity, NotAttempted, Stale, ArtworkDecisionRequired,
    NothingUsable, AcquisitionFailed, Importing, Searching
}
// Names form the PostgreSQL facts-function text contract; never rename or reuse.
public enum EnrichmentRequirementSatisfiedBy
{
    Present, AcceptedOmission, LeftEmpty, UnavailableFallback, Trusted, ProviderSuppressed, NotRequested, Current, Idle
}

/// <summary>Facts independent of job outcomes; the expression is also translated by persistence into SQL.</summary>
public sealed class EnrichmentRequirementFacts
{
    public int TitleId { get; init; }
    public EnrichmentRequirementKind Kind { get; init; }
    public string Subject { get; init; } = string.Empty;
    public bool Satisfied { get; init; }
    public bool RequiresDecision { get; init; }
    public bool Blocked { get; init; }
    public bool Processing { get; init; }
    /// <summary>Cause text for the state these facts classify to; null when satisfied.</summary>
    public string? Cause { get; init; }
    /// <summary>Satisfaction text; null unless satisfied.</summary>
    public string? SatisfiedBy { get; init; }
}
public sealed class EnrichmentRequirement
{
    public int TitleId { get; init; }
    public EnrichmentRequirementKind Kind { get; init; }
    public string Subject { get; init; } = string.Empty;
    public EnrichmentRequirementState State { get; init; }
    public string? Cause { get; init; }
    public string? SatisfiedBy { get; init; }
}

public static class EnrichmentAssessmentPolicy
{
    // These fields make a useful catalog entry. Optional ratings, player counts and credits remain editable.
    public static readonly IReadOnlyList<string> CoreMetadataFields = Array.AsReadOnly(new[] { "Description", "Genre", "ReleaseDate" });
    public static readonly Expression<Func<EnrichmentRequirementFacts, EnrichmentRequirement>> Classify = fact => new EnrichmentRequirement
    {
        TitleId = fact.TitleId,
        Kind = fact.Kind,
        Subject = fact.Subject,
        State = fact.Satisfied ? EnrichmentRequirementState.Satisfied :
            fact.RequiresDecision ? EnrichmentRequirementState.NeedsReview :
            fact.Processing ? EnrichmentRequirementState.Processing :
            fact.Blocked ? EnrichmentRequirementState.Blocked : EnrichmentRequirementState.Automatic,
        Cause = fact.Satisfied ? null : fact.Cause,
        SatisfiedBy = fact.Satisfied ? fact.SatisfiedBy : null
    };
}
