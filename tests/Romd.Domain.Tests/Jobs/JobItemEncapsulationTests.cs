using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Jobs;

public sealed class JobItemEncapsulationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchedTitleIds_MutableInputAndReturnedView_CannotChangeRecordedProvenance(bool rehydrate)
    {
        var titles = new List<int> { 7, 3, 7 };
        var original = JobItem.ForRom(Guid.NewGuid(), "game.rom", 128, RomIngestOutcome.Ingested, 1, titles, 1, null, createdAt: DateTimeOffset.UtcNow);
        var item = rehydrate
            ? JobItem.Rehydrate(original.Id, original.JobId, original.Kind, original.FileName, original.SizeBytes,
                original.Outcome, original.RomFileId, original.DatFileId, original.PlatformId, titles,
                original.GameCount, original.Error, original.ArchiveOnly, original.CreatedAt)
            : original;
        titles[0] = 99;
        titles.Clear();
        var view = item.MatchedTitleIds;

        Should.Throw<NotSupportedException>(() => ((IList<int>)view)[0] = 99);
        Should.Throw<NotSupportedException>(() => ((ICollection<int>)view).Clear());

        item.MatchedTitleIds.ShouldBeSameAs(view);
        view.ShouldBe([7, 3, 7]);
        item.WithUploadIdentity().MatchedTitleIds.ShouldBe(view);
    }

    [Fact]
    public void MatchedTitleIds_DatItem_CannotAddRomProvenanceThroughCollectionCast()
    {
        var item = JobItem.ForDat(Guid.NewGuid(), "catalog.dat", 128, true, true, 1, 2, 3, null, createdAt: DateTimeOffset.UtcNow);

        Should.Throw<NotSupportedException>(() => ((ICollection<int>)item.MatchedTitleIds).Add(7));

        item.MatchedTitleIds.ShouldBeEmpty();
    }
}
