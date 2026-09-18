using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobHistory;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Jobs;

public sealed class GetJobHistoryTests
{
    [Theory]
    [InlineData("bad", null, 50)]
    [InlineData(null, "invalid", 50)]
    [InlineData(null, null, 101)]
    public async Task HandleAsync_InvalidFilter_DoesNotReadRepository(string? cursor, string? outcome, int limit)
    {
        var repository = Substitute.For<IJobRepository>();
        var handler = new GetJobHistoryQueryHandler(TestSystemCatalog.Create(), repository, Substitute.For<ICurrentUser>());
        var result = await handler.HandleAsync(new GetJobHistoryQuery(Cursor: cursor, Outcome: outcome, Limit: limit));
        result.IsError.ShouldBeTrue();
        await repository.DidNotReceiveWithAnyArgs().GetHistoryAsync(default!);
    }

    [Fact]
    public async Task HandleAsync_User_ScopesArchivedHistoryToOwner()
    {
        var repository = Substitute.For<IJobRepository>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.NewGuid());
        repository.GetHistoryAsync(Arg.Any<JobHistoryFilter>(), Arg.Any<CancellationToken>()).Returns([]);
        var result = await new GetJobHistoryQueryHandler(TestSystemCatalog.Create(), repository, user).HandleAsync(new GetJobHistoryQuery(Archive: "archived"));
        result.IsError.ShouldBeFalse();
        await repository.Received().GetHistoryAsync(Arg.Is<JobHistoryFilter>(filter => filter.OwnerId == user.UserId && filter.Archive == "archived"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_Manager_PagesWithStableCursorAndNoOwnerFilter()
    {
        var repository = Substitute.For<IJobRepository>();
        var user = Substitute.For<ICurrentUser>();
        user.HasRole(RomdRoleType.Manager).Returns(true);
        var first = UploadJob.Create("first.zip");
        repository.GetHistoryAsync(Arg.Any<JobHistoryFilter>(), Arg.Any<CancellationToken>()).Returns([first, UploadJob.Create("second.zip")]);
        var handler = new GetJobHistoryQueryHandler(TestSystemCatalog.Create(), repository, user);
        var result = await handler.HandleAsync(new GetJobHistoryQuery(Limit: 1));
        result.Value.Items.Count.ShouldBe(1);
        result.Value.NextCursor.ShouldNotBeNull();
        await handler.HandleAsync(new GetJobHistoryQuery(Cursor: result.Value.NextCursor, Limit: 1));
        await repository.Received().GetHistoryAsync(Arg.Is<JobHistoryFilter>(filter => filter.OwnerId == null && filter.CursorId == first.Id && filter.CursorCreatedAt == first.CreatedAt), Arg.Any<CancellationToken>());
    }
}
