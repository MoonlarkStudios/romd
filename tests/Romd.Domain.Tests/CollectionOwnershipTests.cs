using System.Text.Json;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Dat;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests;

public sealed class CollectionOwnershipTests
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.UnixEpoch;

    [Fact]
    public void JobErrors_ExternalMutationCannotChangeCompletionOutcome_AndViewRemainsLive()
    {
        var job = BulkEnrichmentJob.Create(1, "NES", CreatedAt);
        job.Start("run", CreatedAt);
        var errors = job.Errors;
        job.RecordFailed(7, "Synthetic title", "Unavailable", JobErrorReason.ProviderError, CreatedAt);

        Should.Throw<NotSupportedException>(() => ((ICollection<JobError>)errors).Clear());
        job.Complete(CreatedAt);

        job.Errors.ShouldBeSameAs(errors);
        errors.ShouldHaveSingleItem();
        job.HasErrors.ShouldBeTrue();
        job.PhaseEnum.ShouldBe(BulkEnrichmentPhase.CompletedWithErrors);
    }

    [Fact]
    public void JobItem_CopiesMatchedTitleInputs_AndRejectsExternalJournalMutation()
    {
        var ids = new List<int> { 7, 11 };
        var item = JobItem.ForRom(Guid.NewGuid(), "synthetic.rom", 1, RomIngestOutcome.Ingested,
            23, ids, 1, null, CreatedAt);
        ids.Clear();

        Should.Throw<NotSupportedException>(() => ((IList<int>)item.MatchedTitleIds)[0] = 99);

        item.MatchedTitleIds.ShouldBe([7, 11]);
        item.WithUploadIdentity().MatchedTitleIds.ShouldBe([7, 11]);
    }

    [Fact]
    public void DatGame_OwnsRehydratedMembership_AndOnlyDomainMethodsCanChangeLiveViews()
    {
        var roms = new List<DatRom> { DatRom.CreateNew("first.rom", 1) };
        var disks = new List<DatDisk> { DatDisk.CreateNew("first.chd") };
        var game = DatGame.Rehydrate(1, 2, "Synthetic game", null, null, null, null, null,
            null, null, null, null, null, false, roms, disks);
        roms.Clear();
        disks.Clear();
        var romView = game.Roms;
        var diskView = game.Disks;

        Should.Throw<NotSupportedException>(() => ((ICollection<DatRom>)romView).Clear());
        Should.Throw<NotSupportedException>(() => ((ICollection<DatDisk>)diskView).Clear());
        game.AddRom(DatRom.CreateNew("second.rom", 2));
        game.AddDisk(DatDisk.CreateNew("second.chd"));

        game.Roms.ShouldBeSameAs(romView);
        game.Disks.ShouldBeSameAs(diskView);
        romView.Select(rom => rom.Name).ShouldBe(["first.rom", "second.rom"]);
        diskView.Select(disk => disk.Name).ShouldBe(["first.chd", "second.chd"]);
    }

    [Fact]
    public void ArtworkSelection_CopiesAndNormalizesInput_AndRejectsExternalReplacement()
    {
        int[] ids = [11, 7, 11];
        var job = BulkEnrichmentJob.CreateArtwork(Guid.NewGuid(), ids, Guid.NewGuid(), CreatedAt);
        ids[0] = 99;

        Should.Throw<NotSupportedException>(() => ((IList<int>)job.ArtworkTitleIds!)[0] = 99);

        job.ArtworkTitleIds.ShouldBe([7, 11]);
        job.TotalTitles.ShouldBe(2);
    }

    [Fact]
    public void RehydratedArtwork_OwnsPersistedInputs_AndExposesReadOnlyLiveOutcomeCounts()
    {
        var ids = new List<int> { 7, 11 };
        var outcomes = new Dictionary<string, int> { ["cover:acquired"] = 1 };
        var job = BulkEnrichmentJob.Rehydrate(Guid.NewGuid(), Guid.NewGuid(), "Artwork", null,
            BulkEnrichmentPhase.Pending, EnrichmentScope.Tracked, null, 2, 0, 0, 0, 0, 0, null,
            [], DateTimeOffset.UnixEpoch, null, null, false, null, null, ids, outcomes);
        ids.Clear();
        outcomes.Clear();
        var view = job.ArtworkOutcomes;

        Should.Throw<NotSupportedException>(() => ((IList<int>)job.ArtworkTitleIds!)[0] = 99);
        Should.Throw<NotSupportedException>(() => ((IDictionary<string, int>)view).Clear());
        job.RecordArtworkOutcome("cover", "acquired");

        job.ArtworkTitleIds.ShouldBe([7, 11]);
        job.ArtworkOutcomes.ShouldBeSameAs(view);
        view["cover:acquired"].ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RatingDescriptors_CaptureOwnedSnapshot_AndRejectExternalChanges(bool effective)
    {
        var descriptors = new List<string> { "Fantasy Violence" };
        IReadOnlyList<string> captured = effective
            ? new ContentRating
            {
                Board = RatingBoard.Esrb,
                Code = "E",
                Designation = RatingDesignation.Rated,
                SourceId = "user",
                Descriptors = descriptors
            }.Descriptors
            : new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "E", Descriptors = descriptors }.Descriptors;
        descriptors.Clear();

        Should.Throw<NotSupportedException>(() => ((IList<string>)captured)[0] = "Changed");

        captured.ShouldBe(["Fantasy Violence"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RatingDescriptors_ExplicitNull_PreservesRetainedJsonRepresentation(bool effective)
    {
        if (effective)
        {
            var rating = new ContentRating
            {
                Board = RatingBoard.Esrb,
                Code = "T",
                Designation = RatingDesignation.Rated,
                SourceId = "provider",
                Descriptors = null!
            };
            var restored = JsonSerializer.Deserialize<ContentRating>(JsonSerializer.Serialize(rating))!;
            restored.Descriptors.ShouldBeNull();
        }
        else
        {
            var claim = new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "T", Descriptors = null! };
            var restored = JsonSerializer.Deserialize<ContentRatingClaim>(JsonSerializer.Serialize(claim))!;
            restored.Descriptors.ShouldBeNull();
        }
    }

    [Fact]
    public void MetadataRatings_OwnInputMembership_AndPreserveJsonRepresentation()
    {
        var ratings = new List<ContentRatingClaim>
        {
            new() { Board = RatingBoard.Esrb, RawCode = "E", Descriptors = ["Fantasy Violence"] }
        };
        var payload = new TitleMetadataPayload { ContentRatings = ratings };
        ratings.Clear();

        Should.Throw<NotSupportedException>(() => ((ICollection<ContentRatingClaim>)payload.ContentRatings!).Clear());

        payload.HasAnyValue().ShouldBeTrue();
        var restored = JsonSerializer.Deserialize<TitleMetadataPayload>(JsonSerializer.Serialize(payload))!;
        restored.ContentRatings.ShouldHaveSingleItem().Descriptors.ShouldBe(["Fantasy Violence"]);
    }
}
