using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.MetadataProviders;
using Romd.Application.Common.Concurrency;
using Romd.Contracts.Management.Enrichment;
using Romd.Contracts.Common.Models;
using Romd.Application.Common.Pagination;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Tests.Enrichment.Helpers;
using Romd.Persistence.Entities;
using Romd.Persistence.Enrichment;
using Romd.Persistence.MetadataProviders;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;
using Cause = Romd.Contracts.Management.Enrichment.EnrichmentRequirementCause;
using SatisfiedBy = Romd.Contracts.Management.Enrichment.EnrichmentRequirementSatisfiedBy;
using State = Romd.Contracts.Management.Enrichment.EnrichmentIssueState;

namespace Romd.Infrastructure.Tests.Enrichment;

/// <summary>
///     Covers each cause and satisfaction the schema-43 facts function emits (plan section 3), overlapping facts
///     that decide precedence, and the assessment-level preserved behaviors of plan section 6. Every read also
///     checks every seeded title against the independent requirement oracle.
/// </summary>
public sealed class EnrichmentRequirementCauseTests : IDisposable
{
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();

    public void Dispose() => database.Dispose();

    private static IProviderIdentityAdapter[] Igdb => [Provider("igdb")];
    private static IProviderIdentityAdapter[] Both => [Provider("igdb"), Provider("steamgriddb")];

    [Fact]
    public async Task Assess_IdentityFreshNeedsReviewDecision_RequiresMatchDecision()
    {
        await SeedAsync();
        await DecideAsync("igdb", "NeedsReview");

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Identity:igdb", State.NeedsReview, Cause.MatchDecisionRequired, "ReviewProvider", "ContinueWithoutProvider");
    }

    [Fact]
    public async Task Assess_IdentityFreshNoMatchDecision_ReportsNoMatchFound()
    {
        await SeedAsync();
        await DecideAsync("igdb", "NoMatch");

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Identity:igdb", State.NeedsReview, Cause.NoMatchFound, "ReviewProvider", "ContinueWithoutProvider");
    }

    [Fact]
    public async Task Assess_IdentityEnabledWithoutCredentials_IsBlockedUnconfigured()
    {
        await SeedAsync();

        var assessment = await AssessAsync(Provider("igdb", configured: false));

        Open(assessment, "Identity:igdb", State.Blocked, Cause.ProviderUnconfigured, "ConfigureProvider");
    }

    [Fact]
    public async Task Assess_IdentityDisabledWithMissingCoreMetadata_IsBlockedDisabled()
    {
        await SeedAsync();

        var assessment = await AssessAsync(Provider("igdb", enabled: false));

        Open(assessment, "Identity:igdb", State.Blocked, Cause.ProviderDisabled, "ConfigureProvider");
        Open(assessment, "Metadata:Description", State.Blocked, Cause.ProviderDisabled, "ConfigureProvider");
    }

    [Fact]
    public async Task Assess_IdentityDisabledForUnservedLogo_IsBlockedDisabled()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(logo: true);

        var assessment = await AssessAsync(Provider("igdb"), Provider("steamgriddb", enabled: false));

