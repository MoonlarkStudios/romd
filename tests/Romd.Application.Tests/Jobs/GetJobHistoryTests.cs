using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ListJobs;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Jobs;

public sealed class ListJobsTests
{
    [Theory]
    [InlineData("bad", null, 50)]
    [InlineData(null, "invalid", 50)]
    [InlineData(null, null, 101)]
    public async Task HandleAsync_InvalidFilter_DoesNotReadRepository(string? cursor, string? outcome, int limit)
    {
        var repository = Substitute.For<IJobRepository>();
        var handler = new ListJobsQueryHandler(TestSystemCatalog.Create(), repository, Substitute.For<ICurrentUser>());
        var result = await handler.HandleAsync(new ListJobsQuery(Cursor: cursor, Outcome: outcome, Limit: limit));
        result.IsError.ShouldBeTrue();
        await repository.DidNotReceiveWithAnyArgs().ListAsync(default!);
    }

    [Fact]
    public async Task HandleAsync_User_ScopesArchivedHistoryToOwner()
    {
        var repository = Substitute.For<IJobRepository>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.NewGuid());
        repository.ListAsync(Arg.Any<JobListFilter>(), Arg.Any<CancellationToken>()).Returns([]);
        var result = await new ListJobsQueryHandler(TestSystemCatalog.Create(), repository, user).HandleAsync(new ListJobsQuery(Archive: "archived"));
        result.IsError.ShouldBeFalse();
        await repository.Received().ListAsync(Arg.Is<JobListFilter>(filter => filter.OwnerId == user.UserId && filter.Archive == "archived"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_Manager_PagesWithStableCursorAndNoOwnerFilter()
    {
        var repository = Substitute.For<IJobRepository>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.NewGuid());
        user.HasRole(RomdRoleType.Manager).Returns(true);
        var first = UploadJob.Create("first.zip", createdAt: DateTimeOffset.UtcNow);
        repository.ListAsync(Arg.Any<JobListFilter>(), Arg.Any<CancellationToken>()).Returns([first, UploadJob.Create("second.zip", createdAt: DateTimeOffset.UtcNow)]);
        var handler = new ListJobsQueryHandler(TestSystemCatalog.Create(), repository, user);
        var result = await handler.HandleAsync(new ListJobsQuery(Limit: 1));
        result.Value.Items.Count.ShouldBe(1);
        result.Value.NextCursor.ShouldNotBeNull();
        await handler.HandleAsync(new ListJobsQuery(Cursor: result.Value.NextCursor, Limit: 1));
        await repository.Received().ListAsync(Arg.Is<JobListFilter>(filter => filter.OwnerId == null && filter.CursorId == first.Id && filter.CursorCreatedAt == first.CreatedAt), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cursor_EquivalentNormalizedFiltersAndChangedLimit_AreAccepted()
    {
        var repository = Substitute.For<IJobRepository>();
        var user = User();
        repository.ListAsync(Arg.Any<JobListFilter>(), Arg.Any<CancellationToken>()).Returns([UploadJob.Create("first.zip", createdAt: DateTimeOffset.UtcNow), UploadJob.Create("second.zip", createdAt: DateTimeOffset.UtcNow)]);
        var handler = new ListJobsQueryHandler(TestSystemCatalog.Create(), repository, user);
        var from = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var before = from.AddDays(1);
        var first = await handler.HandleAsync(new(Query: "  PACK  ", From: from, Before: before, Limit: 1));
        first.Value.HasNextPage.ShouldBeTrue();
        var second = await handler.HandleAsync(new(Query: "pack", From: from.ToOffset(TimeSpan.FromHours(3)),
            Before: before.ToOffset(TimeSpan.FromHours(-4)), Cursor: first.Value.NextCursor, Limit: 100));
        second.IsError.ShouldBeFalse();
        await repository.Received().ListAsync(Arg.Is<JobListFilter>(filter => filter.Limit == 100 && filter.Search == "pack" && filter.From == from), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("query")]
    [InlineData("outcome")]
    [InlineData("type")]
    [InlineData("archive")]
    [InlineData("correlation")]
    [InlineData("from")]
    [InlineData("before")]
    [InlineData("visibility")]
    public async Task Cursor_ChangedEffectiveQuery_IsRejectedBeforeRead(string changed)
    {
        var repository = Substitute.For<IJobRepository>();
        var user = User();
        repository.ListAsync(Arg.Any<JobListFilter>(), Arg.Any<CancellationToken>()).Returns([UploadJob.Create("first.zip", createdAt: DateTimeOffset.UtcNow), UploadJob.Create("second.zip", createdAt: DateTimeOffset.UtcNow)]);
        var handler = new ListJobsQueryHandler(TestSystemCatalog.Create(), repository, user);
        var query = new ListJobsQuery(Limit: 1);
        var first = await handler.HandleAsync(query);
        query = query with { Cursor = first.Value.NextCursor };
        query = changed switch
        {
            "query" => query with { Query = "different" },
            "outcome" => query with { Outcome = "failed" },
            "type" => query with { JobType = "upload" },
            "archive" => query with { Archive = "all" },
            "correlation" => query with { CorrelationId = Guid.NewGuid() },
            "from" => query with { From = DateTimeOffset.UtcNow.AddDays(-1) },
            "before" => query with { Before = DateTimeOffset.UtcNow.AddDays(1) },
            _ => query
        };
        if (changed == "visibility")
            user.HasRole(RomdRoleType.Admin).Returns(true);
        repository.ClearReceivedCalls();
        var rejected = await handler.HandleAsync(query);
        rejected.FirstError.ShouldBe(JobErrors.InvalidCursor());
        await repository.DidNotReceiveWithAnyArgs().ListAsync(default!, default);
    }

    [Fact]
    public async Task List_MissingActor_DoesNotReadUnattributedJobs()
    {
        var repository = Substitute.For<IJobRepository>();
        var result = await new ListJobsQueryHandler(TestSystemCatalog.Create(), repository, Substitute.For<ICurrentUser>()).HandleAsync(new());
        result.Value.Items.ShouldBeEmpty();
        result.Value.HasNextPage.ShouldBeFalse();
        await repository.DidNotReceiveWithAnyArgs().ListAsync(default!, default);
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.NewGuid());
        user.HasRole(RomdRoleType.Manager).Returns(true);
        return user;
    }
}
