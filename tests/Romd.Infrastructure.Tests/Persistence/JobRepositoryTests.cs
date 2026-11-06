using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class JobRepositoryTests
{
    [Fact]
    public async Task ArchiveCompletedAsync_DeferredMaterializationJob_ArchivesItAndSkipsActiveJob()
    {
        await using var database = await CreateDatabaseAsync();
        var archivedAt = new DateTimeOffset(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);
        var repository = new JobRepository(database.Context, new ManualTimeProvider(archivedAt));
        var deferred = MaterializationJob.Create(7, "Arcade", createdAt: DateTimeOffset.UtcNow);
        deferred.Start("hangfire-deferred", startedAt: DateTimeOffset.UtcNow);
        deferred.MarkDeferred();
        deferred.Complete(completedAt: DateTimeOffset.UtcNow);
        var pending = MaterializationJob.Create(7, "Arcade", createdAt: DateTimeOffset.UtcNow);
        database.Context.Set<MaterializationJobEntity>().AddRange(
            MaterializationJobEntity.FromDomain(deferred),
            MaterializationJobEntity.FromDomain(pending));
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        int archivedCount = await repository.ArchiveCompletedAsync();

        archivedCount.ShouldBe(1);
        var deferredEntity = await database.Context.Jobs
            .AsNoTracking()
            .SingleAsync(job => job.Id == deferred.Id);
        deferredEntity.IsArchived.ShouldBeTrue();
        deferredEntity.ArchivedAt.ShouldBe(archivedAt);
        var pendingEntity = await database.Context.Jobs
            .AsNoTracking()
            .SingleAsync(job => job.Id == pending.Id);
        pendingEntity.IsArchived.ShouldBeFalse();
        pendingEntity.ArchivedAt.ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_TiedTimestamps_PagesWithoutDuplicatesAndHonorsFilters()
    {
        await using var database = await CreateDatabaseAsync();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        database.Context.Users.AddRange(new Romd.Persistence.Identity.RomdUser { Id = owner, UserName = "owner" }, new Romd.Persistence.Identity.RomdUser { Id = other, UserName = "other" });
        var created = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        var entities = Enumerable.Range(0, 6).Select(index =>
        {
            var job = UploadJob.Create($"pack-{index}.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: index == 5 ? other : owner);
            if (index != 0)
                job.Fail("test failure", failedAt: DateTimeOffset.UtcNow);
            if (index == 4)
                job.Archive(archivedAt: DateTimeOffset.UtcNow);
            var entity = UploadJobEntity.FromDomain(job);
            entity.CreatedAt = created;
            return entity;
        }).ToArray();
        database.Context.AddRange(entities);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        var repository = new JobRepository(database.Context, TimeProvider.System);
        var filter = new JobListFilter(owner, "pack", "failed", "upload", "active", created, created.AddDays(1), null, null, 2);
        var first = await repository.ListAsync(filter);
        first.Count.ShouldBe(3); // two rows plus one lookahead
        var second = await repository.ListAsync(filter with { CursorCreatedAt = first[1].CreatedAt, CursorId = first[1].Id });
        second.Count.ShouldBe(1);
        first.Take(2).Select(job => job.Id).Intersect(second.Select(job => job.Id)).ShouldBeEmpty();
        first.All(job => job.CreatedByUserId == owner && job.Phase == "Failed" && !job.IsArchived).ShouldBeTrue();
        var archived = await repository.ListAsync(filter with { Archive = "archived" });
        archived.Single().Id.ShouldBe(entities[4].Id);
        var queued = await repository.ListAsync(filter with { Outcome = "queued" });
        queued.Single().Id.ShouldBe(entities[0].Id);
    }

    [Theory]
    [InlineData(false, false, "deferred")]
    [InlineData(false, false, "queued")]
    [InlineData(false, true, "deferred")]
    [InlineData(false, true, "running")]
    [InlineData(true, false, "deferred")]
    [InlineData(true, false, "queued")]
    [InlineData(true, true, "deferred")]
    [InlineData(true, true, "running")]
    public async Task ListAsync_ScheduledMedia_MatchesDisplayedOutcomeWithoutEnteringActiveBucket(
        bool bulk, bool started, string outcome)
    {
        await using var database = await CreateDatabaseAsync();
        var owner = Guid.NewGuid();
        database.Context.Users.Add(new RomdUser { Id = owner, UserName = "history-owner" });
        await database.Context.SaveChangesAsync(CancellationToken.None);
        var deferred = await SeedScheduledMediaAsync(database.Context, bulk, started, true, owner, 1, CancellationToken.None);
        var immediate = await SeedScheduledMediaAsync(database.Context, bulk, started, false, owner, 2, CancellationToken.None);
        var repository = new JobRepository(database.Context, TimeProvider.System);
        var filter = new JobListFilter(owner, null, outcome, MediaReconciliationJobEntity.Type,
            "active", null, null, null, null, 100);

        var jobs = await repository.ListAsync(filter, CancellationToken.None);

        jobs.Select(job => job.Id).ShouldBe([outcome == "deferred" ? deferred.Id : immediate.Id],
            "scheduled media displays deferred regardless of its active phase; queued/running contains only unscheduled work");
    }

    [Fact]
    public async Task ListAsync_DeferredMedia_PreservesOwnerAndFamilyVisibilityBeforePagination()
    {
        await using var database = await CreateDatabaseAsync();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        database.Context.Users.AddRange(new RomdUser { Id = owner, UserName = "owner" },
            new RomdUser { Id = other, UserName = "other" });
        var ordinary = MaterializationJob.Create(7, "Owned library", createdAt: DateTimeOffset.UtcNow, owner);
        ordinary.Start("ordinary-deferred", startedAt: DateTimeOffset.UtcNow);
        ordinary.MarkDeferred();
        ordinary.Complete(completedAt: DateTimeOffset.UtcNow);
        database.Context.Add(MaterializationJobEntity.FromDomain(ordinary));
        await database.Context.SaveChangesAsync(CancellationToken.None);
        var ownDisc = await SeedScheduledMediaAsync(database.Context, false, false, true, owner, 3, CancellationToken.None);
        var ownBulk = await SeedScheduledMediaAsync(database.Context, true, true, true, owner, 4, CancellationToken.None);
        await SeedScheduledMediaAsync(database.Context, false, true, true, other, 5, CancellationToken.None);
        await SeedScheduledMediaAsync(database.Context, true, false, true, other, 6, CancellationToken.None);
        var repository = new JobRepository(database.Context, TimeProvider.System);
        var filter = new JobListFilter(owner, null, "deferred", null, "active", null, null, null, null, 1,
            IncludeMediaConvergence: false);

        var nonAdmin = await repository.ListAsync(filter, CancellationToken.None);
        var admin = await repository.ListAsync(filter with { Limit = 100, IncludeMediaConvergence = true }, CancellationToken.None);

        nonAdmin.Select(job => job.Id).ShouldBe([ordinary.Id], "hidden media must not consume the visible page or its lookahead");
        admin.Select(job => job.Id).Order().ShouldBe(new[] { ordinary.Id, ownDisc.Id, ownBulk.Id }.Order(),
            "enabling media visibility must still respect the requested owner fence");
    }

    [Fact]
    public async Task ListAsync_NonMediaScheduledJobs_PreserveExistingOutcomeSemantics()
    {
        await using var database = await CreateDatabaseAsync();
        var pending = UploadJob.Create("pending.zip", createdAt: DateTimeOffset.UtcNow);
        pending.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var running = UploadJob.Create("running.zip", createdAt: DateTimeOffset.UtcNow);
        running.Start("ordinary-running", startedAt: DateTimeOffset.UtcNow);
        running.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var deferred = MaterializationJob.Create(7, "Deferred library", createdAt: DateTimeOffset.UtcNow);
        deferred.Start("ordinary-deferred", startedAt: DateTimeOffset.UtcNow);
        deferred.MarkDeferred();
        deferred.Complete(completedAt: DateTimeOffset.UtcNow);
        deferred.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(1);
        database.Context.AddRange(UploadJobEntity.FromDomain(pending), UploadJobEntity.FromDomain(running),
            MaterializationJobEntity.FromDomain(deferred));
        await database.Context.SaveChangesAsync(CancellationToken.None);
        database.Context.ChangeTracker.Clear();
        var repository = new JobRepository(database.Context, TimeProvider.System);
        var filter = new JobListFilter(null, null, null, null, "active", null, null, null, null, 100);

        var queued = await repository.ListAsync(filter with { Outcome = "queued" }, CancellationToken.None);
        var active = await repository.ListAsync(filter with { Outcome = "running" }, CancellationToken.None);
        var terminalDeferred = await repository.ListAsync(filter with { Outcome = "deferred" }, CancellationToken.None);

        queued.Select(job => job.Id).ShouldBe([pending.Id]);
        active.Select(job => job.Id).ShouldBe([running.Id]);
        terminalDeferred.Select(job => job.Id).ShouldBe([deferred.Id], "existing materialization Deferred remains a terminal outcome");
    }

    [Fact]
    public async Task ListAsync_CorrelationPagesBeyondOneThousand_WithCompleteStableIds()
    {
        await using var database = await CreateDatabaseAsync();
        var correlation = Guid.NewGuid();
        var created = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var entities = Enumerable.Range(0, 1005).Select(index =>
        {
            var row = UploadJobEntity.FromDomain(UploadJob.Create($"correlation-{index}.zip", createdAt: DateTimeOffset.UtcNow));
            row.CorrelationId = correlation;
            row.CreatedAt = created;
            return row;
        }).ToArray();
        database.Context.AddRange(entities.Append(UploadJobEntity.FromDomain(UploadJob.Create("other-batch.zip", createdAt: DateTimeOffset.UtcNow))));
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        var repository = new JobRepository(database.Context, TimeProvider.System);
        var filter = new JobListFilter(null, null, null, "upload", "all", null, null, null, null, 100, false, correlation);
        var ids = new List<Guid>();
        var pages = 0;
        while (true)
        {
            var rows = await repository.ListAsync(filter);
            var page = rows.Take(filter.Limit).ToArray();
            ids.AddRange(page.Select(row => row.Id));
            pages++;
            if (rows.Count <= filter.Limit)
                break;
            filter = filter with { CursorCreatedAt = page[^1].CreatedAt, CursorId = page[^1].Id };
        }
        pages.ShouldBe(11);
        ids.Count.ShouldBe(1005);
        ids.Distinct().Count().ShouldBe(1005);
        ids.Order().ShouldBe(entities.Select(row => row.Id).Order());
    }

    [Fact]
    public async Task SetArchivedAsync_OnlyChangesArchiveColumns_AfterStaleRead()
    {
        await using var database = await CreateDatabaseAsync();
        var job = UploadJob.Create("terminal.zip", createdAt: DateTimeOffset.UtcNow);
        job.Fail("done", failedAt: DateTimeOffset.UtcNow);
        database.Context.Add(UploadJobEntity.FromDomain(job));
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        var repository = new JobRepository(database.Context, TimeProvider.System);
        (await repository.GetByIdAsync(job.Id))!.IsArchived.ShouldBeFalse();
        var fence = Guid.NewGuid();
        var lease = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        await database.Context.Jobs.Where(row => row.Id == job.Id).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.ExecutionFenceToken, fence).SetProperty(row => row.ExecutionLeaseExpiresAtUtc, lease)
            .SetProperty(row => row.CurrentItem, "new progress").SetProperty(row => row.HangfireJobId, "new delivery"));
        var scope = new JobVisibilityScope(null, true, false);
        var archived = await repository.SetArchivedAsync(job.Id, true, scope);
        archived!.IsArchived.ShouldBeTrue();
        var saved = await database.Context.Jobs.SingleAsync(row => row.Id == job.Id);
        saved.Phase.ShouldBe("Failed");
        saved.ExecutionFenceToken.ShouldBe(fence);
        saved.ExecutionLeaseExpiresAtUtc.ShouldBe(lease);
        saved.CurrentItem.ShouldBe("new progress");
        saved.HangfireJobId.ShouldBe("new delivery");
        var archivedAt = saved.ArchivedAt;
        await repository.SetArchivedAsync(job.Id, true, scope);
        (await database.Context.Jobs.SingleAsync(row => row.Id == job.Id)).ArchivedAt.ShouldBe(archivedAt);
        (await repository.SetArchivedAsync(job.Id, false, scope))!.IsArchived.ShouldBeFalse();
    }

    private static async Task<Job> SeedScheduledMediaAsync(RomdDbContext context, bool bulk, bool started,
        bool scheduled, Guid actorId, byte seed, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var requests = new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(context, TimeProvider.System);
        Romd.Admin.Application.Source.Rom.Reconciliation.MediaReconciliationAcceptance accepted;
        if (bulk)
        {
            var selection = await requests.StageSelectionAsync(Guid.NewGuid(), new MediaReconciliationFilter.AllSubjects(), 100, false,
                Romd.Admin.Application.Source.Rom.Reconciliation.MediaReconciliationCause.Admin, actorId, ct);
            selection.IsError.ShouldBeFalse();
            accepted = selection.Value;
        }
        else
        {
            var artifact = new FileEntityPersistence
            {
                Sha256 = Sha256.Parse(seed.ToString("x64")),
                Size = 128,
                SizeOnDisk = 128,
                CreatedAt = DateTimeOffset.UtcNow
            };
            context.Files.Add(artifact);
            await context.SaveChangesAsync(ct);
            var identity = MediaFactFixtures.Identity(Enumerable.Repeat(seed, 4096).ToArray());
            var published = await context.CreatePreferredUnmeasuredWholeAsync("disc.rvz", artifact.Id, identity,
                MediaFactFixtures.Rvz(identity.Size, 1, 5, 12, 131072, false), Guid.Empty, DateTimeOffset.UnixEpoch, ct);
            var subject = await requests.StageSubjectAsync(new(Guid.NewGuid(), new MediaReconciliationSubject.LogicalRom(published.RomId), false,
                Romd.Admin.Application.Source.Rom.Reconciliation.MediaReconciliationCause.Admin, actorId, null), ct);
            subject.IsError.ShouldBeFalse();
            accepted = subject.Value;
        }
        var job = accepted.Job;
        if (started)
        {
            job.Start("history-fixture", startedAt: DateTimeOffset.UtcNow);
            job.SetPhase(bulk ? MediaReconciliationPhase.Waiting : MediaReconciliationPhase.Encoding);
        }
        if (scheduled)
        {
            job.RecordDeferral("Shared media capacity occupied.", at: DateTimeOffset.UtcNow);
            job.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(1);
        }
        var entity = context.Set<MediaReconciliationJobEntity>().Local.Single(row => row.Id == job.Id);
        context.Entry(entity).CurrentValues.SetValues(MediaReconciliationJobEntity.FromDomain(job, DateTimeOffset.UtcNow));
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        context.ChangeTracker.Clear();
        return job;
    }

    private static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var connection = PostgreSqlTestDatabase.Create();
        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(connection.ConnectionString)
            .Options;
        var context = new RomdDbContext(options);
        return new TestDatabase(connection, context);
    }

    private sealed class TestDatabase(PostgreSqlTestDatabase connection, RomdDbContext context) : IAsyncDisposable
    {
        public RomdDbContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
