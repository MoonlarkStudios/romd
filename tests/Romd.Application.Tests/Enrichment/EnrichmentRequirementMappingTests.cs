using Romd.Admin.Application.Enrichment;
using Romd.Contracts.Management.Enrichment;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;
using Cause = Romd.Contracts.Management.Enrichment.EnrichmentRequirementCause;
using SatisfiedBy = Romd.Contracts.Management.Enrichment.EnrichmentRequirementSatisfiedBy;

namespace Romd.Application.Tests.Enrichment;

public sealed class EnrichmentRequirementMappingTests
{
    private const string None = "";

    // Every allowed (kind, subject group, state, cause or satisfaction) combination and its actions.
    public static TheoryData<EnrichmentRequirementKind, string, EnrichmentRequirementState, string?, string?, string> AllowedRows => new()
    {
        { EnrichmentRequirementKind.Identity, "igdb", EnrichmentRequirementState.NeedsReview, "MatchDecisionRequired", null, "ReviewProvider,ContinueWithoutProvider" },
        { EnrichmentRequirementKind.Identity, "igdb", EnrichmentRequirementState.NeedsReview, "NoMatchFound", null, "ReviewProvider,ContinueWithoutProvider" },
        { EnrichmentRequirementKind.Identity, "igdb", EnrichmentRequirementState.Blocked, "ProviderUnconfigured", null, "ConfigureProvider" },
        { EnrichmentRequirementKind.Identity, "igdb", EnrichmentRequirementState.Blocked, "ProviderDisabled", null, "ConfigureProvider" },
        { EnrichmentRequirementKind.Identity, "igdb", EnrichmentRequirementState.Blocked, "ProviderUnavailable", null, "Retry" },
        { EnrichmentRequirementKind.Identity, "igdb", EnrichmentRequirementState.Automatic, "Discover", null, "Enrich" },
        { EnrichmentRequirementKind.Identity, "igdb", EnrichmentRequirementState.Processing, "Working", null, None },
        { EnrichmentRequirementKind.Identity, "igdb", EnrichmentRequirementState.Satisfied, null, "Trusted", "ReviewProvider" },
        { EnrichmentRequirementKind.Identity, "steamgriddb", EnrichmentRequirementState.Satisfied, null, "ProviderSuppressed", "ReviewProvider" },
        { EnrichmentRequirementKind.Metadata, "Description", EnrichmentRequirementState.NeedsReview, "NotProvided", null, "ReviewMetadata,AcceptOmission" },
        { EnrichmentRequirementKind.Metadata, "Genre", EnrichmentRequirementState.NeedsReview, "ProviderSuppressed", null, "ReviewMetadata,AcceptOmission,ReviewProvider" },
        { EnrichmentRequirementKind.Metadata, "ReleaseDate", EnrichmentRequirementState.Blocked, "AwaitingIdentity", null, "ReviewProvider" },
        { EnrichmentRequirementKind.Metadata, "Description", EnrichmentRequirementState.Blocked, "ProviderUnconfigured", null, "ConfigureProvider" },
        { EnrichmentRequirementKind.Metadata, "Description", EnrichmentRequirementState.Blocked, "ProviderDisabled", null, "ConfigureProvider" },
        { EnrichmentRequirementKind.Metadata, "Description", EnrichmentRequirementState.Blocked, "ProviderUnavailable", null, "Retry" },
        { EnrichmentRequirementKind.Metadata, "Description", EnrichmentRequirementState.Automatic, "NotAttempted", null, "Enrich" },
        { EnrichmentRequirementKind.Metadata, "Genre", EnrichmentRequirementState.Automatic, "Stale", null, "Enrich" },
        { EnrichmentRequirementKind.Metadata, "Description", EnrichmentRequirementState.Processing, "Working", null, None },
        { EnrichmentRequirementKind.Metadata, "Description", EnrichmentRequirementState.Satisfied, null, "Present", "ReviewMetadata" },
        { EnrichmentRequirementKind.Metadata, "Genre", EnrichmentRequirementState.Satisfied, null, "AcceptedOmission", "ReviewMetadata" },
        { EnrichmentRequirementKind.Metadata, "Refresh", EnrichmentRequirementState.Automatic, "Stale", null, "Enrich" },
        { EnrichmentRequirementKind.Metadata, "Refresh", EnrichmentRequirementState.Processing, "Working", null, None },
        { EnrichmentRequirementKind.Metadata, "Refresh", EnrichmentRequirementState.Satisfied, null, "Current", None },
        { EnrichmentRequirementKind.Artwork, "Backdrop", EnrichmentRequirementState.NeedsReview, "ArtworkDecisionRequired", null, "ReviewArtwork" },
        { EnrichmentRequirementKind.Artwork, "Poster", EnrichmentRequirementState.NeedsReview, "NothingUsable", null, "ReviewArtwork,LeaveEmpty" },
        { EnrichmentRequirementKind.Artwork, "Hero", EnrichmentRequirementState.NeedsReview, "ProviderSuppressed", null, "ReviewArtwork,LeaveEmpty,ReviewProvider" },
        { EnrichmentRequirementKind.Artwork, "Poster", EnrichmentRequirementState.Blocked, "AwaitingIdentity", null, "ReviewProvider" },
        { EnrichmentRequirementKind.Artwork, "Poster", EnrichmentRequirementState.Blocked, "ProviderUnconfigured", null, "ConfigureProvider" },
        { EnrichmentRequirementKind.Artwork, "Logo", EnrichmentRequirementState.Blocked, "ProviderDisabled", null, "ConfigureProvider" },
        { EnrichmentRequirementKind.Artwork, "Poster", EnrichmentRequirementState.Blocked, "ProviderUnavailable", null, "Retry" },
        { EnrichmentRequirementKind.Artwork, "Hero", EnrichmentRequirementState.Blocked, "AcquisitionFailed", null, "Retry" },
        { EnrichmentRequirementKind.Artwork, "Poster", EnrichmentRequirementState.Automatic, "NotAttempted", null, "Enrich" },
        { EnrichmentRequirementKind.Artwork, "Backdrop", EnrichmentRequirementState.Processing, "Importing", null, None },
        { EnrichmentRequirementKind.Artwork, "Poster", EnrichmentRequirementState.Processing, "Searching", null, None },
        { EnrichmentRequirementKind.Artwork, "Backdrop", EnrichmentRequirementState.Satisfied, null, "LeftEmpty", "ReviewArtwork" },
        { EnrichmentRequirementKind.Artwork, "Poster", EnrichmentRequirementState.Satisfied, null, "Present", "ReviewArtwork" },
        { EnrichmentRequirementKind.Artwork, "Hero", EnrichmentRequirementState.Satisfied, null, "NotRequested", "ReviewArtwork" },
        { EnrichmentRequirementKind.Artwork, "Logo", EnrichmentRequirementState.Satisfied, null, "UnavailableFallback", "ReviewArtwork" },
        { EnrichmentRequirementKind.Operation, "Processing", EnrichmentRequirementState.Processing, "Working", null, None },
        { EnrichmentRequirementKind.Operation, "Processing", EnrichmentRequirementState.Satisfied, null, "Idle", None }
    };

