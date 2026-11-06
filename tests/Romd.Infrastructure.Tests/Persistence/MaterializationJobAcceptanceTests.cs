using Microsoft.EntityFrameworkCore;
using Romd.Domain.Jobs;
using Romd.Persistence;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class MaterializationJobAcceptanceTests
{
    [Fact]
    public async Task AddStagedAsync_NoCallerTransaction_RejectsBeforeTracking()
    {
        await using var context = CreateUnopenedContext();
        var repository = new MaterializationJobRepository(context, TimeProvider.System);
        var job = MaterializationJob.Create(7, "Arcade", createdAt: DateTimeOffset.UnixEpoch);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => repository.AddStagedAsync(job));

        error.Message.ShouldBe("Materialization job acceptance requires a caller-owned transaction.");
        context.ChangeTracker.Entries().ShouldBeEmpty();
        context.Database.GetDbConnection().State.ShouldBe(System.Data.ConnectionState.Closed);
    }

    [Fact]
    public async Task AddStagedAsync_CancelledBeforeTransactionGuard_PreservesCancellationWithoutTracking()
    {
        await using var context = CreateUnopenedContext();
        var repository = new MaterializationJobRepository(context, TimeProvider.System);
        var job = MaterializationJob.Create(7, "Arcade", createdAt: DateTimeOffset.UnixEpoch);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Should.ThrowAsync<OperationCanceledException>(() =>
            repository.AddStagedAsync(job, cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        context.ChangeTracker.Entries().ShouldBeEmpty();
        context.Database.GetDbConnection().State.ShouldBe(System.Data.ConnectionState.Closed);
    }

    private static RomdDbContext CreateUnopenedContext() => new(new DbContextOptionsBuilder<RomdDbContext>()
        .UseNpgsql("Host=127.0.0.1;Database=unused;Username=unused")
        .Options);
}
