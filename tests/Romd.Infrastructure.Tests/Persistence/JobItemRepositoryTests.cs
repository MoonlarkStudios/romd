using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>
///     Verifies per-file provenance is stored as a minimal snapshot and that the read side resolves
///     the snapshot title/platform ids to display names.
/// </summary>
public sealed class JobItemRepositoryTests : IDisposable
{
    private const int PlatformId = 1;
    private const int MaxIdsPerLookup = BoundedIdQuery.MaxIdsPerBatch;
    private readonly PostgreSqlTestDatabase _connection;
    private readonly CommandParameterCaptureInterceptor _commands = new();
    private readonly DbContextOptions<RomdDbContext> _contextOptions;
    private readonly Guid _jobId;

    public JobItemRepositoryTests()
    {
        _connection = PostgreSqlTestDatabase.Create();
        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .AddInterceptors(_commands)
            .Options;

        using var db = CreateDb();
        _jobId = Seed(db);
    }

    [Fact]
    public async Task GetByJobAsync_ResolvesTitleAndPlatformNames()
    {
        using var db = CreateDb();
        var repository = new JobItemRepository(db);

        await repository.AddRangeAsync([
            JobItem.ForRom(_jobId, "nhl94.bin", 1024, RomIngestOutcome.Ingested, romFileId: 10, [1, 2], PlatformId, null, archiveOnly: false),
            JobItem.ForRom(_jobId, "dupe.bin", 2048, RomIngestOutcome.Deduplicated, romFileId: 10, [1], PlatformId, null, archiveOnly: true),
            JobItem.ForRom(_jobId, "mystery.bin", 512, RomIngestOutcome.Rejected, null, null, null, null),
            JobItem.ForDat(_jobId, "snes.dat", 4096, success: true, routed: true, PlatformId, datFileId: 5, gameCount: 100, null),
        ]);

        var page = await repository.GetByJobAsync(_jobId, outcome: null, limit: 100);

        page.HasMore.ShouldBeFalse();
        page.Items.Count.ShouldBe(4);

        var ingested = page.Items.Single(i => i.Outcome == JobItemOutcome.Ingested);
        ingested.PlatformName.ShouldBe("snes");
        ingested.MatchedTitles.Select(t => t.Name).ShouldBe(["Title 1", "Title 2"], ignoreOrder: true);
        ingested.RomFileId.ShouldBe(10);
        ingested.ArchiveOnly.ShouldBe(false);

        var deduplicated = page.Items.Single(i => i.Outcome == JobItemOutcome.Deduplicated);
        deduplicated.ArchiveOnly.ShouldBe(true);

        var dat = page.Items.Single(i => i.Kind == JobItemKind.Dat);
        dat.Outcome.ShouldBe(JobItemOutcome.DatRouted);
        dat.GameCount.ShouldBe(100);
        dat.PlatformName.ShouldBe("snes");
        dat.ArchiveOnly.ShouldBeNull();

        var rejected = page.Items.Single(i => i.Outcome == JobItemOutcome.Rejected);
        rejected.MatchedTitles.ShouldBeEmpty();
        rejected.PlatformName.ShouldBeNull();
    }

    [Fact]
    public async Task GetByJobAsync_FiltersByOutcome()
    {
        using var db = CreateDb();
        var repository = new JobItemRepository(db);

        await repository.AddRangeAsync([
            JobItem.ForRom(_jobId, "a.bin", 1, RomIngestOutcome.Ingested, 10, [1], PlatformId, null),
            JobItem.ForRom(_jobId, "b.bin", 1, RomIngestOutcome.Rejected, null, null, null, null),
        ]);

        var page = await repository.GetByJobAsync(_jobId, JobItemOutcome.Rejected, limit: 100);

        page.Items.Count.ShouldBe(1);
        page.Items[0].FileName.ShouldBe("b.bin");
    }

