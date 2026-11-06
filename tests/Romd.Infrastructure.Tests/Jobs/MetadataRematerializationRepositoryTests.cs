using Romd.Infrastructure.Tests.Helpers;
using System.Diagnostics;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class MetadataRematerializationRepositoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Job_RestoresThroughEveryRead_AndGenericArchiveUpdatePreservesCheckpointState()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        db.Add(new PlatformEntity { Id = 1, Name = "System", ShortName = "system" });
        await db.SaveChangesAsync();
        var job = MetadataRematerializationJob.Create(1, "system", null, createdAt: DateTimeOffset.UtcNow);
        job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
        job.SetTotalTitles(2);
        job.RecordProcessed(10, occurredAt: DateTimeOffset.UtcNow);
        job.RecordProcessed(20, occurredAt: DateTimeOffset.UtcNow, "retained");
        job.Complete(completedAt: DateTimeOffset.UtcNow);
        db.Add(MetadataRematerializationJobEntity.FromDomain(job));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var repository = new JobRepository(db, TimeProvider.System);
        (await repository.GetByIdAsync(job.Id)).ShouldBeOfType<MetadataRematerializationJob>().CursorTitleId.ShouldBe(20);
        (await repository.GetRecentAsync()).Single().ShouldBeOfType<MetadataRematerializationJob>().DeferredCount.ShouldBe(1);
        (await repository.ListAsync(new JobListFilter(null, null, null, null, "active", null, null, null, null, 50, false))).Single().ShouldBeOfType<MetadataRematerializationJob>().ProcessedCount.ShouldBe(2);
        var history = new JobListFilter(null, null, null, "metadata-rematerialization", "active", null, null, null, null, 50);
        (await repository.ListAsync(history)).Single().ShouldBeOfType<MetadataRematerializationJob>();
        job.Archive(archivedAt: DateTimeOffset.UtcNow);
        await repository.CommitCheckpointAsync(db, job);
        var archived = (await repository.GetByIdAsync(job.Id)).ShouldBeOfType<MetadataRematerializationJob>();
        archived.IsArchived.ShouldBeTrue();
        archived.TotalTitles.ShouldBe(2);
        archived.CursorTitleId.ShouldBe(20);
        archived.Unarchive();
        await repository.CommitCheckpointAsync(db, archived);
        (await repository.ListAsync(history)).Single().IsArchived.ShouldBeFalse();
    }
    [Fact]
    public async Task TitleReader_TenThousandRows_UsesCompleteBoundedKeysetPagesAndExcludesOtherSystems()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        db.AddRange(new PlatformEntity { Id = 1, Name = "One", ShortName = "one" }, new PlatformEntity { Id = 2, Name = "Other", ShortName = "other" });
        await db.SaveChangesAsync();
        db.Titles.AddRange(Enumerable.Range(1, 10001).Select(id => new TitleEntity
        { Id = id, PlatformId = id == 10001 ? 2 : 1, Name = $"Title {id}", NormalizedName = $"title{id}", EnrichmentStatus = "NotEnriched" }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var reader = new MetadataRematerializationTitles(db);
        var all = new List<int>();
        int cursor = 0, queries = 0;
        var watch = Stopwatch.StartNew();
        (await reader.CountAsync(1)).ShouldBe(10000);
        while (true)
        {
            var page = await reader.ReadPageAsync(1, cursor, 500);
            queries++;
            page.Count.ShouldBeLessThanOrEqualTo(500);
            if (page.Count == 0)
                break;
            page.ShouldAllBe(id => id > cursor);
            all.AddRange(page);
            cursor = page[^1];
        }
        all.ShouldBe(Enumerable.Range(1, 10000));
        queries.ShouldBe(21);
        output.WriteLine($"Private paging observation: {all.Count} IDs, {queries} bounded page queries, elapsed {watch.Elapsed}.");
    }
    [Fact]
    public void Dispatch_MetadataFamily_UsesEnrichmentQueueAndTwoTransportAttempts()
    {
        var (invocation, queue) = JobDispatchService.CreateInvocation("metadata-rematerialization", Guid.NewGuid());
        queue.ShouldBe(JobQueues.Enrichment);
        invocation.Method.GetCustomAttributes(typeof(QueueAttribute), false).Cast<QueueAttribute>().Single().Queue.ShouldBe(queue);
        var retry = invocation.Method.GetCustomAttributes(typeof(AutomaticRetryAttribute), false).Cast<AutomaticRetryAttribute>().Single();
        retry.Attempts.ShouldBe(2);
        retry.OnAttemptsExceeded.ShouldBe(AttemptsExceededAction.Fail);
    }
}
