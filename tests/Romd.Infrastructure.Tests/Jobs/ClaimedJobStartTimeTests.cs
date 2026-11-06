using Romd.Persistence;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class ClaimedJobStartTimeTests
{
    [Fact]
    public async Task TryClaimExecutionAsync_ClockAdvancesWhileReadingClaimedJob_RecordsLaterStartAndPreservesLease()
    {
        var before = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var after = before.AddSeconds(2);
        var clock = new ClaimClock(before);
        using var database = PostgreSqlTestDatabase.Create();
        await using var context = database.CreateContext();
        var inner = new UploadJobRepository(context, clock);
        var job = UploadJob.Create("upload.zip", createdAt: before);
        await using (var fixtureAcceptance = await new EfUnitOfWork(context).BeginTransactionAsync())
        {
            await inner.AddStagedAsync(job);
            await fixtureAcceptance.CommitAsync();
        }
        context.ChangeTracker.Clear();
        var repository = Substitute.For<IJobRepository<UploadJob>>();
        repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var loaded = await inner.GetByIdAsync(job.Id, call.Arg<CancellationToken>());
            clock.UtcNow = after;
            return loaded;
        });
        repository.UpdateStagedAsync(Arg.Any<UploadJob>(), Arg.Any<CancellationToken>())
            .Returns(call => inner.UpdateStagedAsync(call.Arg<UploadJob>(), call.Arg<CancellationToken>()));
        var options = new JobExecutionClaimOptions();
        var claims = new ClaimedJobRepository<UploadJob, IJobRepository<UploadJob>>(repository, context, clock, options);

        var claim = await claims.TryClaimExecutionAsync(job.Id, "delivery");

        claim.ShouldNotBeNull().Job.StartedAt.ShouldBe(after);
        await using var observer = database.CreateContext();
        var saved = await observer.Jobs.SingleAsync();
        saved.StartedAt.ShouldBe(after);
        saved.ExecutionLeaseExpiresAtUtc.ShouldBe(before + options.LeaseDuration);
        saved.ExecutionFenceToken.ShouldBe(claim.FenceToken);
        saved.HangfireJobId.ShouldBe("delivery");
    }

    private sealed class ClaimClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
