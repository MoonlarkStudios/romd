using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Libraries;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Libraries;

public sealed class MaterializedLibraryProjectionBuilderTests
{
    [Fact]
    public void Build_SystemOutsideScope_ExcludesAndExplainsConsistently()
    {
        var title = NewTitle(NewCandidate(1, 1, true, true));
        var config = new LibraryConfiguration { AllowedPlatformIds = [999] };
        MaterializedLibraryProjectionBuilder.Build(7, [title], config).Titles.ShouldBeEmpty();
        MaterializedLibraryProjectionBuilder.GetExclusionReason(title, config).ShouldBe("SystemScope");
    }

    [Fact]
    public void Explain_MissingPayloadAndNoLinkedRelease_AreDistinct()
    {
        var title = NewTitle(NewCandidate(1, 1, false, false));
        MaterializedLibraryProjectionBuilder.GetExclusionReason(title, new()).ShouldBe("MissingRom");
        MaterializedLibraryProjectionBuilder.GetExclusionReason(title with { Candidates = [] }, new()).ShouldBe("NoLinkedRelease");
        MaterializedLibraryProjectionBuilder.GetExclusionReason(title, new() { ShowMissingGames = true }).ShouldBeNull();
    }

    [Fact]
    public void Build_EligibleTitle_PreservesEligibleAndBlockedReleaseRows()
    {
        var title = NewTitle(
            NewCandidate(datGameId: 1, datFileId: 1, hasOwnedRoms: true, isComplete: true),
            NewCandidate(datGameId: 2, datFileId: 1, hasOwnedRoms: false, isComplete: false),
            NewCandidate(datGameId: 3, datFileId: 99, hasOwnedRoms: true, isComplete: true));
        var config = new LibraryConfiguration
        {
            ExcludedDatIds = [99],
            ShowMissingGames = true
        };

        var projection = MaterializedLibraryProjectionBuilder.Build(7, [title], config);

        var materializedTitle = projection.Titles.Single();
        materializedTitle.LibraryId.ShouldBe(7);
        materializedTitle.TitleId.ShouldBe(title.TitleId);
        materializedTitle.IsVisible.ShouldBeTrue();
        materializedTitle.IsOwned.ShouldBeTrue();
        materializedTitle.IsPlayable.ShouldBeTrue();
        materializedTitle.EligibleReleaseCount.ShouldBe(2);
        materializedTitle.PlayableReleaseCount.ShouldBe(1);
        materializedTitle.ExposedReleaseCount.ShouldBe(2);
        materializedTitle.Availability.ShouldBe(LibraryTitleAvailability.Playable);

        projection.Releases.Count.ShouldBe(3);
        projection.Releases.Count(release => release.IsExposed).ShouldBe(materializedTitle.ExposedReleaseCount);
        projection.Releases
            .Where(release => release.IsExposed)
            .ShouldAllBe(release => release.IsEligible && !release.IsBlocked);

        var playableRelease = projection.Releases.Single(release => release.DatGameId == 1);
        playableRelease.IsPlayable.ShouldBeTrue();
        playableRelease.IsComplete.ShouldBeTrue();
        playableRelease.IsExposed.ShouldBeTrue();
        playableRelease.ExposureReason.ShouldBe("ExposedDefault");

        var incompleteRelease = projection.Releases.Single(release => release.DatGameId == 2);
        incompleteRelease.IsEligible.ShouldBeTrue();
        incompleteRelease.IsComplete.ShouldBeFalse();
        incompleteRelease.IsPlayable.ShouldBeFalse();
        incompleteRelease.IsExposed.ShouldBeTrue();
        incompleteRelease.ExposureReason.ShouldBe("ExposedDefault");

        var blockedRelease = projection.Releases.Single(release => release.DatGameId == 3);
        blockedRelease.IsEligible.ShouldBeFalse();
        blockedRelease.IsBlocked.ShouldBeTrue();
        blockedRelease.IsExposed.ShouldBeFalse();
        blockedRelease.BlockReason.ShouldBe("ExcludedDat");
        blockedRelease.ExposureReason.ShouldBe("ExcludedDat");
    }