    [Theory]
    [MemberData(nameof(AllowedRows))]
    public void ToContract_AllowedCombination_MapsStateCauseSatisfactionAndActions(EnrichmentRequirementKind kind, string subject,
        EnrichmentRequirementState state, string? cause, string? satisfiedBy, string actions)
    {
        var requirement = Requirement(kind, subject, state, cause, satisfiedBy);

        var result = EnrichmentRequirementMapping.ToContract(requirement);

        result.Key.ShouldBe($"{kind}:{subject}");
        result.Kind.ShouldBe(kind.ToString());
        result.Subject.ShouldBe(subject);
        result.State.ToString().ShouldBe(state.ToString());
        result.Cause.ShouldBe(cause is null ? null : Enum.Parse<Cause>(cause));
        result.SatisfiedBy.ShouldBe(satisfiedBy is null ? null : Enum.Parse<SatisfiedBy>(satisfiedBy));
        string.Join(",", result.Actions).ShouldBe(actions);
    }

    [Theory]
    [InlineData(EnrichmentRequirementKind.Identity, "igdb", EnrichmentRequirementState.Satisfied, "Discover", "Trusted")]
    [InlineData(EnrichmentRequirementKind.Identity, "igdb", EnrichmentRequirementState.Satisfied, null, null)]
    [InlineData(EnrichmentRequirementKind.Identity, "igdb", EnrichmentRequirementState.Satisfied, null, "Present")]
    [InlineData(EnrichmentRequirementKind.Identity, "igdb", EnrichmentRequirementState.Blocked, "AcquisitionFailed", null)]
    [InlineData(EnrichmentRequirementKind.Metadata, "Description", EnrichmentRequirementState.NeedsReview, null, null)]
    [InlineData(EnrichmentRequirementKind.Metadata, "Description", EnrichmentRequirementState.Satisfied, null, "Current")]
    [InlineData(EnrichmentRequirementKind.Metadata, "Description", EnrichmentRequirementState.Automatic, "Discover", null)]
    [InlineData(EnrichmentRequirementKind.Metadata, "Refresh", EnrichmentRequirementState.Blocked, "ProviderUnconfigured", null)]
    [InlineData(EnrichmentRequirementKind.Metadata, "Refresh", EnrichmentRequirementState.Satisfied, null, "Present")]
    [InlineData(EnrichmentRequirementKind.Artwork, "Poster", EnrichmentRequirementState.Blocked, "AcquisitionFailed", "Present")]
    [InlineData(EnrichmentRequirementKind.Artwork, "Poster", EnrichmentRequirementState.Automatic, "Searching", null)]
    [InlineData(EnrichmentRequirementKind.Artwork, "Poster", EnrichmentRequirementState.Processing, "Working", null)]
    [InlineData(EnrichmentRequirementKind.Artwork, "Poster", EnrichmentRequirementState.Satisfied, null, "Trusted")]
    [InlineData(EnrichmentRequirementKind.Operation, "Processing", EnrichmentRequirementState.Automatic, "NotAttempted", null)]
    [InlineData(EnrichmentRequirementKind.Operation, "Processing", EnrichmentRequirementState.Satisfied, null, "Present")]
    public void ToContract_IllegalCombination_Throws(EnrichmentRequirementKind kind, string subject,
        EnrichmentRequirementState state, string? cause, string? satisfiedBy)
    {
        var requirement = Requirement(kind, subject, state, cause, satisfiedBy);

        Should.Throw<InvalidOperationException>(() => EnrichmentRequirementMapping.ToContract(requirement));
    }

