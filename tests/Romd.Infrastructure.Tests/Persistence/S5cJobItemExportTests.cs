using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class S5cJobItemExportTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    [Fact]
    public async Task HandleAsync_MoreThanOneHundredThousandItems_ExportsEveryItemExactlyOnce()
    {
        const int count = 100_001;
        var jobId = await SeedAsync(count);
        var commands = new LedgerReadInterceptor();
        await using var db = CreateDb(commands);
        var handler = Handler(db);

        var result = await handler.HandleAsync(new ExportJobItemsQuery(jobId));

        result.IsError.ShouldBeFalse();
        await using var export = result.Value;
        var ids = new HashSet<Guid>();
        int exportedCount = 0;
        await foreach (var item in export.ReadAsync())
        {
            exportedCount++;
            ids.Add(item.Id).ShouldBeTrue();
        }
        exportedCount.ShouldBe(count);
        ids.Count.ShouldBe(count);
        commands.Limits.Count.ShouldBe(101);
        commands.Limits.ShouldAllBe(limit => limit == JobItemExport.BatchSize + 1);
        db.Database.CurrentTransaction.ShouldNotBeNull();
        await export.CompleteAsync();
    }

    [Fact]
    public async Task ReadAsync_WritesAndNameChangesBetweenPages_RetainsEntireOriginalSnapshot()
    {
        var jobId = await SeedAsync(1001, withNames: true);
        var commands = new LedgerReadInterceptor(pauseSecond: true);
        await using var db = CreateDb(commands);
        var result = await Handler(db).HandleAsync(new ExportJobItemsQuery(jobId));
        await using var export = result.Value;
        var readTask = CollectAsync(export);
        await commands.SecondReadReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await using var writer = _database.CreateContext();
            (await writer.Titles.Where(title => title.Id == 1).ExecuteUpdateAsync(setters => setters.SetProperty(title => title.Name, "Changed title"))).ShouldBe(1);
            (await writer.Platforms.Where(platform => platform.Id == 1).ExecuteUpdateAsync(setters => setters.SetProperty(platform => platform.ShortName, "changed"))).ShouldBe(1);
            (await writer.JobItems.Where(item => item.JobId == jobId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.FileName, "changed.bin"))).ShouldBe(1001);
            writer.JobItems.Add(JobItemEntity.FromDomain(JobItem.ForRom(jobId, "new.bin", 9, RomIngestOutcome.Ingested, null, [1], 1, null, createdAt: DateTimeOffset.UtcNow)));
            await writer.SaveChangesAsync();
        }
        finally
        {
            commands.Resume.TrySetResult();
        }
        var items = await readTask;
        items.Count.ShouldBe(1001);
        items.ShouldAllBe(item => item.PlatformName == "original" && item.MatchedTitles.Single().Name == "Original title");
        items.ShouldAllBe(item => item.FileName.StartsWith("file-", StringComparison.Ordinal));
        await export.CompleteAsync();
    }

    [Fact]
    public async Task ReadAsync_CancellationAfterFirstPage_RollsBackAndReleasesSnapshot()
    {
        var jobId = await SeedAsync(1001);
        await using var db = CreateDb();
        using var cancellation = new CancellationTokenSource();
        var result = await Handler(db).HandleAsync(new ExportJobItemsQuery(jobId), cancellation.Token);
        var export = result.Value;
        await using (var iterator = export.ReadAsync(cancellation.Token).GetAsyncEnumerator())
        {
            for (int index = 0; index < JobItemExport.BatchSize; index++)
                (await iterator.MoveNextAsync()).ShouldBeTrue();
            await cancellation.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
        }
        await Should.ThrowAsync<InvalidOperationException>(() => export.CompleteAsync());
        await export.DisposeAsync();
        db.Database.CurrentTransaction.ShouldBeNull();
        (await db.JobItems.CountAsync(item => item.JobId == jobId)).ShouldBe(1001);
    }

    [Fact]
    public async Task ReadAsync_LaterPageFails_RollsBackAndReleasesSnapshot()
    {
        var jobId = await SeedAsync(1001);
        var commands = new LedgerReadInterceptor(failSecond: true);
        await using var db = CreateDb(commands);
        var result = await Handler(db).HandleAsync(new ExportJobItemsQuery(jobId));
        var export = result.Value;
        int count = 0;
        await Should.ThrowAsync<IOException>(async () =>
        {
            await foreach (var _ in export.ReadAsync())
                count++;
        });
        count.ShouldBe(JobItemExport.BatchSize);
        await export.DisposeAsync();
        db.Database.CurrentTransaction.ShouldBeNull();
        (await db.JobItems.CountAsync(item => item.JobId == jobId)).ShouldBe(1001);
    }

    [Fact]
    public async Task HandleAsync_FirstPageFails_RollsBackBeforeReturningLease()
    {
        var jobId = await SeedAsync(1);
        await using var db = CreateDb(new LedgerReadInterceptor(failFirst: true));
        await Should.ThrowAsync<IOException>(() => Handler(db).HandleAsync(new ExportJobItemsQuery(jobId)));
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    public void Dispose() => _database.Dispose();

    private RomdDbContext CreateDb(IInterceptor? interceptor = null) => new(_database.CreateOptions(options =>
    {
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
    }));

    private static ExportJobItemsQueryHandler Handler(RomdDbContext db) =>
        new(new JobItemRepository(db), new EfReadSnapshotTransactionFactory(db));

    private static async Task<List<JobItemView>> CollectAsync(JobItemExport export)
    {
        var items = new List<JobItemView>();
        await foreach (var item in export.ReadAsync())
            items.Add(item);
        return items;
    }

    private async Task<Guid> SeedAsync(int count, bool withNames = false)
    {
        await using var db = _database.CreateContext();
        var job = UploadJobEntity.FromDomain(UploadJob.Create("complete-export.zip", createdAt: DateTimeOffset.UtcNow));
        db.Jobs.Add(job);
        if (withNames)
        {
            db.Platforms.Add(new PlatformEntity { Id = 1, Name = "Original", ShortName = "original", CreatedAt = DateTimeOffset.UtcNow });
            db.Titles.Add(new TitleEntity { Id = 1, PlatformId = 1, Name = "Original title", NormalizedName = "originaltitle", EnrichmentStatus = EnrichmentStatus.None.ToString(), CreatedAt = DateTimeOffset.UtcNow });
        }
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."JobItems"
                ("Id", "JobId", "Kind", "FileName", "SizeBytes", "Outcome", "MatchedTitleIdsJson", "PlatformId", "CreatedAt")
            SELECT md5(index::text)::uuid, {job.Id}, 0, 'file-' || index || '.bin', index, 0,
                {(withNames ? "[1]" : "[]")}, {(withNames ? (int?)1 : null)}, {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}
            FROM generate_series(1, {count}) AS index
            """);
        return job.Id;
    }

    private sealed class LedgerReadInterceptor(bool pauseSecond = false, bool failSecond = false, bool failFirst = false) : DbCommandInterceptor
    {
        public List<int> Limits { get; } = [];
        public TaskCompletionSource SecondReadReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM romd.\"JobItems\"", StringComparison.Ordinal)
                && command.CommandText.Contains("LIMIT", StringComparison.Ordinal))
            {
                Limits.Add(command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).OfType<int>().Single());
                if (Limits.Count == 1 && failFirst || Limits.Count == 2 && failSecond)
                    throw new IOException("Injected export page failure.");
                if (Limits.Count == 2 && pauseSecond)
                {
                    SecondReadReached.TrySetResult();
                    await Resume.Task.WaitAsync(cancellationToken);
                }
            }
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