    [Fact]
    public void Build_IneligibleMissingRelease_IsNotExposedAndHasExposureReason()
    {
        var title = NewTitle(
            NewCandidate(datGameId: 1, datFileId: 1, hasOwnedRoms: true, isComplete: true),
            NewCandidate(datGameId: 2, datFileId: 1, hasOwnedRoms: false, isComplete: false));
        var config = new LibraryConfiguration
        {
            ShowMissingGames = false
        };

        var projection = MaterializedLibraryProjectionBuilder.Build(7, [title], config);

        var missingRelease = projection.Releases.Single(release => release.DatGameId == 2);
        missingRelease.IsEligible.ShouldBeFalse();
        missingRelease.IsBlocked.ShouldBeTrue();
        missingRelease.IsExposed.ShouldBeFalse();
        missingRelease.BlockReason.ShouldBe("MissingRom");
        missingRelease.ExposureReason.ShouldBe("MissingRom");
    }

    [Fact]
    public void Build_DuplicateReleaseIdentity_CollapsesToSingleRelease()
    {
        var title = NewTitle(
            NewCandidate(datGameId: 1, catalogReleaseId: 10, datFileId: 1, hasOwnedRoms: true, isComplete: true),
            NewCandidate(datGameId: 2, catalogReleaseId: 10, datFileId: 2, hasOwnedRoms: true, isComplete: true));
        var config = new LibraryConfiguration { ShowMissingGames = true };

        var projection = MaterializedLibraryProjectionBuilder.Build(7, [title], config);

        projection.Releases.Count.ShouldBe(1);
        projection.Releases.Single().DatGameId.ShouldBe(1);
        projection.Titles.Single().EligibleReleaseCount.ShouldBe(1);
        projection.Titles.Single().PlayableReleaseCount.ShouldBe(1);
    }

    [Fact]
    public void Build_DuplicateReleaseIdentity_ExcludedOnlyWhenAllSourceDatsExcluded()
    {
        var title = NewTitle(
            NewCandidate(datGameId: 1, catalogReleaseId: 10, datFileId: 1, hasOwnedRoms: true, isComplete: true),
            NewCandidate(datGameId: 2, catalogReleaseId: 10, datFileId: 99, hasOwnedRoms: true, isComplete: true),
            NewCandidate(datGameId: 3, catalogReleaseId: 20, datFileId: 2, hasOwnedRoms: true, isComplete: true));

        var partiallyExcluded = MaterializedLibraryProjectionBuilder.Build(
            7,
            [title],
            new LibraryConfiguration
            {
                ExcludedDatIds = [99],
                ShowMissingGames = true
            });

        var partiallyExcludedRelease = partiallyExcluded.Releases.Single(release => release.DatGameId == 1);
        partiallyExcludedRelease.IsExposed.ShouldBeTrue();
        partiallyExcludedRelease.BlockReason.ShouldBeNull();

        var fullyExcluded = MaterializedLibraryProjectionBuilder.Build(
            7,
            [title],
            new LibraryConfiguration
            {
                ExcludedDatIds = [1, 99],
                ShowMissingGames = true
            });

        var fullyExcludedRelease = fullyExcluded.Releases.Single(release => release.DatGameId == 1);
        fullyExcludedRelease.IsExposed.ShouldBeFalse();
        fullyExcludedRelease.BlockReason.ShouldBe("ExcludedDat");
    }

    [Fact]
    public void Build_UnknownRatingDefaultPolicy_HidesTitle()
    {
        var title = NewTitle(
            [],
            NewCandidate(datGameId: 1, datFileId: 1, hasOwnedRoms: true, isComplete: true));

        var projection = MaterializedLibraryProjectionBuilder.Build(7, [title], new LibraryConfiguration());

        projection.Titles.ShouldBeEmpty();
        projection.Releases.ShouldBeEmpty();
    }

    [Fact]
    public void Build_IncludeTitleIds_DoesNotBypassContentRatingPolicy()
    {
        var title = NewTitle(
            100,
            [Rating(RatingBoard.Esrb, "M", 17)],
            NewCandidate(datGameId: 1, datFileId: 1, hasOwnedRoms: true, isComplete: true));
        var config = new LibraryConfiguration
        {
            IncludeTitleIds = [100],
            AllowedGenres = ["Puzzle"],
            ContentRatingPolicy = new ContentRatingPolicy { MaxMinimumAge = 12 }
        };

        var projection = MaterializedLibraryProjectionBuilder.Build(7, [title], config);

        projection.Titles.ShouldBeEmpty();
        projection.Releases.ShouldBeEmpty();
    }