        Open(assessment, "Identity:steamgriddb", State.Blocked, Cause.ProviderDisabled, "ConfigureProvider");
        Open(assessment, "Artwork:Logo", State.Blocked, Cause.ProviderDisabled, "ConfigureProvider");
        assessment.Requirements.ShouldNotContain(x => x.Key == "Identity:igdb");
    }

    [Fact]
    public async Task Assess_IdentityDisabledWithActiveEnrichment_IsWorking()
    {
        await SeedAsync();
        await ActiveEnrichmentAsync();

        var assessment = await AssessAsync(Provider("igdb", enabled: false));

        Open(assessment, "Identity:igdb", State.Processing, Cause.Working);
        Open(assessment, "Metadata:Description", State.Processing, Cause.Working);
    }

    [Fact]
    public async Task Assess_IdentityDisabledForUnservedLogoWithPendingImport_IsWorkingWithoutBlocker()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(logo: true);
        await PendingImportAsync(ArtworkRole.Logo);

        var assessment = await AssessAsync(Provider("igdb"), Provider("steamgriddb", enabled: false));

        Open(assessment, "Identity:steamgriddb", State.Processing, Cause.Working);
        Open(assessment, "Artwork:Logo", State.Processing, Cause.Importing);
        assessment.HasOperationalBlocker.ShouldBeFalse();
    }

    [Fact]
    public async Task Assess_DisabledProviderForLogoFallback_NamesNoProvider()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(logo: true);
        await OutcomeAsync(ArtworkRole.Logo, "Unavailable");

        var assessment = await AssessAsync(Provider("igdb"), Provider("steamgriddb", enabled: false));

        assessment.Requirements.ShouldNotContain(x => x.Kind == "Identity");
        Satisfied(assessment, "Artwork:Logo", SatisfiedBy.UnavailableFallback, "ReviewArtwork");
        assessment.HasOperationalBlocker.ShouldBeFalse();
    }

    [Fact]
    public async Task Assess_DisabledProviderForUnavailableLogoWithPendingImport_ImportsWithoutNamingProvider()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(logo: true);
        await OutcomeAsync(ArtworkRole.Logo, "Unavailable");
        await PendingImportAsync(ArtworkRole.Logo);

        var assessment = await AssessAsync(Provider("igdb"), Provider("steamgriddb", enabled: false));

        assessment.Requirements.ShouldNotContain(x => x.Kind == "Identity");
        Open(assessment, "Artwork:Logo", State.Processing, Cause.Importing);
        assessment.HasOperationalBlocker.ShouldBeFalse();
    }

    [Fact]
    public async Task Assess_DisabledProviderWithOnlyStoredEvidence_NamesNoProvider()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(poster: true);
        await AssetAsync(ArtworkRole.Poster, "igdb");
        await LayerAsync("42");

        var assessment = await AssessAsync(Provider("igdb", enabled: false));

        assessment.Requirements.ShouldNotContain(x => x.Kind == "Identity");
        Satisfied(assessment, "Artwork:Poster", SatisfiedBy.Present, "ReviewArtwork");
    }

    [Fact]
    public async Task Assess_UnregisteredMetadataProvider_BlocksFieldsWithoutNamingIt()
    {
        await SeedAsync();

        var assessment = await AssessAsync(Provider("steamgriddb"));

        Open(assessment, "Metadata:Description", State.Blocked, Cause.ProviderDisabled, "ConfigureProvider");
        assessment.Requirements.ShouldNotContain(x => x.Kind == "Identity");
    }

    [Fact]
    public async Task Assess_IdentityFreshUnavailableDecision_IsBlockedUnavailableWithRetry()
    {
        await SeedAsync();
        await DecideAsync("igdb", "Unavailable");

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Identity:igdb", State.Blocked, Cause.ProviderUnavailable, "Retry");
    }

    [Fact]
    public async Task Assess_IdentityWithoutEvidence_DiscoversOnNextEnrichment()
    {
        await SeedAsync();

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Identity:igdb", State.Automatic, Cause.Discover, "Enrich");
    }

    [Fact]
    public async Task Assess_IdentityWithActiveEnrichment_IsWorking()
    {
        await SeedAsync();
        await ActiveEnrichmentAsync();

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Identity:igdb", State.Processing, Cause.Working);
    }

    [Fact]
    public async Task Assess_IdentityConfirmed_IsSatisfiedByTrust()
    {
        await SeedAsync();
        await ConfirmAsync("igdb");

        var assessment = await AssessAsync(Igdb);

        Satisfied(assessment, "Identity:igdb", SatisfiedBy.Trusted, "ReviewProvider");
    }

    [Fact]
    public async Task Assess_IdentitySuppressed_IsSatisfiedBySuppression()
    {
        await SeedAsync();
        await SuppressAsync("igdb");

        var assessment = await AssessAsync(Igdb);

        Satisfied(assessment, "Identity:igdb", SatisfiedBy.ProviderSuppressed, "ReviewProvider");
    }

    [Fact]
    public async Task Assess_MetadataMissingAfterCurrentAttempt_IsNotProvided()
    {
        await SeedAsync();
        await SetMetadataAsync(genre: "Adventure", releaseDate: new DateOnly(1995, 3, 11), attempted: true);
        await ConfirmAsync("igdb");
        await LayerAsync("42");

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Metadata:Description", State.NeedsReview, Cause.NotProvided, "ReviewMetadata", "AcceptOmission");
        Satisfied(assessment, "Metadata:Genre", SatisfiedBy.Present, "ReviewMetadata");
        Satisfied(assessment, "Metadata:Refresh", SatisfiedBy.Current);
    }

    [Fact]
    public async Task Assess_MetadataWithSuppressedProvider_NeedsReviewForSuppression()
    {
        await SeedAsync();
        await SuppressAsync("igdb");

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Metadata:Description", State.NeedsReview, Cause.ProviderSuppressed, "ReviewMetadata", "AcceptOmission", "ReviewProvider");
    }

    [Fact]
    public async Task Assess_MetadataWithUnconfiguredProvider_IsBlockedUnconfigured()
    {
        await SeedAsync();

        var assessment = await AssessAsync(Provider("igdb", configured: false));

        Open(assessment, "Metadata:Description", State.Blocked, Cause.ProviderUnconfigured, "ConfigureProvider");
    }

    [Fact]
    public async Task Assess_MetadataWithUnavailableIdentityDecision_IsBlockedUnavailableWithRetry()
    {
        await SeedAsync();
        await DecideAsync("igdb", "Unavailable");

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Metadata:Description", State.Blocked, Cause.ProviderUnavailable, "Retry");
    }

    [Fact]
    public async Task Assess_MetadataNeverFetched_IsNotAttemptedAndRefreshIsStale()
    {
        await SeedAsync();
        await ConfirmAsync("igdb");

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Metadata:Description", State.Automatic, Cause.NotAttempted, "Enrich");
        Open(assessment, "Metadata:Refresh", State.Automatic, Cause.Stale, "Enrich");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("99")]
    public async Task Assess_MetadataAttemptedWithoutCurrentMatchLayer_IsStale(string? previousMatchGameId)
    {
        await SeedAsync();
        await SetMetadataAsync(genre: "Adventure", releaseDate: new DateOnly(1995, 3, 11), attempted: true);
        await ConfirmAsync("igdb");
        if (previousMatchGameId is not null)
            await LayerAsync(previousMatchGameId);

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Metadata:Description", State.Automatic, Cause.Stale, "Enrich");
        Satisfied(assessment, "Metadata:Genre", SatisfiedBy.Present, "ReviewMetadata");
        Open(assessment, "Metadata:Refresh", State.Automatic, Cause.Stale, "Enrich");
    }

    [Fact]
    public async Task Assess_MetadataWithActiveEnrichment_IsWorking()
    {
        await SeedAsync();
        await ConfirmAsync("igdb");
        await ActiveEnrichmentAsync();

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Metadata:Description", State.Processing, Cause.Working);
        Open(assessment, "Metadata:Refresh", State.Processing, Cause.Working);
    }

    [Fact]
    public async Task Assess_MetadataAcceptedOmission_IsSatisfiedByOmission()
    {
        await SeedAsync();
        await SetMetadataAsync(genre: "Adventure", releaseDate: new DateOnly(1995, 3, 11));
        await OmitAsync("Description");

        var assessment = await AssessAsync(Igdb);

        Satisfied(assessment, "Metadata:Description", SatisfiedBy.AcceptedOmission, "ReviewMetadata");
        Satisfied(assessment, "Metadata:ReleaseDate", SatisfiedBy.Present, "ReviewMetadata");
    }

    [Theory]
    [InlineData("NeedsReview")]
    [InlineData("NoMatch")]
    public async Task Assess_MetadataWhileIdentityReviewWaits_HidesFieldsSoAwaitingIdentityIsUnreachable(string decision)
    {
        await SeedAsync();
        await DecideAsync("igdb", decision);

        var assessment = await AssessAsync(Igdb);

        assessment.Requirements.Where(x => x.Kind == "Metadata").Select(x => x.Subject).ShouldBe(["Refresh"]);
        assessment.Requirements.ShouldNotContain(x => x.Cause == Cause.AwaitingIdentity);
        assessment.HasOperationalBlocker.ShouldBeFalse();
    }

    [Theory]
    [InlineData("NeedsReview")]
    [InlineData("NoMatch")]
    public async Task Assess_ArtworkWhileIdentityReviewWaits_HidesUntrustedRolesSoAwaitingIdentityIsUnreachable(string decision)
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(poster: true, backdrop: true);
        await ConfirmAsync("steamgriddb");
        await DecideAsync("igdb", decision);

        var assessment = await AssessAsync(Both);

        assessment.Requirements.ShouldNotContain(x => x.Key == "Artwork:Backdrop");
        Open(assessment, "Artwork:Poster", State.Automatic, Cause.NotAttempted, "Enrich");
        assessment.Requirements.ShouldNotContain(x => x.Cause == Cause.AwaitingIdentity);
    }

    [Fact]
    public async Task Assess_BackdropWithReviewPreference_RequiresArtworkDecision()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(backdrop: true, reviewBackdrops: true);
        await ConfirmAsync("igdb");

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Artwork:Backdrop", State.NeedsReview, Cause.ArtworkDecisionRequired, "ReviewArtwork");
    }

    [Fact]
    public async Task Assess_PosterWithRecordedNeedsReviewOutcome_RequiresArtworkDecision()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(poster: true);
        await ConfirmAsync("igdb");
        await OutcomeAsync(ArtworkRole.Poster, "NeedsReview");

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Artwork:Poster", State.NeedsReview, Cause.ArtworkDecisionRequired, "ReviewArtwork");
    }

    // Preserved behavior 7: Poster or Hero completed Unavailable still requires a decision.
    [Theory]
    [InlineData(ArtworkRole.Poster)]
    [InlineData(ArtworkRole.Hero)]
    public async Task Assess_PosterOrHeroUnavailable_NeedsDecisionBecauseNothingUsable(ArtworkRole role)
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(poster: role == ArtworkRole.Poster, hero: role == ArtworkRole.Hero);
        await ConfirmAsync("igdb");
        await OutcomeAsync(role, "Unavailable");

        var assessment = await AssessAsync(Igdb);

        Open(assessment, $"Artwork:{role}", State.NeedsReview, Cause.NothingUsable, "ReviewArtwork", "LeaveEmpty");
        assessment.NeedsReview.ShouldBeTrue();
    }

    [Fact]
    public async Task Assess_ArtworkWithSuppressedProvider_NeedsReviewForSuppression()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(poster: true);
        await SuppressAsync("igdb");

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Artwork:Poster", State.NeedsReview, Cause.ProviderSuppressed, "ReviewArtwork", "LeaveEmpty", "ReviewProvider");
    }

    [Fact]
    public async Task Assess_ArtworkWithUnconfiguredProvider_IsBlockedUnconfigured()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(poster: true);

        var assessment = await AssessAsync(Provider("igdb", configured: false));

        Open(assessment, "Artwork:Poster", State.Blocked, Cause.ProviderUnconfigured, "ConfigureProvider");
    }

    [Fact]
    public async Task Assess_ArtworkRoleWithoutEnabledProvider_IsBlockedDisabled()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(logo: true);

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Artwork:Logo", State.Blocked, Cause.ProviderDisabled, "ConfigureProvider");
    }

    [Fact]
    public async Task Assess_ArtworkWithUnavailableIdentityDecision_IsBlockedUnavailableWithRetry()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(poster: true);
        await DecideAsync("igdb", "Unavailable");

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Artwork:Poster", State.Blocked, Cause.ProviderUnavailable, "Retry");
    }

    // Preserved behavior 5: acquisition Failed and identity Unavailable render Blocked with Retry.
    [Fact]
    public async Task Assess_ArtworkAcquisitionFailed_IsBlockedWithRetry()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(hero: true);
        await ConfirmAsync("igdb");
        await OutcomeAsync(ArtworkRole.Hero, "Failed");

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Artwork:Hero", State.Blocked, Cause.AcquisitionFailed, "Retry");
    }

    [Fact]
    public async Task Assess_ArtworkNotYetSearched_IsNotAttempted()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(poster: true);
        await ConfirmAsync("igdb");

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Artwork:Poster", State.Automatic, Cause.NotAttempted, "Enrich");
    }

    [Fact]
    public async Task Assess_ArtworkImportPending_IsImporting()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(backdrop: true);
        await ConfirmAsync("igdb");
        await PendingImportAsync(ArtworkRole.Backdrop);

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Artwork:Backdrop", State.Processing, Cause.Importing);
        Open(assessment, "Operation:Processing", State.Processing, Cause.Working);
    }

    [Fact]
    public async Task Assess_ArtworkWithActiveEnrichment_IsSearching()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(poster: true);
        await ConfirmAsync("igdb");
        await ActiveEnrichmentAsync();

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Artwork:Poster", State.Processing, Cause.Searching);
    }

    [Fact]
    public async Task Assess_ArtworkSettledRoles_ReportPresentLeftEmptyNotRequestedAndFallback()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(poster: true, logo: true, backdrop: true);
        await AssetAsync(ArtworkRole.Poster, "igdb");
        await EmptySelectionAsync(ArtworkRole.Backdrop);
        await OutcomeAsync(ArtworkRole.Logo, "Unavailable");
        await ConfirmAsync("igdb");
        await ConfirmAsync("steamgriddb");

        var assessment = await AssessAsync(Both);

        Satisfied(assessment, "Artwork:Poster", SatisfiedBy.Present, "ReviewArtwork");
        Satisfied(assessment, "Artwork:Backdrop", SatisfiedBy.LeftEmpty, "ReviewArtwork");
        Satisfied(assessment, "Artwork:Hero", SatisfiedBy.NotRequested, "ReviewArtwork");
        Satisfied(assessment, "Artwork:Logo", SatisfiedBy.UnavailableFallback, "ReviewArtwork");
    }

    [Fact]
    public async Task Assess_Operation_IsIdleUntilWorkStarts()
    {
        await SeedAsync(completeMetadata: true);
        Satisfied(await AssessAsync(Igdb), "Operation:Processing", SatisfiedBy.Idle);

        await ActiveEnrichmentAsync();

        Open(await AssessAsync(Igdb), "Operation:Processing", State.Processing, Cause.Working);
    }

    [Fact]
    public async Task Assess_EmptySelectionWithPresentAsset_LeftEmptyWins()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(poster: true);
        await AssetAsync(ArtworkRole.Poster, "igdb");
        await EmptySelectionAsync(ArtworkRole.Poster);

        var assessment = await AssessAsync(Igdb);

        Satisfied(assessment, "Artwork:Poster", SatisfiedBy.LeftEmpty, "ReviewArtwork");
    }

    [Fact]
    public async Task Assess_OmissionForLaterPopulatedField_AcceptedOmissionWins()
    {
        await SeedAsync();
        await OmitAsync("Description");
        await SetMetadataAsync(description: "Written later", genre: "Adventure", releaseDate: new DateOnly(1995, 3, 11));

        var assessment = await AssessAsync(Igdb);

        Satisfied(assessment, "Metadata:Description", SatisfiedBy.AcceptedOmission, "ReviewMetadata");
    }

    [Fact]
    public async Task Assess_PresentAssetForUnrequestedRole_PresentWins()
    {
        await SeedAsync(completeMetadata: true);
        await AssetAsync(ArtworkRole.Hero, "igdb");

        var assessment = await AssessAsync(Igdb);

        Satisfied(assessment, "Artwork:Hero", SatisfiedBy.Present, "ReviewArtwork");
    }

    [Fact]
    public async Task Assess_UnavailableOutcomeForUnrequestedRole_NotRequestedWins()
    {
        await SeedAsync(completeMetadata: true);
        await OutcomeAsync(ArtworkRole.Logo, "Unavailable");

        var assessment = await AssessAsync(Both);

        Satisfied(assessment, "Artwork:Logo", SatisfiedBy.NotRequested, "ReviewArtwork");
    }

    [Fact]
    public async Task Assess_SuppressedConfirmedIdentity_SuppressionWinsOverTrust()
    {
        await SeedAsync();
        await ConfirmAsync("igdb");
        await SuppressAsync("igdb");

        var assessment = await AssessAsync(Igdb);

        Satisfied(assessment, "Identity:igdb", SatisfiedBy.ProviderSuppressed, "ReviewProvider");
    }

    [Fact]
    public async Task Assess_BackdropReviewPreferenceWithFailedOutcome_DecisionWinsOverFailure()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(backdrop: true, reviewBackdrops: true);
        await ConfirmAsync("igdb");
        await OutcomeAsync(ArtworkRole.Backdrop, "Failed");

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Artwork:Backdrop", State.NeedsReview, Cause.ArtworkDecisionRequired, "ReviewArtwork");
    }

    [Fact]
    public async Task Assess_UnconfiguredProviderWithActiveWork_ProcessingWinsOverBlocker()
    {
        await SeedAsync();
        await ActiveEnrichmentAsync();

        var assessment = await AssessAsync(Provider("igdb", configured: false));

        Open(assessment, "Identity:igdb", State.Processing, Cause.Working);
        Open(assessment, "Metadata:Description", State.Processing, Cause.Working);
        assessment.HasOperationalBlocker.ShouldBeFalse();
    }

    [Fact]
    public async Task Assess_PendingImportForUnavailableBackdrop_ImportingWinsOverFallback()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(backdrop: true);
        await ConfirmAsync("igdb");
        await OutcomeAsync(ArtworkRole.Backdrop, "Unavailable");
        await PendingImportAsync(ArtworkRole.Backdrop);

        var assessment = await AssessAsync(Igdb);

        Open(assessment, "Artwork:Backdrop", State.Processing, Cause.Importing);
    }

    // Preserved behavior 1: a completed logo search with nothing usable is settled, not review work.
    [Fact]
    public async Task Assess_LogoUnavailable_IsFallbackOutsideNeedsReviewView()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(logo: true);
        await OutcomeAsync(ArtworkRole.Logo, "Unavailable");
        await ConfirmAsync("steamgriddb");

        var assessment = await AssessAsync(Both);

        Satisfied(assessment, "Artwork:Logo", SatisfiedBy.UnavailableFallback, "ReviewArtwork");
        assessment.NeedsReview.ShouldBeFalse();
        (await ReadViewAsync("NeedsReview", Both)).Items.ShouldBeEmpty();
    }

    // Preserved behavior 2: backdrop Unavailable settles regardless of preference; a NeedsReview outcome under
    // review preference still requires a decision.
    [Theory]
    [InlineData(false, "Unavailable", State.Satisfied)]
    [InlineData(true, "Unavailable", State.Satisfied)]
    [InlineData(true, "NeedsReview", State.NeedsReview)]
    public async Task Assess_BackdropOutcome_SettlesUnavailableAndKeepsReviewDecisions(bool review, string outcome, State expected)
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(backdrop: true, reviewBackdrops: review);
        await ConfirmAsync("igdb");
        await OutcomeAsync(ArtworkRole.Backdrop, outcome);

        var assessment = await AssessAsync(Igdb);

        if (expected == State.Satisfied)
            Satisfied(assessment, "Artwork:Backdrop", SatisfiedBy.UnavailableFallback, "ReviewArtwork");
        else
            Open(assessment, "Artwork:Backdrop", State.NeedsReview, Cause.ArtworkDecisionRequired, "ReviewArtwork");
    }

    // Preserved behavior 3: an explicit empty selection settles the role and creates no automatic demand.
    [Fact]
    public async Task Assess_EmptySelection_IsLeftEmptyWithoutAutomaticDemand()
    {
        await SeedAsync(completeMetadata: true);
        await ArtworkAsync(logo: true);
        await EmptySelectionAsync(ArtworkRole.Logo);

        var assessment = await AssessAsync(Both);

        Satisfied(assessment, "Artwork:Logo", SatisfiedBy.LeftEmpty, "ReviewArtwork");
        assessment.Requirements.ShouldNotContain(x => x.Kind == "Identity");
        assessment.CanEnrichAutomatically.ShouldBeFalse();
    }

    // Preserved behavior 4: an accepted omission settles the field until the curator revisits it.
    [Fact]
    public async Task SetOmission_AcceptThenRevisit_SettlesAndReopensField()
    {
        await SeedAsync();
        await SetMetadataAsync(genre: "Adventure", releaseDate: new DateOnly(1995, 3, 11), attempted: true);
        await ConfirmAsync("igdb");
        await LayerAsync("42");
        await using var db = database.CreateContext();
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: Igdb);
        var before = (await workbench.GetAsync(1, default)).Value;

        (await workbench.SetCommittedAsync(db, 1, "Description", true, RevisionPrecondition.For(before.Revision.ToString())))
            .IsError.ShouldBeFalse();
        var accepted = (await workbench.GetAsync(1, default)).Value;
        (await workbench.SetCommittedAsync(db, 1, "Description", false, RevisionPrecondition.For(accepted.Revision.ToString())))
            .IsError.ShouldBeFalse();
        var revisited = (await workbench.GetAsync(1, default)).Value;

        Open(before, "Metadata:Description", State.NeedsReview, Cause.NotProvided, "ReviewMetadata", "AcceptOmission");
        Satisfied(accepted, "Metadata:Description", SatisfiedBy.AcceptedOmission, "ReviewMetadata");
        Open(revisited, "Metadata:Description", State.NeedsReview, Cause.NotProvided, "ReviewMetadata", "AcceptOmission");
        EnrichmentRequirementInvariants.AssertWellFormed(accepted);
        EnrichmentRequirementInvariants.AssertWellFormed(revisited);
    }

    // Preserved behavior 6: only Description, Genre and ReleaseDate create metadata rows, plus Refresh.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assess_OptionalMetadataMissing_CreatesOnlyCoreFieldRows(bool completeCore)
    {
        await SeedAsync(completeMetadata: completeCore);

        var assessment = await AssessAsync(Igdb);

        assessment.Requirements.Where(x => x.Kind == "Metadata").Select(x => x.Subject)
            .ShouldBe(["Description", "Genre", "ReleaseDate", "Refresh"]);
        if (completeCore)
            assessment.Requirements.Where(x => x.Kind == "Metadata").ShouldAllBe(x => x.State == State.Satisfied);
    }

    private static void Open(EnrichmentAssessmentDto assessment, string key, State state, Cause cause, params string[] actions)
    {
        var row = assessment.Requirements.Where(x => x.Key == key).ShouldHaveSingleItem(key);
        (row.State, row.Cause, row.SatisfiedBy).ShouldBe((state, (Cause?)cause, (SatisfiedBy?)null), key);
        string.Join(",", row.Actions).ShouldBe(string.Join(",", actions), key);
    }

    private static void Satisfied(EnrichmentAssessmentDto assessment, string key, SatisfiedBy satisfiedBy, params string[] actions)
    {
        var row = assessment.Requirements.Where(x => x.Key == key).ShouldHaveSingleItem(key);
        (row.State, row.Cause, row.SatisfiedBy).ShouldBe((State.Satisfied, (Cause?)null, (SatisfiedBy?)satisfiedBy), key);
        string.Join(",", row.Actions).ShouldBe(string.Join(",", actions), key);
    }

    private static IProviderIdentityAdapter Provider(string id, bool enabled = true, bool configured = true)
    {
        var adapter = Substitute.For<IProviderIdentityAdapter>();
        adapter.Id.Returns(id);
        adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(enabled, configured));
        return adapter;
    }

    private async Task<EnrichmentAssessmentDto> AssessAsync(params IProviderIdentityAdapter[] adapters)
    {
        var page = await ReadViewAsync("All", adapters);
        page.Items.ShouldNotBeEmpty();
        foreach (var item in page.Items)
            EnrichmentRequirementInvariants.AssertWellFormed(item);
        await using var db = database.CreateContext();
        var detail = (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: adapters).GetAsync(1, default)).Value;
        Shape(detail).ShouldBe(Shape(page.Items.Single(x => x.TitleId == detail.TitleId)));
        return detail;
    }

    private static IEnumerable<string> Shape(EnrichmentAssessmentDto assessment) =>
        assessment.Requirements.Select(x => $"{x.Key} {x.State} {x.Cause} {x.SatisfiedBy} {string.Join(",", x.Actions)}");

    private async Task<Page<EnrichmentAssessmentDto>> ReadViewAsync(string view, IProviderIdentityAdapter[] adapters)
    {
        await using var db = database.CreateContext();
        return (await new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: adapters)
            .ReadAsync(view, null, 100, null, default)).Value;
    }

    private async Task SeedAsync(bool completeMetadata = false)
    {
        await using var db = database.CreateContext();
        db.Platforms.Add(new() { Id = 1, Name = "System", ShortName = "system" });
        db.Files.Add(new FileEntityPersistence { Id = 1, Sha256 = Sha256.FromSpan(Enumerable.Repeat((byte)1, 32).ToArray()) });
        db.Titles.Add(new() { Id = 1, PlatformId = 1, Name = "Title 1", NormalizedName = "TITLE 1", EnrichmentStatus = "None" });
        db.TrackedTitles.Add(new() { TitleId = 1 });
        await db.SaveChangesAsync();
        await ArtworkAsync();
        if (completeMetadata)
            await SetMetadataAsync(description: "Complete", genre: "Adventure", releaseDate: new DateOnly(1995, 3, 11));
    }

    private async Task SetMetadataAsync(string? description = null, string? genre = null, DateOnly? releaseDate = null, bool attempted = false)
    {
        DateTimeOffset? lastEnrichedAt = attempted ? DateTimeOffset.UtcNow : null;
        var status = attempted ? "Completed" : "None";
        await using var db = database.CreateContext();
        await db.Titles.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Description, description).SetProperty(x => x.Genre, genre).SetProperty(x => x.ReleaseDate, releaseDate)
            .SetProperty(x => x.EnrichmentStatus, status).SetProperty(x => x.LastEnrichedAt, lastEnrichedAt));
    }

    private async Task ArtworkAsync(bool poster = false, bool hero = false, bool logo = false, bool backdrop = false, bool reviewBackdrops = false)
    {
        await using var db = database.CreateContext();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(s => s.SetProperty(x => x.FillPosters, poster)
            .SetProperty(x => x.FillHeroes, hero).SetProperty(x => x.FillLogos, logo).SetProperty(x => x.FillBackdrops, backdrop)
            .SetProperty(x => x.ReviewBackdrops, reviewBackdrops));
    }

    private async Task ConfirmAsync(string provider)
    {
        await using var db = database.CreateContext();
        db.TitleExternalIds.Add(new() { TitleId = 1, Provider = provider, ExternalId = "42", IsConfirmed = true });
        await db.SaveChangesAsync();
    }

    // The decision fingerprint covers confirmed identities, so decisions are seeded after confirmations.
    private async Task DecideAsync(string provider, string decision)
    {
        await using var db = database.CreateContext();
        var fingerprint = await db.Titles.Where(x => x.Id == 1)
            .Select(x => ProviderIdentityDatabaseFunctions.Fingerprint(x.Id, ProviderIdentityPolicy.Version)).SingleAsync();
        db.Add(new TitleProviderMatchStateEntity
        {
            TitleId = 1,
            ProviderId = provider,
            MatcherVersion = ProviderIdentityPolicy.Version,
            InputFingerprint = fingerprint,
            DecisionJson = $$"""{"State":"{{decision}}","Candidates":[]}"""
        });
        await db.SaveChangesAsync();
    }

    private async Task SuppressAsync(string provider)
    {
        await using var db = database.CreateContext();
        db.Add(new TitleProviderMatchStateEntity { TitleId = 1, ProviderId = provider, SuppressAutomaticMatch = true });
        await db.SaveChangesAsync();
    }

    private async Task ActiveEnrichmentAsync()
    {
        await using var db = database.CreateContext();
        db.Add(new EnrichmentJobEntity
        {
            Id = Guid.NewGuid(),
            TitleId = 1,
            PlatformId = 1,
            Phase = "Enriching",
            SourceFilename = "Title 1",
            ErrorsJson = "[]",
            JobType = "enrichment"
        });
        await db.SaveChangesAsync();
    }

    private async Task PendingImportAsync(ArtworkRole role)
    {
        await using var db = database.CreateContext();
        var id = Guid.NewGuid();
        var job = ArtworkImportJobEntity.FromDomain(ArtworkImportJob.Create(id, "Title 1", 1, 1, role, 1, "igdb", "42", "asset",
            "https://example.com/image", null, TimeProvider.System.GetUtcNow()));
        job.Phase = "Importing";
        db.Add(job);
        var selection = await db.ArtworkSelections.AsTracking().SingleOrDefaultAsync(x => x.TitleId == 1 && x.Role == role);
        if (selection is null)
            db.ArtworkSelections.Add(new() { TitleId = 1, Role = role, Mode = ArtworkSelectionMode.Automatic, Revision = 1, PendingRequestId = id });
        else
            selection.PendingRequestId = id;
        await db.SaveChangesAsync();
    }

    private async Task EmptySelectionAsync(ArtworkRole role)
    {
        await using var db = database.CreateContext();
        db.ArtworkSelections.Add(new() { TitleId = 1, Role = role, Mode = ArtworkSelectionMode.Empty });
        await db.SaveChangesAsync();
    }

    private async Task OutcomeAsync(ArtworkRole role, string status)
    {
        await using var db = database.CreateContext();
        db.Set<ArtworkAcquisitionEntity>().Add(new() { TitleId = 1, Role = role, Status = status });
        await db.SaveChangesAsync();
    }

    private async Task AssetAsync(ArtworkRole role, string source)
    {
        await using var db = database.CreateContext();
        db.ArtworkAssets.Add(new ArtworkAssetEntity
        {
            TitleId = 1,
            Role = role,
            SourceId = source,
            OriginalFileId = 1,
            ContentVersion = "original",
            ContentType = "image/webp",
            Width = 600,
            Height = 900,
            IsEligible = true,
            Variants = [new() { Name = "small", FileId = 1, ContentVersion = "small-v1", ContentType = "image/webp", Width = 120, Height = 180 }]
        });
        await db.SaveChangesAsync();
    }

    private async Task OmitAsync(string field)
    {
        await using var db = database.CreateContext();
        db.Add(new EnrichmentOmissionEntity { TitleId = 1, Field = field, AcceptedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task LayerAsync(string providerGameId)
    {
        await using var db = database.CreateContext();
        db.TitleMetadataLayers.Add(new() { TitleId = 1, SourceId = "igdb", MetadataJson = $$"""{"providerGameId":"{{providerGameId}}"}""" });
        await db.SaveChangesAsync();
    }
}
