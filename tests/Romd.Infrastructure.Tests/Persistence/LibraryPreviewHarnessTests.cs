using System.Text.Json;
using System.Text.Json.Nodes;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Libraries;
using Romd.ScaleHarness;
using Romd.ScaleHarness.Scenarios;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class LibraryPreviewHarnessTests
{
    [Fact]
    public void Options_Preview30k_SeparatesFixtureAndRecordsActualTitleAxis()
    {
        var preview = HarnessOptions.Parse(["--scale", "30k", "--scenario", "library-preview",
            "--connection-string", "Host=localhost;Database=unrelated;Username=synthetic", "--preview-iterations", "7", "--prepare-only"]);
        var ordinary = preview with { Scenario = "title-search" };

        preview.Scale.Titles.ShouldBe(30_000);
        preview.Scale.DatGames.ShouldBe(36_000); // Admin fixture shape differs from the preview manifest.
        preview.DatabaseName.ShouldBe("romd_scale_30k_preview");
        preview.DatabaseName.ShouldNotBe(ordinary.DatabaseName);
        preview.ManifestPath.ShouldNotBe(ordinary.ManifestPath);
        ordinary.ManifestPath.ShouldEndWith("romd-scale-30k.manifest.json");
        preview.DatasetKind.ShouldBe("library-preview-v1");
        preview.PreviewIterations.ShouldBe(7);
        preview.PrepareOnly.ShouldBeTrue();
        preview.DatabaseDescription.ShouldNotContain("synthetic");
        LibraryPreviewScenario.BatchSizes.ShouldBe([256, 1024, 2048]);
    }

    [Fact]
    public void Manifest_LegacyAdminJson_RetainsReuseCompatibility()
    {
        var manifest = new DatasetManifest(ScaleParameters.SpecVersion, "100k", ScaleParameters.Seed,
            "romd_scale_100k", DateTimeOffset.UnixEpoch, 0, Guid.Empty, 1, "saga", 70_000, [], []);
        var json = JsonNode.Parse(JsonSerializer.Serialize(manifest))!.AsObject();
        json.Remove(nameof(DatasetManifest.DatasetKind));

        var legacy = JsonSerializer.Deserialize<DatasetManifest>(json.ToJsonString())!;

        legacy.Matches(ScaleParameters.Scale100K, "romd_scale_100k").ShouldBeTrue();
        legacy.Matches(ScaleParameters.Scale100K, "romd_scale_100k", "library-preview-v1").ShouldBeFalse();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("31")]
    [InlineData("invalid")]
    public void Options_InvalidPreviewIterations_Rejects(string value) =>
        Should.Throw<ArgumentException>(() => HarnessOptions.Parse(["--preview-iterations", value]));

    [Fact]
    public void PreviewOracle_MixedRatingsTrackingAndDuplicateSources_AgreesWithProjectionRules()
    {
        var data = new DeterministicDataset(ScaleParameters.Scale30K);
        for (int t = 0; t < 60; t++)
        {
            int dat = (data.PlatformOfTitle(t) - 1) * 2 + 1;
            var ratings = data.TitleEnriched(t) ? new[] { new ContentRating {
                Board = (RatingBoard)data.RatingBoardOfTitle(t), Code = data.RatingCodeOfTitle(t),
                MinimumAge = data.RatingAgeOfTitle(t), Designation = RatingDesignation.Rated, SourceId = "igdb"
            } } : Array.Empty<ContentRating>();
            var candidates = LibraryPreviewDataset.HasCandidates(t) ? new[] {
                new GameCandidate(t * 2 + 1, t + 1, dat, null, LibraryPreviewDataset.IsOwned(t), LibraryPreviewDataset.IsOwned(t), [dat], [1], [1]),
                new GameCandidate(t * 2 + 2, t + 1, dat + 1, null, false, false, [dat + 1], [1], [1])
            } : Array.Empty<GameCandidate>();
            var title = new TitleCandidates(t + 1, data.PlatformOfTitle(t), data.TitleGenre(t), ratings, candidates, data.TitleTracked(t));

            var saved = MaterializedLibraryProjectionBuilder.Build(1, [title], LibraryPreviewDataset.SavedConfiguration);
            var draft = MaterializedLibraryProjectionBuilder.Build(1, [title], LibraryPreviewDataset.DraftConfiguration);

            saved.Titles.Any().ShouldBe(LibraryPreviewDataset.WasEligible(data, t));
            draft.Titles.Any().ShouldBe(LibraryPreviewDataset.IsEligible(data, t));
            if (draft.Titles.Any())
            {
                draft.Releases.ShouldHaveSingleItem();
                draft.Titles[0].IsOwned.ShouldBe(LibraryPreviewDataset.IsOwned(t));
                draft.Titles[0].IsPlayable.ShouldBe(LibraryPreviewDataset.IsOwned(t));
            }
        }
    }

    [Fact]
    public void PreviewShape_ThirtyThousandTitles_HasExpectedMixedPopulations()
    {
        var data = new DeterministicDataset(ScaleParameters.Scale30K);
        var titles = Enumerable.Range(0, data.Scale.Titles).ToArray();

        titles.Count(LibraryPreviewDataset.HasCandidates).ShouldBe(27_000);
        titles.Count(data.TitleTracked).ShouldBe(20_000);
        titles.Count(data.TitleEnriched).ShouldBe(12_000);
        titles.Count(t => LibraryPreviewDataset.WasEligible(data, t)).ShouldBe(21_000);
        titles.Count(t => LibraryPreviewDataset.IsEligible(data, t)).ShouldBe(18_000);
        titles.Count(t => LibraryPreviewDataset.IsEligible(data, t) && !LibraryPreviewDataset.WasEligible(data, t)).ShouldBe(4_000);
        titles.Count(t => LibraryPreviewDataset.WasEligible(data, t) && !LibraryPreviewDataset.IsEligible(data, t)).ShouldBe(7_000);
        titles.Count(LibraryPreviewDataset.IsOwned).ShouldBe(9_000);
        foreach (string view in new[] { "all", "matching", "added", "removed", "excluded" })
            titles.Count(t => LibraryPreviewDataset.MatchesView(data, t, view)).ShouldBeGreaterThan(0);
    }
}