    [Fact]
    public void Build_IncludeTitleIds_BypassesCurationFiltersAfterRatingGate()
    {
        var title = NewTitle(
            100,
            [Rating(RatingBoard.Esrb, "E", 0)],
            NewCandidate(datGameId: 1, datFileId: 1, hasOwnedRoms: true, isComplete: true));
        var config = new LibraryConfiguration
        {
            IncludeTitleIds = [100],
            AllowedGenres = ["Puzzle"],
            ContentRatingPolicy = new ContentRatingPolicy { MaxMinimumAge = 0 }
        };

        var projection = MaterializedLibraryProjectionBuilder.Build(7, [title], config);

        projection.Titles.Single().TitleId.ShouldBe(100);
        projection.Releases.Single().TitleId.ShouldBe(100);
    }

    [Fact]
    public void Build_ShowMissingGamesFalse_UnownedOnlyTitleIsHidden()
    {
        var title = NewTitle(
            NewCandidate(datGameId: 1, datFileId: 1, hasOwnedRoms: false, isComplete: false));

        var projection = MaterializedLibraryProjectionBuilder.Build(7, [title], new LibraryConfiguration());

        projection.Titles.ShouldBeEmpty();
        projection.Releases.ShouldBeEmpty();
    }

    [Fact]
    public void Build_TitleSelectionModeIncludeOnly_HidesTitlesOutsideIncludeList()
    {
        var includedTitle = NewTitle(
            titleId: 100,
            NewCandidate(datGameId: 1, datFileId: 1, hasOwnedRoms: true, isComplete: true));
        var hiddenTitle = NewTitle(
            titleId: 200,
            NewCandidate(datGameId: 2, datFileId: 1, hasOwnedRoms: true, isComplete: true));
        var config = new LibraryConfiguration
        {
            TitleSelectionMode = LibraryTitleSelectionMode.IncludeOnly,
            IncludeTitleIds = [100]
        };

        var projection = MaterializedLibraryProjectionBuilder.Build(
            7,
            [includedTitle, hiddenTitle],
            config);

        projection.Titles.Single().TitleId.ShouldBe(100);
        projection.Releases.Single().TitleId.ShouldBe(100);
    }

    [Fact]
    public void AssertReleaseInvariants_PlayableButIneligible_Throws()
    {
        var release = NewMaterializedRelease(
            isEligible: false,
            isOwned: true,
            isComplete: true,
            isPlayable: true,
            isBlocked: true,
            blockReason: "ExcludedDat",
            isExposed: false,
            exposureReason: "ExcludedDat");

        var exception = Should.Throw<InvalidOperationException>(() =>
            MaterializedLibraryProjectionBuilder.AssertReleaseInvariants([release]));

        exception.Message.ShouldBe(
            "Materialized release playability requires eligibility, ownership, completeness, and no block.");
    }

    [Fact]
    public void AssertReleaseInvariants_BlockedButEligible_Throws()
    {
        var release = NewMaterializedRelease(
            isEligible: true,
            isOwned: true,
            isComplete: true,
            isPlayable: false,
            isBlocked: true,
            blockReason: "ExcludedDat",
            isExposed: false,
            exposureReason: "ExcludedDat");

        var exception = Should.Throw<InvalidOperationException>(() =>
            MaterializedLibraryProjectionBuilder.AssertReleaseInvariants([release]));

        exception.Message.ShouldBe("Blocked materialized releases must not be eligible.");
    }

    [Fact]
    public void AssertReleaseInvariants_IneligibleButUnblocked_Throws()
    {
        var release = NewMaterializedRelease(
            isEligible: false,
            isOwned: true,
            isComplete: true,
            isPlayable: false,
            isBlocked: false,
            blockReason: null,
            isExposed: false,
            exposureReason: "NotEligible");

        var exception = Should.Throw<InvalidOperationException>(() =>
            MaterializedLibraryProjectionBuilder.AssertReleaseInvariants([release]));

        exception.Message.ShouldBe("Ineligible materialized releases must be blocked.");
    }

