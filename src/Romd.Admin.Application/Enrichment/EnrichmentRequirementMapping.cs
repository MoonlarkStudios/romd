using Romd.Contracts.Management.Enrichment;
using Romd.Domain.Catalog;
using Cause = Romd.Contracts.Management.Enrichment.EnrichmentRequirementCause;
using DomainCause = Romd.Domain.Catalog.EnrichmentRequirementCause;
using DomainSatisfiedBy = Romd.Domain.Catalog.EnrichmentRequirementSatisfiedBy;
using SatisfiedBy = Romd.Contracts.Management.Enrichment.EnrichmentRequirementSatisfiedBy;
using State = Romd.Contracts.Management.Enrichment.EnrichmentIssueState;

namespace Romd.Admin.Application.Enrichment;

/// <summary>
///     Maps classified requirements to the wire contract in canonical order. Curator copy lives in the admin SPA;
///     actions are role-independent and follow only from kind, state, cause and satisfaction. Cause and
///     satisfaction text is parsed strictly; a row the facts function should never produce fails loudly.
/// </summary>
public static class EnrichmentRequirementMapping
{
    private static readonly string[] IdentityOrder = ["igdb", "steamgriddb"];
    private static readonly string[] MetadataOrder = ["Description", "Genre", "ReleaseDate", "Refresh"];
    private static readonly string[] ArtworkOrder = ["Poster", "Backdrop", "Hero", "Logo"];

    private enum RequirementGroup { Identity, MetadataField, Refresh, Artwork, Operation }

    public static IReadOnlyList<EnrichmentRequirementDto> ToContract(IEnumerable<EnrichmentRequirement> requirements) =>
        requirements.OrderBy(requirement => requirement.Kind).ThenBy(SubjectRank)
            .ThenBy(requirement => requirement.Subject, StringComparer.Ordinal)
            .Select(ToContract).ToArray();

    public static EnrichmentRequirementDto ToContract(EnrichmentRequirement requirement)
    {
        var kind = requirement.Kind.ToString();
        var group = Group(requirement);
        var state = ToContract(requirement.State);
        var cause = requirement.Cause is null ? (Cause?)null : ToContract(Parse<DomainCause>(requirement.Cause));
        var satisfiedBy = requirement.SatisfiedBy is null
            ? (SatisfiedBy?)null
            : ToContract(Parse<DomainSatisfiedBy>(requirement.SatisfiedBy));
        return new($"{kind}:{requirement.Subject}", kind, requirement.Subject, state, cause, satisfiedBy,
            Actions(group, state, cause, satisfiedBy));
    }

    private static int SubjectRank(EnrichmentRequirement requirement)
    {
        var order = requirement.Kind switch
        {
            EnrichmentRequirementKind.Identity => IdentityOrder,
            EnrichmentRequirementKind.Metadata => MetadataOrder,
            EnrichmentRequirementKind.Artwork => ArtworkOrder,
            _ => []
        };
        var rank = Array.IndexOf(order, requirement.Subject);
        return rank < 0 ? order.Length : rank;
    }

    private static RequirementGroup Group(EnrichmentRequirement requirement) => requirement.Kind switch
    {
        EnrichmentRequirementKind.Identity => RequirementGroup.Identity,
        EnrichmentRequirementKind.Metadata when requirement.Subject == "Refresh" => RequirementGroup.Refresh,
        EnrichmentRequirementKind.Metadata => RequirementGroup.MetadataField,
        EnrichmentRequirementKind.Artwork => RequirementGroup.Artwork,
        EnrichmentRequirementKind.Operation => RequirementGroup.Operation,
        _ => throw new ArgumentOutOfRangeException(nameof(requirement), requirement.Kind, "Unknown enrichment requirement kind.")
    };

    private static State ToContract(EnrichmentRequirementState state) => state switch
    {
        EnrichmentRequirementState.Satisfied => State.Satisfied,
        EnrichmentRequirementState.Automatic => State.Automatic,
        EnrichmentRequirementState.NeedsReview => State.NeedsReview,
        EnrichmentRequirementState.Blocked => State.Blocked,
        EnrichmentRequirementState.Processing => State.Processing,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown enrichment requirement state.")
    };