    [Fact]
    public async Task GetByJobAsync_SignalsHasMore_WhenCapped()
    {
        using var db = CreateDb();
        var repository = new JobItemRepository(db);

        await repository.AddRangeAsync([
            JobItem.ForRom(_jobId, "a.bin", 1, RomIngestOutcome.Ingested, 10, [], null, null),
            JobItem.ForRom(_jobId, "b.bin", 1, RomIngestOutcome.Ingested, 11, [], null, null),
            JobItem.ForRom(_jobId, "c.bin", 1, RomIngestOutcome.Ingested, 12, [], null, null),
        ]);

        var page = await repository.GetByJobAsync(_jobId, outcome: null, limit: 2);

        page.Items.Count.ShouldBe(2);
        page.HasMore.ShouldBeTrue();
    }

    [Fact]
    public async Task GetByJobAsync_CursorAndSearch_ReturnEveryMatchingItemOnce()
    {
        using var db = CreateDb();
        var repository = new JobItemRepository(db);
        var items = Enumerable.Range(0, 5).Select(index =>
            JobItem.ForRom(_jobId, $"folder/Game_{index}.bin", 1, RomIngestOutcome.Rejected, null, [], null, null)).ToList();
        await repository.AddRangeAsync(items);
        // Force a timestamp tie so pagination must also use the unique ID.
        var instant = DateTimeOffset.UtcNow;
        await db.JobItems.Where(item => item.JobId == _jobId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CreatedAt, instant));
        var seen = new List<Guid>();
        Guid? cursor = null;
        do
        {
            var page = await repository.GetByJobAsync(_jobId, JobItemOutcome.Rejected, 2, cursor: cursor, search: " GAME_ ");
            seen.AddRange(page.Items.Select(item => item.Id));
            cursor = page.NextCursor is null ? null : Guid.Parse(page.NextCursor);
        } while (cursor.HasValue);
        seen.Count.ShouldBe(5);
        seen.Distinct().Count().ShouldBe(5);
        (await repository.GetByJobAsync(_jobId, null, 10, search: "%")).Items.ShouldBeEmpty();
        (await repository.GetByJobAsync(Guid.NewGuid(), null, 10, cursor: seen[0])).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetByJobAsync_MoreThanLookupCeiling_BoundsTitleIdsAndPreservesResolutionSemantics()
    {
        const int titleCount = MaxIdsPerLookup + 1;
        await SeedAdditionalTitlesAsync(titleCount);

        var matchedTitleIds = new[] { titleCount, 1, titleCount }
            .Concat(Enumerable.Range(2, titleCount - 1))
            .Append(99_999)
            .ToList();

        using (var db = CreateDb())
        {
            var repository = new JobItemRepository(db);
            await repository.AddRangeAsync([
                JobItem.ForRom(
                    _jobId,
                    "many-matches.bin",
                    1024,
                    RomIngestOutcome.Ingested,
                    romFileId: 10,
                    matchedTitleIds,
                    PlatformId,
                    null)
            ]);
        }

        _commands.Clear();
        using (var db = CreateDb())
        {
            var repository = new JobItemRepository(db);

            var page = await repository.GetByJobAsync(_jobId, outcome: null, limit: 100);

            page.Items.ShouldHaveSingleItem().MatchedTitles.Select(title => title.TitleId).ShouldBe(
                matchedTitleIds.Where(id => id != 99_999));
        }

        var titleCommands = _commands.Commands
            .Where(command => command.CommandText.Contains("FROM romd.\"Titles\"", StringComparison.Ordinal))
            .ToList();
        titleCommands.Count.ShouldBe(2);
        titleCommands.ShouldAllBe(command => command.ParameterCount <= MaxIdsPerLookup,
            string.Join(Environment.NewLine, titleCommands.Select(command =>
                $"{command.ParameterCount}: {string.Join(",", command.ParameterNames)}")));
    }

