using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobItems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobArtworkItems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobArtworkSummary;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Jobs;

public sealed class S5cTypedJobFeedTests
{
    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    public S5cTypedJobFeedTests()
    {
        _user.UserId.Returns(Guid.NewGuid());
        _user.HasRole(Arg.Any<RomdRoleType>()).Returns(true);
    }
    [Fact]
    public async Task Artwork_CursorBindsStatusAndJob_WhileLimitCanChange()
    {
        var service = Substitute.For<IArtworkCompletionService>();
        var job = ArtworkCompletionJob.Create(Guid.NewGuid(), 2, null, createdAt: DateTimeOffset.UtcNow, "Artwork");
        var other = ArtworkCompletionJob.Create(Guid.NewGuid(), 2, null, createdAt: DateTimeOffset.UtcNow, "Artwork");
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        _jobs.GetByIdAsync(other.Id, Arg.Any<CancellationToken>()).Returns(other);
        var item = new ArtworkCompletionItemDto(IdCoder.Encode(1), "Title", ArtworkCompletionState.Done, [], null, null, null);
        service.ItemsAsync(job.Id, 0, "Failed", Arg.Any<CancellationToken>(), 1).Returns(new ArtworkCompletionItemsDto([item], item.TitleId));
        service.ItemsAsync(job.Id, 1, "Failed", Arg.Any<CancellationToken>(), 100).Returns(new ArtworkCompletionItemsDto([], null));
        var handler = new GetJobArtworkItemsQueryHandler(_jobs, _user, service);
        var first = await handler.HandleAsync(new(job.Id, "Failed", Limit: 1));
        first.Value.HasNextPage.ShouldBeTrue();
        (await handler.HandleAsync(new(job.Id, "Failed", first.Value.NextCursor, 100))).IsError.ShouldBeFalse();
        (await handler.HandleAsync(new(job.Id, "Review", first.Value.NextCursor))).FirstError.ShouldBe(JobErrors.InvalidCursor());
        (await handler.HandleAsync(new(other.Id, "Failed", first.Value.NextCursor))).FirstError.ShouldBe(JobErrors.InvalidCursor());
        await service.Received(1).ItemsAsync(job.Id, 1, "Failed", Arg.Any<CancellationToken>(), 100);
    }
    [Fact]
    public async Task Upload_CursorBindsQueryOutcomeAndJob_AndReappliesVisibleScope()
    {
        var items = Substitute.For<IJobItemRepository>();
        var snapshots = Substitute.For<IReadSnapshotTransactionFactory>();
        snapshots.BeginAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<IReadSnapshotTransaction>());
        var job = UploadJob.Create("owned.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: _user.UserId);
        var other = UploadJob.Create("other.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: _user.UserId);
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        _jobs.GetByIdAsync(other.Id, Arg.Any<CancellationToken>()).Returns(other);
        var view = new JobItemView
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            Kind = JobItemKind.Rom,
            FileName = "pack.bin",
            SizeBytes = 1,
            Outcome = JobItemOutcome.Failed,
            MatchedTitles = [],
            CreatedAt = DateTimeOffset.UtcNow
        };
        var position = new JobItemPosition(view.CreatedAt, view.Id);
        items.GetByJobAsync(job.Id, JobItemOutcome.Failed, 1, Arg.Any<CancellationToken>(), null, "PACK")
            .Returns(new JobItemPageResult([view], true, position));
        items.GetByJobAsync(job.Id, JobItemOutcome.Failed, 100, Arg.Any<CancellationToken>(), position, "pack")
            .Returns(new JobItemPageResult([], false));
        var handler = new GetJobItemsQueryHandler(items, snapshots, _jobs, _user, TestSystemCatalog.Create());
        var first = await handler.HandleAsync(new(job.Id, "failed", 1, Query: " PACK "));
        first.Value.HasNextPage.ShouldBeTrue();
        (await handler.HandleAsync(new(job.Id, "failed", 100, first.Value.NextCursor, "pack"))).IsError.ShouldBeFalse();
        (await handler.HandleAsync(new(job.Id, "ingested", 100, first.Value.NextCursor, "pack"))).FirstError.ShouldBe(JobErrors.InvalidCursor());
        (await handler.HandleAsync(new(job.Id, "failed", 100, first.Value.NextCursor, "different"))).FirstError.ShouldBe(JobErrors.InvalidCursor());
        (await handler.HandleAsync(new(other.Id, "failed", 100, first.Value.NextCursor, "pack"))).FirstError.ShouldBe(JobErrors.InvalidCursor());
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns((Job?)null);
        (await handler.HandleAsync(new(job.Id, "failed", 100, first.Value.NextCursor, "pack"))).FirstError.ShouldBe(JobErrors.NotFound(job.Id));
        await items.Received(1).GetByJobAsync(job.Id, JobItemOutcome.Failed, 100, Arg.Any<CancellationToken>(), position, "pack");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Feeds_UnknownOrWrongFamily_ReturnConsistentErrorsBeforeReading(bool known)
    {
        var job = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow);
        if (known)
            _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var items = Substitute.For<IJobItemRepository>();
        var snapshots = Substitute.For<IReadSnapshotTransactionFactory>();
        var artwork = Substitute.For<IArtworkCompletionService>();
        var expected = known ? JobErrors.FeedUnavailable() : JobErrors.NotFound(job.Id);
        (await new GetJobItemsQueryHandler(items, snapshots, _jobs, _user, TestSystemCatalog.Create()).HandleAsync(new(job.Id))).FirstError.ShouldBe(expected);
        (await new GetJobArtworkItemsQueryHandler(_jobs, _user, artwork).HandleAsync(new(job.Id))).FirstError.ShouldBe(expected);
        (await new GetJobArtworkSummaryQueryHandler(_jobs, _user, artwork).HandleAsync(new(job.Id))).FirstError.ShouldBe(expected);
        items.ReceivedCalls().ShouldBeEmpty();
        snapshots.ReceivedCalls().ShouldBeEmpty();
        artwork.ReceivedCalls().ShouldBeEmpty();
    }
}
