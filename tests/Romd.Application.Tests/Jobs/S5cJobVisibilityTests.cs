using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Domain.Identity;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Jobs;

public sealed class S5cJobVisibilityTests
{
    [Theory]
    [InlineData(RomdRoleType.User, false)]
    [InlineData(RomdRoleType.Manager, false)]
    [InlineData(RomdRoleType.Admin, false)]
    [InlineData(RomdRoleType.User, true)]
    [InlineData(RomdRoleType.Manager, true)]
    [InlineData(RomdRoleType.Admin, true)]
    public async Task MissingActor_NeverMatchesAnUnattributedJob(RomdRoleType role, bool empty)
    {
        var job = UploadJob.Create("unattributed.zip", createdAt: DateTimeOffset.UtcNow);
        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(empty ? Guid.Empty : null);
        user.HasRole(Arg.Any<RomdRoleType>()).Returns(call => role >= call.Arg<RomdRoleType>());
        JobVisibility.ScopeFor(user).Empty.ShouldBeTrue();
        (await repository.GetVisibleAsync(job.Id, user)).ShouldBeNull();
    }
}