    [Fact]
    public void AssertReleaseInvariants_ExposedButBlocked_Throws()
    {
        var release = NewMaterializedRelease(
            isEligible: false,
            isOwned: true,
            isComplete: true,
            isPlayable: false,
            isBlocked: true,
            blockReason: "ExcludedDat",
            isExposed: true,
            exposureReason: "ExposedDefault");

        var exception = Should.Throw<InvalidOperationException>(() =>
            MaterializedLibraryProjectionBuilder.AssertReleaseInvariants([release]));

        exception.Message.ShouldBe("Materialized release exposure requires eligibility and no block.");
    }

    [Fact]
    public void AssertProjectionInvariants_TitleAggregateMismatch_Throws()
    {
        var title = NewMaterializedTitle(
            eligibleReleaseCount: 1,
            playableReleaseCount: 0,
            exposedReleaseCount: 1,
            isOwned: false,
            isPlayable: false,
            availability: LibraryTitleAvailability.MetadataOnly);
        var release = NewMaterializedRelease(
            isEligible: true,
            isOwned: true,
            isComplete: false,
            isPlayable: false,
            isBlocked: false,
            blockReason: null,
            isExposed: true,
            exposureReason: "ExposedDefault");

        var exception = Should.Throw<InvalidOperationException>(() =>
            MaterializedLibraryProjectionBuilder.AssertProjectionInvariants(
                title,
                [release]));

        exception.Message.ShouldBe("Materialized title ownership must be derived from eligible releases.");
    }

    private static TitleCandidates NewTitle(
        params GameCandidate[] candidates) =>
        NewTitle([Rating(RatingBoard.Esrb, "E", 0)], candidates);

    private static TitleCandidates NewTitle(
        int titleId,
        params GameCandidate[] candidates) =>
        NewTitle(titleId, [Rating(RatingBoard.Esrb, "E", 0)], candidates);

    private static TitleCandidates NewTitle(
        IReadOnlyList<ContentRating> ratings,
        params GameCandidate[] candidates) =>
        NewTitle(100, ratings, candidates);

    private static TitleCandidates NewTitle(
        int titleId,
        IReadOnlyList<ContentRating> ratings,
        params GameCandidate[] candidates) =>
        new(
            TitleId: titleId,
            PlatformId: 1,
            Genre: "Action",
            ContentRatings: ratings,
            Candidates: candidates);

    private static ContentRating Rating(RatingBoard board, string code, int minimumAge) =>
        new()
        {
            Board = board,
            Code = code,
            Designation = RatingDesignation.Rated,
            MinimumAge = minimumAge,
            SourceId = "test"
        };

    private static GameCandidate NewCandidate(
        int datGameId,
        int datFileId,
        bool hasOwnedRoms,
        bool isComplete,
        int? catalogReleaseId = null,
        IReadOnlyList<int>? regions = null) =>
        new(
            datGameId,
            catalogReleaseId,
            datFileId,
            Revision: null,
            hasOwnedRoms,
            isComplete,
            [datFileId],
            regions ?? [1],
            LanguageIds: [1]);

    private static MaterializedLibraryTitle NewMaterializedTitle(
        int eligibleReleaseCount,
        int playableReleaseCount,
        int exposedReleaseCount,
        bool isOwned,
        bool isPlayable,
        LibraryTitleAvailability availability) =>
        new(
            LibraryId: 7,
            TitleId: 100,
            PlatformId: 1,
            Genre: "Action",
            IsVisible: true,
            IsOwned: isOwned,
            IsPlayable: isPlayable,
            EligibleReleaseCount: eligibleReleaseCount,
            PlayableReleaseCount: playableReleaseCount,
            ExposedReleaseCount: exposedReleaseCount,
            Availability: availability);

    private static MaterializedLibraryRelease NewMaterializedRelease(
        bool isEligible,
        bool isOwned,
        bool isComplete,
        bool isPlayable,
        bool isBlocked,
        string? blockReason,
        bool isExposed,
        string? exposureReason,
        int datGameId = 1) =>
        new(
            LibraryId: 7,
            TitleId: 100,
            CatalogReleaseId: datGameId,
            DatGameId: datGameId,
            DatFileId: 1,
            PlatformId: 1,
            IsEligible: isEligible,
            IsComplete: isComplete,
            IsOwned: isOwned,
            IsPlayable: isPlayable,
            IsBlocked: isBlocked,
            BlockReason: blockReason,
            IsExposed: isExposed,
            ExposureReason: exposureReason);
}