    private static TEnum Parse<TEnum>(string text) where TEnum : struct, Enum =>
        EnumNames<TEnum>.Values.TryGetValue(text, out var value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(text), text, $"Unknown {typeof(TEnum).Name} text.");

    private static Cause ToContract(DomainCause cause) => cause switch
    {
        DomainCause.MatchDecisionRequired => Cause.MatchDecisionRequired,
        DomainCause.NoMatchFound => Cause.NoMatchFound,
        DomainCause.ProviderUnconfigured => Cause.ProviderUnconfigured,
        DomainCause.ProviderDisabled => Cause.ProviderDisabled,
        DomainCause.ProviderUnavailable => Cause.ProviderUnavailable,
        DomainCause.Discover => Cause.Discover,
        DomainCause.Working => Cause.Working,
        DomainCause.NotProvided => Cause.NotProvided,
        DomainCause.ProviderSuppressed => Cause.ProviderSuppressed,
        DomainCause.AwaitingIdentity => Cause.AwaitingIdentity,
        DomainCause.NotAttempted => Cause.NotAttempted,
        DomainCause.Stale => Cause.Stale,
        DomainCause.ArtworkDecisionRequired => Cause.ArtworkDecisionRequired,
        DomainCause.NothingUsable => Cause.NothingUsable,
        DomainCause.AcquisitionFailed => Cause.AcquisitionFailed,
        DomainCause.Importing => Cause.Importing,
        DomainCause.Searching => Cause.Searching,
        _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "Unknown enrichment requirement cause.")
    };

    private static SatisfiedBy ToContract(DomainSatisfiedBy satisfiedBy) => satisfiedBy switch
    {
        DomainSatisfiedBy.Present => SatisfiedBy.Present,
        DomainSatisfiedBy.AcceptedOmission => SatisfiedBy.AcceptedOmission,
        DomainSatisfiedBy.LeftEmpty => SatisfiedBy.LeftEmpty,
        DomainSatisfiedBy.UnavailableFallback => SatisfiedBy.UnavailableFallback,
        DomainSatisfiedBy.Trusted => SatisfiedBy.Trusted,
        DomainSatisfiedBy.ProviderSuppressed => SatisfiedBy.ProviderSuppressed,
        DomainSatisfiedBy.NotRequested => SatisfiedBy.NotRequested,
        DomainSatisfiedBy.Current => SatisfiedBy.Current,
        DomainSatisfiedBy.Idle => SatisfiedBy.Idle,
        _ => throw new ArgumentOutOfRangeException(nameof(satisfiedBy), satisfiedBy, "Unknown enrichment requirement satisfaction.")
    };

    // Each arm is one allowed (group, state, cause or satisfaction) combination. Anything else is an
    // invariant violation in the facts function and must fail loudly rather than claim success.
    private static IReadOnlyList<string> Actions(RequirementGroup group, State state, Cause? cause, SatisfiedBy? satisfiedBy) =>
        (group, state, cause, satisfiedBy) switch
        {
            (RequirementGroup.Identity or RequirementGroup.MetadataField or RequirementGroup.Refresh or RequirementGroup.Operation,
                State.Processing, Cause.Working, null) => [],
            (RequirementGroup.Artwork, State.Processing, Cause.Importing or Cause.Searching, null) => [],

            (RequirementGroup.Identity, State.NeedsReview, Cause.MatchDecisionRequired or Cause.NoMatchFound, null) =>
                [EnrichmentActions.ReviewProvider, EnrichmentActions.ContinueWithoutProvider],
            (RequirementGroup.Identity, State.Blocked, Cause.ProviderUnconfigured or Cause.ProviderDisabled, null) =>
                [EnrichmentActions.ConfigureProvider],
            (RequirementGroup.Identity, State.Blocked, Cause.ProviderUnavailable, null) => [EnrichmentActions.Retry],
            (RequirementGroup.Identity, State.Automatic, Cause.Discover, null) => [EnrichmentActions.Enrich],
            (RequirementGroup.Identity, State.Satisfied, null, SatisfiedBy.Trusted or SatisfiedBy.ProviderSuppressed) =>
                [EnrichmentActions.ReviewProvider],

            (RequirementGroup.MetadataField, State.NeedsReview, Cause.NotProvided, null) =>
                [EnrichmentActions.ReviewMetadata, EnrichmentActions.AcceptOmission],
            (RequirementGroup.MetadataField, State.NeedsReview, Cause.ProviderSuppressed, null) =>
                [EnrichmentActions.ReviewMetadata, EnrichmentActions.AcceptOmission, EnrichmentActions.ReviewProvider],
            (RequirementGroup.MetadataField, State.Blocked, Cause.AwaitingIdentity, null) => [EnrichmentActions.ReviewProvider],
            (RequirementGroup.MetadataField, State.Blocked, Cause.ProviderUnconfigured or Cause.ProviderDisabled, null) =>
                [EnrichmentActions.ConfigureProvider],
            (RequirementGroup.MetadataField, State.Blocked, Cause.ProviderUnavailable, null) => [EnrichmentActions.Retry],
            (RequirementGroup.MetadataField, State.Automatic, Cause.NotAttempted or Cause.Stale, null) => [EnrichmentActions.Enrich],
            (RequirementGroup.MetadataField, State.Satisfied, null, SatisfiedBy.Present or SatisfiedBy.AcceptedOmission) =>
                [EnrichmentActions.ReviewMetadata],

            (RequirementGroup.Refresh, State.Automatic, Cause.Stale, null) => [EnrichmentActions.Enrich],
            (RequirementGroup.Refresh, State.Satisfied, null, SatisfiedBy.Current) => [],

            (RequirementGroup.Artwork, State.NeedsReview, Cause.ArtworkDecisionRequired, null) => [EnrichmentActions.ReviewArtwork],
            (RequirementGroup.Artwork, State.NeedsReview, Cause.NothingUsable, null) =>
                [EnrichmentActions.ReviewArtwork, EnrichmentActions.LeaveEmpty],
            (RequirementGroup.Artwork, State.NeedsReview, Cause.ProviderSuppressed, null) =>
                [EnrichmentActions.ReviewArtwork, EnrichmentActions.LeaveEmpty, EnrichmentActions.ReviewProvider],
            (RequirementGroup.Artwork, State.Blocked, Cause.AwaitingIdentity, null) => [EnrichmentActions.ReviewProvider],
            (RequirementGroup.Artwork, State.Blocked, Cause.ProviderUnconfigured or Cause.ProviderDisabled, null) =>
                [EnrichmentActions.ConfigureProvider],
            (RequirementGroup.Artwork, State.Blocked, Cause.ProviderUnavailable or Cause.AcquisitionFailed, null) =>
                [EnrichmentActions.Retry],
            (RequirementGroup.Artwork, State.Automatic, Cause.NotAttempted, null) => [EnrichmentActions.Enrich],
            (RequirementGroup.Artwork, State.Satisfied, null,
                SatisfiedBy.LeftEmpty or SatisfiedBy.Present or SatisfiedBy.NotRequested or SatisfiedBy.UnavailableFallback) =>
                [EnrichmentActions.ReviewArtwork],

            (RequirementGroup.Operation, State.Satisfied, null, SatisfiedBy.Idle) => [],
            _ => throw new InvalidOperationException(
                $"Enrichment requirement {group} {state} has an invalid cause '{cause}' or satisfaction '{satisfiedBy}'.")
        };

    // Exact declared names only: no case variants, whitespace, numbers or comma-composed values.
    private static class EnumNames<TEnum> where TEnum : struct, Enum
    {
        public static readonly IReadOnlyDictionary<string, TEnum> Values =
            Enum.GetValues<TEnum>().ToDictionary(value => value.ToString(), StringComparer.Ordinal);
    }
}
