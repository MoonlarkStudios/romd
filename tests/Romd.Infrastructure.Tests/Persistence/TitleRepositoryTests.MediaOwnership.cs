using Microsoft.EntityFrameworkCore;
using Romd.Domain.Catalog;
using Romd.Persistence;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class TitleRepositoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MediaOwnership_FreshSnapshotsAndStagedPrimaryWrite_PreserveIndependentStateAndProvenance(bool commit)
    {
        await SeedCuratedTitleAsync();
        await using (var seed = CreateDb())
        {
            await seed.TitleMedia.Where(media => media.Id == 1).ExecuteUpdateAsync(setters => setters
                .SetProperty(media => media.SourceUrl, "https://example.test/cover.png")
                .SetProperty(media => media.SourcePageUrl, "https://example.test/credits"));
        }
        await using var firstDb = CreateDb();
        await using var secondDb = CreateDb();
        var repository = new TitleRepository(firstDb);
        var first = (await repository.GetAggregateAsync(1)).ShouldNotBeNull();
        var second = (await new TitleRepository(secondDb).GetAggregateAsync(1)).ShouldNotBeNull();
        var originalRevision = first.Revision;
        var original = first.GetMediaById(1).ShouldNotBeNull();

        first.SetPrimaryMedia(2);

        first.GetPrimaryMedia(MediaType.Cover).ShouldNotBeNull().Id.ShouldBe(2);
        second.GetPrimaryMedia(MediaType.Cover).ShouldNotBeNull().Id.ShouldBe(1);
        second.GetMediaById(1).ShouldNotBeSameAs(original);
        original.Attribution.ShouldBe("Artist");
        original.SourceUrl.ShouldBe("https://example.test/cover.png");
        original.SourcePageUrl.ShouldBe("https://example.test/credits");
        await using (var transaction = await new EfUnitOfWork(firstDb).BeginTransactionAsync())
        {
            await repository.UpdatePrimaryMediaStagedAsync(first, MediaType.Cover);
            if (commit)
                await transaction.CommitAsync();
        }

        await using var verify = CreateDb();
        var stored = (await new TitleRepository(verify).GetAggregateAsync(1)).ShouldNotBeNull();
        stored.GetPrimaryMedia(MediaType.Cover).ShouldNotBeNull().Id.ShouldBe(commit ? 2 : 1);
        var retained = stored.GetMediaById(1).ShouldNotBeNull();
        retained.Attribution.ShouldBe(original.Attribution);
        retained.SourceUrl.ShouldBe(original.SourceUrl);
        retained.SourcePageUrl.ShouldBe(original.SourcePageUrl);
        retained.FileId.ShouldBe(original.FileId);
        retained.CreatedAt.ShouldBe(original.CreatedAt);
        if (commit)
            stored.Revision.ShouldNotBe(originalRevision);
        else
            stored.Revision.ShouldBe(originalRevision);
        second.GetPrimaryMedia(MediaType.Cover).ShouldNotBeNull().Id.ShouldBe(1);
    }
}
