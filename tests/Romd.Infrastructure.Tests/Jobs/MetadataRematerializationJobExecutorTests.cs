using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Persistence;
using Romd.Persistence.Enrichment;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class MetadataRematerializationJobExecutorTests : IDisposable
{
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    public void Dispose() => database.Dispose();
    private ServiceProvider Provider(Func<Guid, int, Task> action, bool failFallback = false)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => database.CreateContext());
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IRematerializationScheduler>(sp => failFallback ? new FailingScheduler() :
            new RematerializationScheduler(sp.GetRequiredService<RomdDbContext>(), TimeProvider.System));
        services.AddScoped<IRematerializationService>(_ => new Work(action));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
    [Fact]
    public async Task Execute_MultiplePages_VisitsEveryIdInFreshScope_AndPersistsFallbackBeforeProgress()
    {
        var visited = new List<(Guid Scope, int Id)>();
        using var provider = Provider((scope, id) =>
        {
            visited.Add((scope, id));
            return id is 500 or 501 or 1001 ? Task.FromException(new IOException("metadata unavailable")) : Task.CompletedTask;
        });
        var titles = new Titles(1001);
        var job = MetadataRematerializationJob.Create(1, "system", null, createdAt: DateTimeOffset.UtcNow);
        job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
        var checkpoints = new List<int>();
        var context = new JobContext(null, async _ =>
        {
            await using var db = database.CreateContext();
            foreach (int failed in new[] { 500, 501, 1001 }.Where(id => id <= job.CursorTitleId))
                (await db.MetadataRematerializationRequests.AnyAsync(x => x.TitleId == failed)).ShouldBeTrue();
            checkpoints.Add(job.CursorTitleId);
        }, CancellationToken.None);
        await new MetadataRematerializationJobExecutor(titles, provider.GetRequiredService<IServiceScopeFactory>(), clock: TimeProvider.System).ExecuteAsync(job, context);
        job.Complete(completedAt: DateTimeOffset.UtcNow);
        visited.Select(x => x.Id).ShouldBe(Enumerable.Range(1, 1001));
        visited.Select(x => x.Scope).Distinct().Count().ShouldBe(1001);
        titles.Cursors.ShouldBe([0, 500, 1000, 1001]);
        job.ProcessedCount.ShouldBe(1001);
        job.TotalTitles.ShouldBe(1001);
        job.DeferredCount.ShouldBe(3);
        job.Phase.ShouldBe("CompletedWithErrors");
        job.Errors.Count.ShouldBe(3);
        checkpoints.ShouldBe(Enumerable.Range(0, 1002));
    }
    [Theory]
    [InlineData("count")]
    [InlineData("enumeration")]
    [InlineData("checkpoint")]
    [InlineData("fallback")]
    public async Task Execute_InfrastructureFault_PreservesCursorUntilTitleOrIntentIsCommitted(string fault)
    {
        using var provider = Provider((_, _) => fault == "fallback" ? Task.FromException(new IOException("title")) : Task.CompletedTask,
            failFallback: fault == "fallback");
        var reader = new Titles(2) { Fault = fault };
        var job = MetadataRematerializationJob.Create(1, "system", null, createdAt: DateTimeOffset.UtcNow);
        job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
        var context = new JobContext(null, _ => fault == "checkpoint" ? Task.FromException(new IOException("checkpoint")) : Task.CompletedTask, CancellationToken.None);
        await Should.ThrowAsync<IOException>(() => new MetadataRematerializationJobExecutor(reader, provider.GetRequiredService<IServiceScopeFactory>(), clock: TimeProvider.System).ExecuteAsync(job, context));
        job.CursorTitleId.ShouldBe(0);
        job.ProcessedCount.ShouldBe(0);
        job.DeferredCount.ShouldBe(0);
        job.Errors.ShouldBeEmpty();
        await using var assert = database.CreateContext();
        (await assert.MetadataRematerializationRequests.CountAsync()).ShouldBe(0);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_CancellationOrOwnershipLoss_IsNeverDeferredTitleFailure(bool ownership)
    {
        using var provider = Provider((_, _) => Task.FromException(ownership ? new JobExecutionOwnershipLostException() : new OperationCanceledException()));
        var job = MetadataRematerializationJob.Create(1, "system", null, createdAt: DateTimeOffset.UtcNow);
        job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
        var task = () => new MetadataRematerializationJobExecutor(new Titles(1), provider.GetRequiredService<IServiceScopeFactory>(), clock: TimeProvider.System)
            .ExecuteAsync(job, new JobContext(null, _ => Task.CompletedTask, CancellationToken.None));
        if (ownership)
            await Should.ThrowAsync<JobExecutionOwnershipLostException>(task);
        else
            await Should.ThrowAsync<OperationCanceledException>(task);
        job.CursorTitleId.ShouldBe(0);
        job.Errors.ShouldBeEmpty();
        await using var db = database.CreateContext();
        (await db.MetadataRematerializationRequests.CountAsync()).ShouldBe(0);
    }
    [Fact]
    public async Task Execute_CheckpointFailsAfterDurableTitle_RestartRepeatsThatTitleWithoutSkippingLaterWork()
    {
        var calls = new List<int>();
        using var provider = Provider((_, id) => { calls.Add(id); return Task.CompletedTask; });
        var persisted = MetadataRematerializationJob.Create(1, "system", null, createdAt: DateTimeOffset.UtcNow);
        persisted.Start("delivery", startedAt: DateTimeOffset.UtcNow);
        var attempt = Romd.Persistence.Entities.MetadataRematerializationJobEntity.FromDomain(persisted).ToDomain();
        var context = new JobContext(null, _ => attempt.CursorTitleId == 1 ? Task.FromException(new IOException("checkpoint")) : Task.CompletedTask, CancellationToken.None);
        await Should.ThrowAsync<IOException>(() => new MetadataRematerializationJobExecutor(new Titles(2), provider.GetRequiredService<IServiceScopeFactory>(), clock: TimeProvider.System).ExecuteAsync(attempt, context));
        persisted.CursorTitleId.ShouldBe(0);
        await new MetadataRematerializationJobExecutor(new Titles(2), provider.GetRequiredService<IServiceScopeFactory>(), clock: TimeProvider.System)
            .ExecuteAsync(persisted, new JobContext(null, _ => Task.CompletedTask, CancellationToken.None));
        calls.ShouldBe([1, 1, 2]);
        persisted.CursorTitleId.ShouldBe(2);
    }
    [Fact]
    public async Task Execute_FallbackCommitFails_DoesNotAdvanceCursorOrRecordDeferredProgress()
    {
        await using (var db = database.CreateContext())
            await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION romd.fail_metadata_intent() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected fallback persistence failure'; END $$;
            CREATE TRIGGER fail_metadata_intent BEFORE INSERT ON romd."MetadataRematerializationRequests"
                FOR EACH ROW EXECUTE FUNCTION romd.fail_metadata_intent();
            """);
        using var provider = Provider((_, _) => Task.FromException(new IOException("title failed")));
        var job = MetadataRematerializationJob.Create(1, "system", null, createdAt: DateTimeOffset.UtcNow);
        job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
        await Should.ThrowAsync<DbUpdateException>(() => new MetadataRematerializationJobExecutor(new Titles(1), provider.GetRequiredService<IServiceScopeFactory>(), clock: TimeProvider.System)
            .ExecuteAsync(job, new JobContext(null, _ => Task.CompletedTask, CancellationToken.None)));
        job.CursorTitleId.ShouldBe(0);
        job.ProcessedCount.ShouldBe(0);
        job.DeferredCount.ShouldBe(0);
        job.Errors.ShouldBeEmpty();
        await using var assert = database.CreateContext();
        (await assert.MetadataRematerializationRequests.CountAsync()).ShouldBe(0);
    }

    private sealed class FailingScheduler : IRematerializationScheduler
    { public Task EnqueueTitleAsync(int titleId, CancellationToken ct = default) => throw new IOException("fallback persistence"); }
    private sealed class Work(Func<Guid, int, Task> action) : IRematerializationService
    {
        private readonly Guid scope = Guid.NewGuid();
        public Task RematerializeTitleAsync(int titleId, CancellationToken ct = default) => action(scope, titleId);
    }
    private sealed class Titles(int count) : IMetadataRematerializationTitles
    {
        public List<int> Cursors { get; } = [];
        public string? Fault { get; init; }
        public Task<int> CountAsync(int platformId, CancellationToken ct = default) => Fault == "count" ? throw new IOException("count") : Task.FromResult(count);
        public Task<IReadOnlyList<int>> ReadPageAsync(int platformId, int afterTitleId, int limit, CancellationToken ct = default)
        {
            if (Fault == "enumeration")
                throw new IOException("enumeration");
            Cursors.Add(afterTitleId);
            limit.ShouldBe(500);
            return Task.FromResult<IReadOnlyList<int>>(Enumerable.Range(1, count).Where(id => id > afterTitleId).Take(limit).ToArray());
        }
    }
}
