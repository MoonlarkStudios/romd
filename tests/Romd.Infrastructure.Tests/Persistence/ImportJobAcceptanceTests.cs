using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class ImportJobAcceptanceTests
{
    private static readonly DateTimeOffset At = new DateTimeOffset(2026, 11, 4, 9, 8, 7, TimeSpan.FromHours(-4)).AddTicks(1234560);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddStagedAsync_NoTransactionRejectsBeforeTrackingOrOpeningDatabase(bool replacement)
    {
        await using var context = UnopenedContext();

        await Should.ThrowAsync<InvalidOperationException>(() => StageAsync(context, CreateJob(replacement)));

        context.ChangeTracker.Entries().ShouldBeEmpty();
        context.Database.GetDbConnection().State.ShouldBe(System.Data.ConnectionState.Closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddStagedAsync_CancelledTokenPreservesCancellationBeforeTransactionGuard(bool replacement)
    {
        await using var context = UnopenedContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Should.ThrowAsync<OperationCanceledException>(() =>
            StageAsync(context, CreateJob(replacement), cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        context.ChangeTracker.Entries().ShouldBeEmpty();
        context.Database.GetDbConnection().State.ShouldBe(System.Data.ConnectionState.Closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddStagedAsync_PostgreSqlOwnerFlushAndRollbackKeepJobAndDispatchInvisible(bool replacement)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var context = database.CreateContext();
        var owner = new EfUnitOfWork(context);
        await using var transaction = await owner.BeginTransactionAsync();
        var job = CreateJob(replacement);

        await StageAsync(context, job);

        context.ChangeTracker.Entries<JobEntity>().ShouldHaveSingleItem().State.ShouldBe(EntityState.Added);
        await AssertEmptyAsync(database);
        await owner.FlushAsync();
        (await context.Jobs.CountAsync()).ShouldBe(1);
        (await context.JobDispatches.CountAsync()).ShouldBe(1);
        await AssertEmptyAsync(database);
        await transaction.RollbackAsync();

        context.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertEmptyAsync(database);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddStagedAsync_PostgreSqlOwnerCommitPublishesExactJobAndDurableDispatch(bool replacement)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var context = database.CreateContext();
        var owner = new EfUnitOfWork(context);
        await using var transaction = await owner.BeginTransactionAsync();
        var job = CreateJob(replacement);

        await StageAsync(context, job);
        await transaction.CommitAsync();

        await using var read = database.CreateContext();
        var row = await read.Jobs.SingleAsync();
        row.Id.ShouldBe(job.Id);
        row.CorrelationId.ShouldBe(job.CorrelationId);
        row.SourceFilename.ShouldBe(job.SourceFilename);
        row.Phase.ShouldBe("Pending");
        row.CreatedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(At.ToUnixTimeMilliseconds()));
        var dispatch = await read.JobDispatches.SingleAsync();
        dispatch.JobId.ShouldBe(job.Id);
        dispatch.JobType.ShouldBe(row.JobType);
        dispatch.CreatedAtUtc.ShouldBe(row.CreatedAt);
        dispatch.AvailableAtUtc.ShouldBe(row.CreatedAt);
        job.CreatedAt.ShouldBe(At);
        job.CreatedAt.Offset.ShouldBe(At.Offset);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AddStagedAsync_PostgreSqlFaultOrCancellationAfterActualFlushRollsBackBoth(bool replacement, bool cancel)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var context = database.CreateContext();
        var owner = new EfUnitOfWork(context);
        using var cancellation = new CancellationTokenSource();
        var fault = new IOException("fault after job and dispatch SQL");
        async Task AcceptAsync()
        {
            await using var transaction = await owner.BeginTransactionAsync(cancellation.Token);
            await StageAsync(context, CreateJob(replacement), cancellation.Token);
            await owner.FlushAsync(cancellation.Token);
            (await context.JobDispatches.CountAsync()).ShouldBe(1);
            if (cancel)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            throw fault;
        }

        if (cancel)
            (await Should.ThrowAsync<OperationCanceledException>(AcceptAsync)).CancellationToken.ShouldBe(cancellation.Token);
        else
            (await Should.ThrowAsync<IOException>(AcceptAsync)).ShouldBeSameAs(fault);

        context.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertEmptyAsync(database);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestingComposition_PostgreSqlStagingAndUnitOfWorkUseSameScopedContext(bool replacement)
    {
        using var database = PostgreSqlTestDatabase.Create();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRomdTestingPersistence(database.ConnectionString);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var transaction = await owner.BeginTransactionAsync();
        var job = CreateJob(replacement);

        if (job is ReplaceDatJob replace)
            await scope.ServiceProvider.GetRequiredService<IReplaceDatJobAcceptance>().AddStagedAsync(replace);
        else
            await scope.ServiceProvider.GetRequiredService<IUploadJobAcceptance>().AddStagedAsync((UploadJob)job);
        await owner.FlushAsync();
        await AssertEmptyAsync(database);
        await transaction.CommitAsync();

        await using var read = database.CreateContext();
        (await read.Jobs.SingleAsync()).Id.ShouldBe(job.Id);
        (await read.JobDispatches.SingleAsync()).JobId.ShouldBe(job.Id);
    }

    private static Job CreateJob(bool replacement) => replacement
        ? ReplaceDatJob.Create(7, "catalog.dat", createdAt: At)
        : UploadJob.Create("pack.zip", createdAt: At);

    private static Task StageAsync(RomdDbContext context, Job job, CancellationToken ct = default) => job switch
    {
        ReplaceDatJob replace => new ReplaceDatJobRepository(context, new ManualTimeProvider(At)).AddStagedAsync(replace, ct),
        UploadJob upload => new UploadJobRepository(context, new ManualTimeProvider(At)).AddStagedAsync(upload, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(job))
    };

    private static async Task AssertEmptyAsync(PostgreSqlTestDatabase database)
    {
        await using var read = database.CreateContext();
        (await read.Jobs.CountAsync()).ShouldBe(0);
        (await read.JobDispatches.CountAsync()).ShouldBe(0);
    }

    private static RomdDbContext UnopenedContext() => new(new DbContextOptionsBuilder<RomdDbContext>()
        .UseNpgsql("Host=127.0.0.1;Database=unused;Username=unused").Options);
}
