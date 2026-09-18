using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Browse;

public sealed class ConsumerReleaseSelectorTests
{
    private readonly ConsumerReleaseSelector _selector = new();

    [Fact]
    public void SelectDefault_NoReleases_ReturnsNull()
    {
        var selected = _selector.SelectDefault([], ConsumerReleasePreference.Default);

        selected.ShouldBeNull();
    }

    [Fact]
    public void SelectDefault_NoPreference_UsesCanonicalAccessibleFallback()
    {
        var releases = new[]
        {
            NewCandidate(1, "Alpha", isPlayable: false, isComplete: true, revision: "Rev 9"),
            NewCandidate(2, "Beta", isPlayable: true, isComplete: true, revision: "Rev 1")
        };

        var selected = _selector.SelectDefault(releases, ConsumerReleasePreference.Default);

        selected.ShouldNotBeNull();
        selected.Id.ShouldBe(2);
    }

    [Fact]
    public void SelectDefault_RegionPreference_SelectsFirstPreferredRegion()
    {
        var releases = new[]
        {
            NewCandidate(1, "USA", regionIds: [1]),
            NewCandidate(2, "Europe", regionIds: [2])
        };
        var preference = new ConsumerReleasePreference(
            PreferredRegionIds: [2, 1],
            PreferredLanguageIds: [],
            RevisionStrategy: ConsumerRevisionPreference.None);

        var selected = _selector.SelectDefault(releases, preference);

        selected.ShouldNotBeNull();
        selected.Id.ShouldBe(2);
    }

    [Fact]
    public void SelectDefault_LanguagePreference_BreaksRegionTie()
    {
        var releases = new[]
        {
            NewCandidate(1, "English", regionIds: [1], languageIds: [1]),
            NewCandidate(2, "Japanese", regionIds: [1], languageIds: [2])
        };
        var preference = new ConsumerReleasePreference(
            PreferredRegionIds: [1],
            PreferredLanguageIds: [2, 1],
            RevisionStrategy: ConsumerRevisionPreference.None);

        var selected = _selector.SelectDefault(releases, preference);

        selected.ShouldNotBeNull();
        selected.Id.ShouldBe(2);
    }

    [Theory]
    [InlineData(ConsumerRevisionPreference.NewestFirst, 2)]
    [InlineData(ConsumerRevisionPreference.OldestFirst, 1)]
    public void SelectDefault_RevisionPreference_BreaksVersionTie(
        ConsumerRevisionPreference revisionPreference,
        int expectedReleaseId)
    {
        var releases = new[]
        {
            NewCandidate(1, "Rev 1", revision: "Rev 1", regionIds: [1], languageIds: [1]),
            NewCandidate(2, "Rev 2", revision: "Rev 2", regionIds: [1], languageIds: [1])
        };
        var preference = new ConsumerReleasePreference(
            PreferredRegionIds: [1],
            PreferredLanguageIds: [1],
            RevisionStrategy: revisionPreference);

        var selected = _selector.SelectDefault(releases, preference);

        selected.ShouldNotBeNull();
        selected.Id.ShouldBe(expectedReleaseId);
    }

    [Fact]
    public void SelectDefault_PreferenceTie_FallsBackToCanonicalOrdering()
    {
        var releases = new[]
        {
            NewCandidate(1, "Alpha", revision: "Rev 1", regionIds: [1], languageIds: [1]),
            NewCandidate(2, "Beta", revision: "Rev 2", regionIds: [1], languageIds: [1])
        };
        var preference = new ConsumerReleasePreference(
            PreferredRegionIds: [1],
            PreferredLanguageIds: [1],
            RevisionStrategy: ConsumerRevisionPreference.None);

        var selected = _selector.SelectDefault(releases, preference);

        selected.ShouldNotBeNull();
        selected.Id.ShouldBe(2);
    }

    [Fact]
    public void SelectDefault_UnmatchedPreference_FallsBackToCanonicalOrdering()
    {
        var releases = new[]
        {
            NewCandidate(1, "Alpha", revision: "Rev 1", regionIds: [1]),
            NewCandidate(2, "Beta", revision: "Rev 2", regionIds: [2])
        };
        var preference = new ConsumerReleasePreference(
            PreferredRegionIds: [99],
            PreferredLanguageIds: [],
            RevisionStrategy: ConsumerRevisionPreference.None);

        var selected = _selector.SelectDefault(releases, preference);

        selected.ShouldNotBeNull();
        selected.Id.ShouldBe(2);
    }

    [Fact]
    public void SelectDefault_MultiSegmentRevision_OrdersSemantically()
    {
        var releases = new[]
        {
            NewCandidate(1, "Rev 1.9", revision: "Rev 1.9"),
            NewCandidate(2, "Rev 1.10", revision: "Rev 1.10")
        };

        var selected = _selector.SelectDefault(releases, ConsumerReleasePreference.Default);

        selected.ShouldNotBeNull();
        selected.Id.ShouldBe(2);
    }

    private static ConsumerReleaseSelectionCandidate NewCandidate(
        int id,
        string name,
        bool isPlayable = true,
        bool isComplete = true,
        string? revision = null,
        IReadOnlyList<int>? regionIds = null,
        IReadOnlyList<int>? languageIds = null) =>
        new(
            new ConsumerReleaseData
            {
                Id = id,
                Name = name,
                Revision = revision,
                Regions = [],
                Languages = [],
                SizeBytes = 1,
                IsComplete = isComplete
            },
            isPlayable,
            regionIds ?? [],
            languageIds ?? []);
}
