using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class JobBackstopOwnershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private const string TimeoutReason = "Job timed out (no progress updates received)";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportCleanup_CallerCommitOrRollback_ChangesOnlyMatchingPathAndAudit(bool commit)
    {
        using var database = PostgreSqlTestDatabase.Create();
        var job = CompletedExport(Now.AddDays(-2));
        await using (var seed = database.CreateContext())
        {
            seed.Add(ExportJobEntity.FromDomain(job, Now.AddHours(-1)));
            await seed.SaveChangesAsync();
        }
        await using var context = database.CreateContext();
        await using var observer = database.CreateContext();
        var store = new ExportJobRepository(context, new ManualTimeProvider(Now));
        var cutoff = Now.AddDays(-1);
        var candidate = (await store.GetExpiredArtifactsAsync(cutoff)).ShouldHaveSingleItem();
        candidate.JobId.ShouldBe(job.Id);
        candidate.Path.ShouldBe("old.zip");
        var before = await observer.Set<ExportJobEntity>().SingleAsync();
        await Should.ThrowAsync<InvalidOperationException>(() => store.ClearExpiredArtifactStagedAsync(
            job.Id, candidate.Path, cutoff, Now));

        await using (var transaction = await new EfUnitOfWork(context).BeginTransactionAsync())
        {
            (await store.ClearExpiredArtifactStagedAsync(job.Id, candidate.Path, cutoff, Now)).ShouldBeTrue();
            (await context.Set<ExportJobEntity>().Select(row => row.ExportPath).SingleAsync()).ShouldBeNull();
            (await observer.Set<ExportJobEntity>().SingleAsync()).ExportPath.ShouldBe("old.zip");
            if (commit)
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
        }

        var after = await observer.Set<ExportJobEntity>().SingleAsync();
        after.ExportPath.ShouldBe(commit ? null : "old.zip");
        after.UpdatedAt.ShouldBe(commit ? Now : before.UpdatedAt);
        after.Phase.ShouldBe(before.Phase);
        after.CompletedAt.ShouldBe(before.CompletedAt);
        after.IsArchived.ShouldBe(before.IsArchived);
        after.ArchivedAt.ShouldBe(before.ArchivedAt);
        after.ErrorsJson.ShouldBe(before.ErrorsJson);
        after.TotalTitles.ShouldBe(before.TotalTitles);
        after.ProcessedTitles.ShouldBe(before.ProcessedTitles);
        after.TotalFiles.ShouldBe(before.TotalFiles);
        after.ProcessedFiles.ShouldBe(before.ProcessedFiles);
        after.SkippedFiles.ShouldBe(before.SkippedFiles);
        after.ExportScopeKind.ShouldBe(before.ExportScopeKind);
        after.ExportFormat.ShouldBe(before.ExportFormat);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportCleanup_ConcurrentMetadataChanges_PreservesArchiveEditAndRejectsOnlyChangedPath(bool pathChanges)
    {
        using var database = PostgreSqlTestDatabase.Create();
        var job = CompletedExport(Now.AddDays(-2));
        await using (var seed = database.CreateContext())
        {
            seed.Add(ExportJobEntity.FromDomain(job, Now.AddHours(-1)));
            await seed.SaveChangesAsync();
        }
        await using var context = database.CreateContext();
        var store = new ExportJobRepository(context, new ManualTimeProvider(Now));
        var candidate = (await store.GetExpiredArtifactsAsync(Now.AddDays(-1))).ShouldHaveSingleItem();
        await using (var writer = database.CreateContext())
        {
            var current = (await new ExportJobRepository(writer, new ManualTimeProvider(Now)).GetByIdAsync(job.Id))!;
            if (pathChanges)
                current.SetExportPath("new.zip");
            current.Unarchive();
            await new ExportJobRepository(writer, new ManualTimeProvider(Now)).CommitCheckpointAsync(writer, current);
        }

        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync();
        (await store.ClearExpiredArtifactStagedAsync(candidate.JobId, candidate.Path, Now.AddDays(-1), Now)).ShouldBe(!pathChanges);
        await transaction.CommitAsync();

        await using var observer = database.CreateContext();
        var after = await observer.Set<ExportJobEntity>().SingleAsync();
        after.ExportPath.ShouldBe(pathChanges ? "new.zip" : null);
        after.IsArchived.ShouldBeFalse();
        after.ArchivedAt.ShouldBeNull();
        after.ProcessedFiles.ShouldBe(2);
        after.ErrorsJson.ShouldContain("retained error");
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task ExportCleanup_ExpiryBoundary_IsRecheckedByConditionalWrite(int secondsFromCutoff, bool expired)
    {
        using var database = PostgreSqlTestDatabase.Create();
        var cutoff = Now.AddDays(-1);
        var job = CompletedExport(cutoff.AddSeconds(secondsFromCutoff));
        await using var context = database.CreateContext();
        context.Add(ExportJobEntity.FromDomain(job, Now.AddHours(-1)));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var store = new ExportJobRepository(context, new ManualTimeProvider(Now));
        (await store.GetExpiredArtifactsAsync(cutoff)).Count.ShouldBe(expired ? 1 : 0);
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync();
        (await store.ClearExpiredArtifactStagedAsync(job.Id, "old.zip", cutoff, Now)).ShouldBe(expired);
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task StaleFailure_HeartbeatLockWait_CapturesFailureAfterWaitAndPreservesCurrentHistory()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var token = Guid.NewGuid();
        var id = await SeedStaleReplaceAsync(database, token);
        var clock = new ManualTimeProvider(Now);
        await using var blocker = database.CreateContext();
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        var claims = new ClaimedJobRepository<ReplaceDatJob, ReplaceDatJobRepository>(
            new ReplaceDatJobRepository(blocker, clock), blocker, clock, new());
        (await claims.RenewExecutionLeaseAsync(id, token)).ShouldBeTrue();
        var oldCheckpoint = (await new ReplaceDatJobRepository(blocker, clock).GetByIdAsync(id))!;
        oldCheckpoint.SetCurrentItem("late checkpoint");
        await using var context = database.CreateContext();
        var repository = new ReplaceDatJobRepository(context, clock);
        var cutoff = Now.AddMinutes(-30);
        (await repository.GetStaleStartedIdsAsync(cutoff)).ShouldBe([id]);
        await Should.ThrowAsync<InvalidOperationException>(() => repository.TryFailStaleStartedStagedAsync(id, cutoff, TimeoutReason));
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<bool>? pending = null;
        Exception? failure = null;
        var afterWait = Now.AddSeconds(2);
        try
        {
            bool waited;
            (pending, waited) = await database.StartWaitingForALockAsync(context,
                ct => repository.TryFailStaleStartedStagedAsync(id, cutoff, TimeoutReason, ct), timeout.Token);
            waited.ShouldBeTrue();
            clock.SetUtcNow(afterWait);
            await blockerTransaction.CommitAsync(timeout.Token);
            (await pending).ShouldBeTrue();
            await transaction.CommitAsync(timeout.Token);
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, failure, pending ?? Task.CompletedTask);
        }

        await using var observer = database.CreateContext();
        var row = await observer.Set<ReplaceDatJobEntity>().SingleAsync();
        row.Phase.ShouldBe("Failed");
        row.CompletedAt.ShouldBe(afterWait);
        row.UpdatedAt.ShouldBe(afterWait);
        row.ExecutionFenceToken.ShouldBeNull();
        row.ExecutionLeaseExpiresAtUtc.ShouldBeNull();
        row.CurrentItem.ShouldBe("resume cursor");
        row.NewDatId.ShouldBe(42);
        row.HangfireJobId.ShouldBe("delivery");
        row.DeferralCount.ShouldBe(2);
        row.LastDeferralError.ShouldBe("old pressure");
        var errors = row.GetErrors();
        errors.Select(error => error.Message).ShouldBe(["retained error", TimeoutReason]);
        errors[^1].OccurredAt.ShouldBe(afterWait);
        (await claims.TryUpdateClaimedAsync(oldCheckpoint, token)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("checkpoint")]
    [InlineData("claim")]
    [InlineData("cancel")]
    [InlineData("defer")]
    public async Task StaleFailure_ConcurrentProgressWins_RechecksLockedCurrentStateInsteadOfOldHint(string winner)
    {
        using var database = PostgreSqlTestDatabase.Create();
        var token = Guid.NewGuid();
        var id = await SeedStaleReplaceAsync(database, token, expiredLease: winner == "claim",
            phase: winner == "checkpoint" ? ReplaceDatPhase.Ingesting : ReplaceDatPhase.Replacing);
        var clock = new ManualTimeProvider(Now);
        var cutoff = Now.AddMinutes(-30);
        await using var context = database.CreateContext();
        var repository = new ReplaceDatJobRepository(context, clock);
        (await repository.GetStaleStartedIdsAsync(cutoff)).ShouldBe([id]);
        var gate = new PauseAfterCheckpointFlush();
        await using var writer = new RomdDbContext(database.CreateOptions(builder => builder.AddInterceptors(gate)));
        var writes = new ReplaceDatJobRepository(writer, clock);
        var claims = new ClaimedJobRepository<ReplaceDatJob, ReplaceDatJobRepository>(writes, writer, clock, new());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync(timeout.Token);
        Task<bool>? writing = null;
        Task<bool>? failing = null;
        Exception? failure = null;
        try
        {
            writing = WriteWinnerAsync();
            await Task.WhenAny(gate.Arrived, writing).WaitAsync(timeout.Token);
            if (writing.IsCompleted)
                await writing;
            gate.Arrived.IsCompleted.ShouldBeTrue("The writer must reach its checkpoint flush before lock observation.");
            bool waited;
            (failing, waited) = await database.StartWaitingForALockAsync(context,
                ct => repository.TryFailStaleStartedStagedAsync(id, cutoff, TimeoutReason, ct), timeout.Token);
            waited.ShouldBeTrue();
            gate.Release();
            (await writing).ShouldBeTrue();
            (await failing).ShouldBeFalse();
            await transaction.CommitAsync(timeout.Token);
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            gate.Release();
            await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, failure,
                writing ?? Task.CompletedTask, failing ?? Task.CompletedTask);
        }

        await using var observer = database.CreateContext();
        var row = await observer.Set<ReplaceDatJobEntity>().SingleAsync();
        row.Phase.ShouldBe(winner == "cancel" ? "Cancelled" : "Replacing");
        row.GetErrors().ShouldNotContain(error => error.Message == TimeoutReason);
        row.NewDatId.ShouldBe(winner == "checkpoint" ? 99 : 42);
        row.CurrentItem.ShouldBe(winner == "checkpoint" ? "winner cursor" : "resume cursor");
        row.IsArchived.ShouldBeFalse();
        row.UpdatedAt.ShouldBe(Now);
        if (winner is "cancel" or "defer")
            row.ExecutionFenceToken.ShouldBeNull();
        else
            row.ExecutionFenceToken.ShouldNotBeNull();
        if (winner == "defer")
            row.NextAttemptAtUtc.ShouldBe(Now.AddMinutes(1));
        if (winner == "checkpoint")
            row.GetErrors().Select(error => error.Message).ShouldBe(["retained error", "winner attempt"]);
        if (winner == "claim")
        {
            row.HangfireJobId.ShouldBe("new delivery");
            row.ExecutionFenceToken.ShouldNotBe(token);
        }

        async Task<bool> WriteWinnerAsync()
        {
            if (winner == "claim")
                return await claims.TryClaimExecutionAsync(id, "new delivery", timeout.Token) is not null;
            var job = (await writes.GetByIdAsync(id, timeout.Token))!;
            if (winner == "cancel")
            {
                job.Cancel(clock.GetUtcNow());
                var unitOfWork = new EfUnitOfWork(writer);
                await using var owned = await unitOfWork.BeginTransactionAsync(timeout.Token);
                await writes.UpdateStagedAsync(job, timeout.Token);
                await unitOfWork.FlushAsync(timeout.Token);
                await owned.CommitAsync(timeout.Token);
                return true;
            }
            if (winner == "defer")
            {
                job.RecordDeferral("capacity", clock.GetUtcNow());
                return await claims.TryDeferClaimedAsync(job, token, Now.AddMinutes(1), timeout.Token);
            }
            job.SetNewDatId(99);
            job.BeginReplacing();
            job.SetCurrentItem("winner cursor");
            job.RecordError("attempt", "winner attempt", clock.GetUtcNow());
            return await claims.TryUpdateClaimedAsync(job, token, timeout.Token);
        }
    }

    private static async Task<Guid> SeedStaleReplaceAsync(PostgreSqlTestDatabase database, Guid token, bool expiredLease = false,
        ReplaceDatPhase phase = ReplaceDatPhase.Replacing)
    {
        var id = Guid.NewGuid();
        await using var context = database.CreateContext();
        context.Add(new ReplaceDatJobEntity
        {
            Id = id,
            CorrelationId = Guid.NewGuid(),
            ExistingDatId = 7,
            NewDatId = phase == ReplaceDatPhase.Ingesting ? null : 42,
            SourceFilename = "replacement.dat",
            JobType = "replace_dat",
            Phase = phase.ToString(),
            CreatedAt = Now.AddHours(-2),
            StartedAt = Now.AddHours(-2),
            UpdatedAt = Now.AddMinutes(-31),
            CurrentItem = "resume cursor",
            HangfireJobId = "delivery",
            ExecutionFenceToken = token,
            ExecutionLeaseExpiresAtUtc = expiredLease ? Now.AddMinutes(-1) : Now.AddMinutes(1),
            DeferralCount = 2,
            FirstDeferralAt = Now.AddHours(-1),
            LastDeferralAt = Now.AddMinutes(-31),
            LastDeferralError = "old pressure",
            ErrorsJson = JsonSerializer.Serialize(new[] { new JobError("attempt", "retained error", Now.AddHours(-1)) })
        });
        context.JobDispatches.Add(new JobDispatchEntity
        {
            JobId = id,
            JobType = "replace_dat",
            CreatedAtUtc = Now.AddHours(-2),
            AvailableAtUtc = Now.AddHours(-2),
            DeliveredAtUtc = Now.AddHours(-2),
            HangfireJobId = "delivery"
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static ExportJob CompletedExport(DateTimeOffset completedAt)
    {
        var job = ExportJob.CreateAllCatalog(Now.AddDays(-3), null);
        job.Start("delivery", Now.AddDays(-3));
        job.SetTotals(1, 2);
        job.BeginPackaging();
        job.RecordTitleProcessed();
        job.RecordFileProcessed();
        job.RecordFileSkipped();
        job.BeginStoring();
        job.SetExportPath("old.zip");
        job.RecordError("attempt", "retained error", Now.AddDays(-2));
        job.Complete(completedAt);
        job.Archive(Now.AddHours(-1));
        return job;
    }

    private sealed class PauseAfterCheckpointFlush : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _paused;
        public Task Arrived => _arrived.Task;
        public void Release() => _release.TrySetResult();

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (!_paused)
            {
                _paused = true;
                _arrived.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