    [Fact]
    public async Task GetByJobAsync_CancellationBetweenTitleBatches_StopsBeforeRemainingResolution()
    {
        const int titleCount = MaxIdsPerLookup + 1;
        await SeedAdditionalTitlesAsync(titleCount);

        using (var db = CreateDb())
        {
            var repository = new JobItemRepository(db);
            await repository.AddRangeAsync([
                JobItem.ForRom(
                    _jobId,
                    "cancel.bin",
                    1024,
                    RomIngestOutcome.Ingested,
                    romFileId: 10,
                    Enumerable.Range(1, titleCount).ToList(),
                    PlatformId,
                    null)
            ]);
        }

        using var cancellation = new CancellationTokenSource();
        var pause = new PauseBeforeSecondTitleLookupInterceptor();
        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .AddInterceptors(pause)
            .Options;
        using var readDb = new RomdDbContext(options);
        var readTask = new JobItemRepository(readDb)
            .GetByJobAsync(_jobId, outcome: null, limit: 100, cancellation.Token);

        await pause.SecondLookupReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        pause.Resume.TrySetResult();

        await Should.ThrowAsync<OperationCanceledException>(readTask);
        pause.TitleLookupCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetByJobAsync_MoreThanPlatformLookupCeiling_BoundsPlatformIdsAndResolvesAllNames()
    {
        const int platformCount = MaxIdsPerLookup + 1;
        using (var db = CreateDb())
        {
            var now = DateTimeOffset.UtcNow;
            for (int id = 2; id <= platformCount; id++)
            {
                db.Platforms.Add(new PlatformEntity
                {
                    Id = id,
                    Name = $"Platform {id}",
                    ShortName = $"p{id}",
                    CreatedAt = now
                });
            }

            await db.SaveChangesAsync();
            var repository = new JobItemRepository(db);
            await repository.AddRangeAsync(Enumerable.Range(1, platformCount)
                .Select(id => JobItem.ForRom(
                    _jobId,
                    $"platform-{id}.bin",
                    1,
                    RomIngestOutcome.Ingested,
                    romFileId: null,
                    matchedTitleIds: [],
                    platformId: id,
                    error: null))
                .ToList());
        }

        _commands.Clear();
        using (var db = CreateDb())
        {
            var items = await new JobItemRepository(db)
                .GetByJobAsync(_jobId, outcome: null, limit: platformCount);

            items.Items.Count.ShouldBe(platformCount);
            items.Items.Select(item => item.PlatformName).ShouldBe(
                new[] { "snes" }.Concat(Enumerable.Range(2, platformCount - 1).Select(id => $"p{id}")),
                ignoreOrder: true);
        }

        var platformCommands = _commands.Commands
            .Where(command => command.CommandText.Contains("FROM romd.\"Platforms\"", StringComparison.Ordinal))
            .ToList();
        platformCommands.Count.ShouldBe(2);
        platformCommands.ShouldAllBe(command => command.ParameterCount <= MaxIdsPerLookup);
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_contextOptions);

    private async Task SeedAdditionalTitlesAsync(int titleCount)
    {
        using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        for (int id = 3; id <= titleCount; id++)
        {
            db.Titles.Add(new TitleEntity
            {
                Id = id,
                PlatformId = PlatformId,
                Name = $"Title {id}",
                NormalizedName = $"title{id}",
                EnrichmentStatus = EnrichmentStatus.None.ToString(),
                FieldProvenanceJson = "{}",
                FieldSourceOverridesJson = "{}",
                ScreenshotPrefsJson = "{}",
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync();
    }

    private static Guid Seed(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();

        db.Platforms.Add(new PlatformEntity
        {
            Id = PlatformId,
            Name = "Super Nintendo Entertainment System",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo Entertainment System", BaseCompactLabel = "Super Nintendo Entertainment System", CanonicalKey = "snes", ShortName = "snes",
            Manufacturer = "Nintendo",
            CreatedAt = now,
            CreatedByUserId = userId
        });

        foreach (int id in new[] { 1, 2 })
        {
            db.Titles.Add(new TitleEntity
            {
                Id = id,
                PlatformId = PlatformId,
                Name = $"Title {id}",
                NormalizedName = $"title{id}",
                EnrichmentStatus = EnrichmentStatus.None.ToString(),
                FieldProvenanceJson = "{}",
                FieldSourceOverridesJson = "{}",
                ScreenshotPrefsJson = "{}",
                CreatedAt = now,
                CreatedByUserId = userId
            });
        }

        var job = UploadJobEntity.FromDomain(UploadJob.Create("import.zip"));
        db.Jobs.Add(job);

        db.SaveChanges();
        db.ChangeTracker.Clear();
        return job.Id;
    }

    private sealed class PauseBeforeSecondTitleLookupInterceptor : DbCommandInterceptor
    {
        private int _titleLookupCount;
        public TaskCompletionSource SecondLookupReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TitleLookupCount => _titleLookupCount;

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
