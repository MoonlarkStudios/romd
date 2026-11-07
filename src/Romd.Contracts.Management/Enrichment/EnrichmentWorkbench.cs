namespace Romd.Contracts.Management.Enrichment;

/// <summary>Current requirement state, independent of the actions available to a curator.</summary>
public enum EnrichmentIssueState
{
    Satisfied = 0,
    Automatic = 1,
    NeedsReview = 2,
    Blocked = 3,
    Processing = 4
}

/// <summary>The facts responsible for a requirement that is not satisfied.</summary>
public enum EnrichmentRequirementCause
{
    MatchDecisionRequired, NoMatchFound, ProviderUnconfigured, ProviderDisabled, ProviderUnavailable, Discover,
    Working, NotProvided, ProviderSuppressed, AwaitingIdentity, NotAttempted, Stale, ArtworkDecisionRequired,
    NothingUsable, AcquisitionFailed, Importing, Searching
}

/// <summary>The facts that satisfy a requirement. Satisfaction does not always mean a value exists.</summary>
public enum EnrichmentRequirementSatisfiedBy
{
    Present, AcceptedOmission, LeftEmpty, UnavailableFallback, Trusted, ProviderSuppressed, NotRequested, Current, Idle
}

/// <summary>
///     One current requirement. Cause is non-null exactly when State is not Satisfied; SatisfiedBy is non-null
///     exactly when State is Satisfied.
/// </summary>
public sealed record EnrichmentRequirement(string Key, string Kind, string Subject, EnrichmentIssueState State,
    EnrichmentRequirementCause? Cause, EnrichmentRequirementSatisfiedBy? SatisfiedBy, IReadOnlyList<string> Actions);
/// <summary>Requirements lists every current requirement, satisfied rows included, in canonical order.</summary>
public sealed record EnrichmentAssessment(string TitleId, string TitleName, string PlatformName, Guid Revision,
    bool NeedsReview, bool CanEnrichAutomatically, bool HasActiveWork, bool HasOperationalBlocker,
    IReadOnlyList<EnrichmentRequirement> Requirements, IReadOnlyList<string> AcceptedOmissions,
    string? ThumbnailUrl = null, string? PlatformCompactLabel = null);
public sealed record EnrichmentWorkbenchCounts(int Total, int NeedsReview, int Automatic, int Processing, int Blocked);
public sealed record SetEnrichmentOmissionRequest(bool Accepted);

public sealed record EnrichmentSummary(bool NeedsReview, bool CanEnrichAutomatically, bool HasActiveWork, bool HasOperationalBlocker);
