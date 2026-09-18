using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobItems;
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

public sealed class JobItemReadSnapshotTests : IAsyncLifetime
{
    private const int TitleCount = BoundedIdQuery.MaxIdsPerBatch + 1;
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private Guid _jobId;

    public async Task InitializeAsync()
    {
        await using var db = CreateDb();
        _jobId = Seed(db);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task HandleAsync_TitleChangesBetweenBatches_ReturnsOneCoherentSnapshot()
    {
        var pause = new PauseBeforeSecondTitleLookupInterceptor();
        await using var readDb = CreateDb(pause);
        var handler = new GetJobItemsQueryHandler(
            new JobItemRepository(readDb),
            new EfReadSnapshotTransactionFactory(readDb));

        var readTask = handler.HandleAsync(new GetJobItemsQuery(_jobId, null, 10));
        await pause.SecondLookupReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // PostgreSQL never blocks the writer: it commits between the reader's two batches, and the
        // REPEATABLE READ snapshot must keep the reader on the generation it opened with.
        (await UpdateLastTitleAsync()).ShouldBe(1);

        pause.Resume.TrySetResult();
        var result = await readTask;

        var titles = result.Value.Items.ShouldHaveSingleItem().MatchedTitles;
        titles.Count.ShouldBe(TitleCount);
        titles.ShouldContain(title => title.TitleId == TitleCount && title.Name == $"Title {TitleCount}");
        titles.ShouldNotContain(title => title.Name == "Changed title");
    }

    [Fact]
    public async Task ExportHandleAsync_TitleChangesBetweenBatches_ReturnsOneCoherentSnapshot()
    {
        var pause = new PauseBeforeSecondTitleLookupInterceptor();
        await using var readDb = CreateDb(pause);
        var handler = new ExportJobItemsQueryHandler(
            new JobItemRepository(readDb),
            new EfReadSnapshotTransactionFactory(readDb));

        var readTask = handler.HandleAsync(new ExportJobItemsQuery(_jobId));
        await pause.SecondLookupReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // PostgreSQL never blocks the writer: it commits between the reader's two batches, and the
        // REPEATABLE READ snapshot must keep the reader on the generation it opened with.
        (await UpdateLastTitleAsync()).ShouldBe(1);

        pause.Resume.TrySetResult();
        var result = await readTask;

        var titles = result.Value.ShouldHaveSingleItem().MatchedTitles;
        titles.Count.ShouldBe(TitleCount);
        titles.ShouldContain(title => title.TitleId == TitleCount && title.Name == $"Title {TitleCount}");
        titles.ShouldNotContain(title => title.Name == "Changed title");
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    private RomdDbContext CreateDb(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_database.ConnectionString);
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new RomdDbContext(builder.Options);
    }

    private async Task<int> UpdateLastTitleAsync()
    {
        await using var writeDb = CreateDb();
        return await writeDb.Titles
            .Where(title => title.Id == TitleCount)
            .ExecuteUpdateAsync(setters => setters.SetProperty(title => title.Name, "Changed title"));
    }

    private static Guid Seed(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        db.Platforms.Add(new PlatformEntity { Id = 1, Name = "Test", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test", BaseCompactLabel = "Test", CanonicalKey = "test", ShortName = "test", CreatedAt = now });
        for (int id = 1; id <= TitleCount; id++)
        {
            db.Titles.Add(new TitleEntity
            {
                Id = id,
                PlatformId = 1,
                Name = $"Title {id}",
                NormalizedName = $"title{id}",
                EnrichmentStatus = EnrichmentStatus.None.ToString(),
                CreatedAt = now
            });
        }

        var job = UploadJobEntity.FromDomain(UploadJob.Create("snapshot.zip"));
        db.Jobs.Add(job);
        db.JobItems.Add(JobItemEntity.FromDomain(JobItem.ForRom(
            job.Id,
            "many.bin",
            1024,
            RomIngestOutcome.Ingested,
            romFileId: null,
            Enumerable.Range(1, TitleCount).ToList(),
            platformId: 1,
            error: null)));
        return job.Id;
    }

    private sealed class PauseBeforeSecondTitleLookupInterceptor : DbCommandInterceptor
    {
        private int _titleLookupCount;
        public TaskCompletionSource SecondLookupReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM romd.\"Titles\"", StringComparison.Ordinal) &&
                Interlocked.Increment(ref _titleLookupCount) == 2)
            {
                SecondLookupReached.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