    [Theory]
    [InlineData("Unknown", null)]
    [InlineData("discover", null)]
    [InlineData(" Discover", null)]
    [InlineData("5", null)]
    [InlineData("Discover,Stale", null)]
    [InlineData(null, "trusted")]
    [InlineData(null, "4")]
    [InlineData(null, "")]
    public void ToContract_UnparseableText_Throws(string? cause, string? satisfiedBy)
    {
        var state = cause is null ? EnrichmentRequirementState.Satisfied : EnrichmentRequirementState.Automatic;
        var requirement = Requirement(EnrichmentRequirementKind.Identity, "igdb", state, cause, satisfiedBy);

        Should.Throw<ArgumentOutOfRangeException>(() => EnrichmentRequirementMapping.ToContract(requirement));
    }

    [Fact]
    public void ToContract_UnknownState_Throws()
    {
        var requirement = Requirement(EnrichmentRequirementKind.Identity, "igdb", (EnrichmentRequirementState)99, "Discover", null);

        Should.Throw<ArgumentOutOfRangeException>(() => EnrichmentRequirementMapping.ToContract(requirement));
    }

    [Fact]
    public void ToContract_ShuffledRequirements_ReturnsCanonicalOrder()
    {
        var requirements = new[]
        {
            Requirement(EnrichmentRequirementKind.Operation, "Processing", EnrichmentRequirementState.Satisfied, null, "Idle"),
            Requirement(EnrichmentRequirementKind.Artwork, "Logo", EnrichmentRequirementState.Satisfied, null, "Present"),
            Requirement(EnrichmentRequirementKind.Metadata, "Refresh", EnrichmentRequirementState.Satisfied, null, "Current"),
            Requirement(EnrichmentRequirementKind.Identity, "zeta", EnrichmentRequirementState.Automatic, "Discover", null),
            Requirement(EnrichmentRequirementKind.Artwork, "Hero", EnrichmentRequirementState.Satisfied, null, "Present"),
            Requirement(EnrichmentRequirementKind.Metadata, "ReleaseDate", EnrichmentRequirementState.Satisfied, null, "Present"),
            Requirement(EnrichmentRequirementKind.Identity, "steamgriddb", EnrichmentRequirementState.Automatic, "Discover", null),
            Requirement(EnrichmentRequirementKind.Artwork, "Backdrop", EnrichmentRequirementState.Satisfied, null, "Present"),
            Requirement(EnrichmentRequirementKind.Metadata, "Genre", EnrichmentRequirementState.Satisfied, null, "Present"),
            Requirement(EnrichmentRequirementKind.Identity, "alpha", EnrichmentRequirementState.Automatic, "Discover", null),
            Requirement(EnrichmentRequirementKind.Artwork, "Poster", EnrichmentRequirementState.Satisfied, null, "Present"),
            Requirement(EnrichmentRequirementKind.Metadata, "Description", EnrichmentRequirementState.Satisfied, null, "Present"),
            Requirement(EnrichmentRequirementKind.Identity, "igdb", EnrichmentRequirementState.Automatic, "Discover", null)
        };

        var result = EnrichmentRequirementMapping.ToContract(requirements);

        result.Select(x => x.Key).ShouldBe([
            "Identity:igdb", "Identity:steamgriddb", "Identity:alpha", "Identity:zeta",
            "Metadata:Description", "Metadata:Genre", "Metadata:ReleaseDate", "Metadata:Refresh",
            "Artwork:Poster", "Artwork:Backdrop", "Artwork:Hero", "Artwork:Logo",
            "Operation:Processing"]);
    }

    [Fact]
    public void DomainVocabularies_MatchContractNamesInDeclarationOrder()
    {
        Enum.GetNames<Romd.Domain.Catalog.EnrichmentRequirementCause>().ShouldBe(Enum.GetNames<Cause>());
        Enum.GetNames<Romd.Domain.Catalog.EnrichmentRequirementSatisfiedBy>().ShouldBe(Enum.GetNames<SatisfiedBy>());
        Enum.GetNames<EnrichmentRequirementState>().ShouldBe(Enum.GetNames<EnrichmentIssueState>());
    }

    private static EnrichmentRequirement Requirement(EnrichmentRequirementKind kind, string subject,
        EnrichmentRequirementState state, string? cause, string? satisfiedBy) =>
        new() { TitleId = 1, Kind = kind, Subject = subject, State = state, Cause = cause, SatisfiedBy = satisfiedBy };
}
