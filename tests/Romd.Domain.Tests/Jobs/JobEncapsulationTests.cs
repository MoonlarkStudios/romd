using System.Reflection;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Jobs;

public sealed class JobEncapsulationTests
{
    [Fact]
    public void Errors_MutableInterface_CannotEraseFailuresOrChangeCompletionOutcome()
    {
        var job = BulkEnrichmentJob.Create(1, "platform", createdAt: DateTimeOffset.UtcNow);
        var errors = job.Errors;
        job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
        job.RecordFailed(7, "title", "provider unavailable", JobErrorReason.ProviderError, occurredAt: DateTimeOffset.UtcNow);
        var recorded = errors.ShouldHaveSingleItem();

        Should.Throw<NotSupportedException>(() => ((IList<JobError>)errors)[0] =
            new JobError("replacement", "replacement", DateTimeOffset.UtcNow));
        Should.Throw<NotSupportedException>(() => ((ICollection<JobError>)errors).Clear());
        job.Complete(completedAt: DateTimeOffset.UtcNow);

        job.Errors.ShouldBeSameAs(errors);
        errors.ShouldHaveSingleItem().ShouldBeSameAs(recorded);
        job.PhaseEnum.ShouldBe(BulkEnrichmentPhase.CompletedWithErrors);
    }

    [Fact]
    public void Errors_RetainedElements_DoNotPermitInPlaceMutation()
    {
        typeof(JobError).GetProperties().Where(property => property.SetMethod?.IsPublic == true).ShouldBeEmpty();
        typeof(JobError).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName && method.ReturnType == typeof(void)).ShouldBeEmpty();
    }

    [Fact]
    public void CreateArtwork_MutableInputAndReturnedView_CannotChangeSelection()
    {
        var selection = new List<int> { 7, 3, 7 };
        var job = BulkEnrichmentJob.CreateArtwork(Guid.NewGuid(), selection, Guid.NewGuid(), createdAt: DateTimeOffset.UtcNow);
        var titles = job.ArtworkTitleIds.ShouldNotBeNull();
        selection[0] = 99;
        selection.Clear();

        Should.Throw<NotSupportedException>(() => ((IList<int>)titles)[0] = 99);
        Should.Throw<NotSupportedException>(() => ((ICollection<int>)titles).Clear());

        job.ArtworkTitleIds.ShouldBeSameAs(titles);
        titles.ShouldBe([3, 7]);
        job.TotalTitles.ShouldBe(2);
    }

    [Fact]
    public void ArtworkOutcomes_MutableInterface_RejectsChangesAndRetainsLiveView()
    {
        var job = BulkEnrichmentJob.CreateArtwork(Guid.NewGuid(), [7], Guid.NewGuid(), createdAt: DateTimeOffset.UtcNow);
        var outcomes = job.ArtworkOutcomes;

        job.RecordArtworkOutcome("Poster", "Completed");
        Should.Throw<NotSupportedException>(() => ((IDictionary<string, int>)outcomes)["Poster:Completed"] = 99);
        Should.Throw<NotSupportedException>(() => ((IDictionary<string, int>)outcomes).Clear());
        job.RecordArtworkOutcome("Poster", "Completed");

        job.ArtworkOutcomes.ShouldBeSameAs(outcomes);
        outcomes["Poster:Completed"].ShouldBe(2);
    }

    [Fact]
    public void Rehydrate_MutableInputs_OwnsSelectionOutcomesAndErrorsWithoutRenormalizingPersistedValues()
    {
        var titles = new List<int> { 7, 3, 7 };
        var outcomes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Poster:Completed"] = 2 };
        var errors = new List<JobError> { new("title", "retained failure", DateTimeOffset.UtcNow) };
        var job = Restore(titles, outcomes, errors);
        titles[0] = 99;
        titles.Clear();
        outcomes["Poster:Completed"] = 99;
        errors.Clear();

        job.ArtworkTitleIds.ShouldBe([7, 3, 7]);
        job.ArtworkOutcomes["poster:completed"].ShouldBe(2);
        job.Errors.ShouldHaveSingleItem().Message.ShouldBe("retained failure");
        Should.Throw<NotSupportedException>(() => ((IList<int>)job.ArtworkTitleIds!)[0] = 99);
        Should.Throw<NotSupportedException>(() => ((IDictionary<string, int>)job.ArtworkOutcomes).Clear());
        Should.Throw<NotSupportedException>(() => ((ICollection<JobError>)job.Errors).Clear());

        job.RecordArtworkOutcome("poster", "completed");

        job.ArtworkOutcomes.Count.ShouldBe(1);
        job.ArtworkOutcomes["Poster:Completed"].ShouldBe(3);
        outcomes["Poster:Completed"].ShouldBe(99);
    }

    [Fact]
    public void Rehydrate_NullAndEmptyArtworkSelection_PreservesTheirDistinctMeaning()
    {
        var platformJob = Restore(null, null, []);
        var artworkJob = Restore([], null, []);

        platformJob.ArtworkTitleIds.ShouldBeNull();
        artworkJob.ArtworkTitleIds.ShouldNotBeNull().ShouldBeEmpty();
        platformJob.ArtworkOutcomes.ShouldBeEmpty();
        artworkJob.RecordArtworkOutcome("Poster", "Completed");
        platformJob.ArtworkOutcomes.ShouldBeEmpty();
    }

    private static BulkEnrichmentJob Restore(IReadOnlyList<int>? titles, Dictionary<string, int>? outcomes,
        IReadOnlyList<JobError> errors) => BulkEnrichmentJob.Rehydrate(
        Guid.NewGuid(), Guid.NewGuid(), "artwork", 1, BulkEnrichmentPhase.Enriching, EnrichmentScope.All,
        "delivery", 3, 0, 0, 0, 0, 0, null, errors, DateTimeOffset.UtcNow, null, null,
        false, null, Guid.NewGuid(), titles, outcomes);
}
