using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;

namespace Romd.Persistence.Repositories.Reconciliation;

/// <summary>Selection is finite dispatch, never a media lease or a grant to publish under stale policy.</summary>
public sealed class MediaReconciliationSelections : IMediaReconciliationSelections
{
    private readonly RomdDbContext _db;
    private readonly DbContextOptions<RomdDbContext> _readOptions;
    private readonly IUnitOfWork _work;
    private readonly IMediaReconciliationRequests _requests;
    private readonly IMediaReconciliationSnapshotReader _snapshots;
    private readonly MediaKindCatalog _kinds;
    private readonly IMediaRunCustody _custody;
    private readonly TimeProvider _clock;
    public MediaReconciliationSelections(RomdDbContext db, DbContextOptions<RomdDbContext> readOptions, IUnitOfWork work,
        IMediaReconciliationRequests requests, IMediaReconciliationSnapshotReader snapshots, MediaKindCatalog kinds, IMediaRunCustody custody, TimeProvider clock)
    {
        this._db = db;
        this._readOptions = readOptions;
        this._work = work;
        this._requests = requests;
        this._snapshots = snapshots;
        this._kinds = kinds;
        this._custody = custody;
        this._clock = clock;
    }
    private sealed class Counts
    {
        public long Selected { get; set; }
        public long Created { get; set; }
        public long Adopted { get; set; }
        public long Skipped { get; set; }
        public long Completed { get; set; }
        public long Failed { get; set; }
    }
    private static async Task<Counts> CountsAsync(RomdDbContext context, Guid id, CancellationToken ct) =>
        await context.Database.SqlQuery<Counts>($"""
            SELECT count(*) AS "Selected",count(*) FILTER (WHERE item."ChildJobId" IS NOT NULL AND item."IsOwned") AS "Created",
                count(*) FILTER (WHERE item."ChildJobId" IS NOT NULL AND NOT item."IsOwned") AS "Adopted",
                count(*) FILTER (WHERE item."ChildJobId" IS NULL) AS "Skipped",
                count(*) FILTER (WHERE item."ChildJobId" IS NOT NULL AND child."CompletedAt" IS NOT NULL) AS "Completed",
                count(*) FILTER (WHERE item."ChildJobId" IS NOT NULL AND child."CompletedAt" IS NOT NULL AND child."Phase" IN ('Failed','Cancelled')) AS "Failed"
            FROM romd."MediaReconciliationSelectionItems" item
            LEFT JOIN romd."Jobs" child ON child."Id"=item."ChildJobId" WHERE item."SelectionJobId"={id}
            """).SingleAsync(ct);
    private static MediaReconciliationSelectionState State(MediaReconciliationDefinitionEntity definition, Counts counts)
    {
        var saved = definition.ToProgress() ?? throw new InvalidDataException("Selection progress requires a selection definition.");
        return new(saved with
        {
            Selected = counts.Selected,
            Created = counts.Created,
            Adopted = counts.Adopted,
            Skipped = counts.Skipped,
            Completed = counts.Completed,
            Failed = counts.Failed
        }, definition.CancelRequested);
    }
    public async Task<MediaReconciliationSelectionState?> ReadAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var read = new RomdDbContext(_readOptions);
        await using var transaction = await read.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await read.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var definition = await read.Set<MediaReconciliationDefinitionEntity>().SingleOrDefaultAsync(row => row.JobId == jobId && row.RequestMode == "Selection", ct);
        var state = definition is null ? null : await read.Set<MediaReconciliationOutcomeEntity>().AnyAsync(row => row.JobId == jobId, ct)
            ? new MediaReconciliationSelectionState(definition.ToProgress()!, definition.CancelRequested) : State(definition, await CountsAsync(read, jobId, ct));
        await transaction.CommitAsync(ct);
        return state;
    }
    public async Task<ErrorOr<MediaReconciliationSelectionState>> RefreshAsync(Guid jobId, CancellationToken ct = default)
    {
        _ = Fence(jobId);
        await using var transaction = await _work.BeginTransactionAsync(ct);
        var definition = await LockAsync(jobId, ct);
        if (definition is null)
            return MediaReconciliationErrors.Changed;
        var progress = definition.ToProgress()!;
        var counts = await SaveProgressAsync(jobId, progress.Cursor, progress.EnumerationComplete, ct);
        var state = State(definition, counts);
        await transaction.CommitAsync(ct);
        return state;
    }
    private Guid Fence(Guid id)
    {
        if (JobExecutionFenceScope.Current is not { } fence || fence.JobId != id)
            throw new JobExecutionOwnershipLostException();
        return fence.Token;
    }
    private async Task<MediaReconciliationDefinitionEntity?> LockAsync(Guid id, CancellationToken ct)
    {
        Guid token = Fence(id);
        if (_db.LockedJobExecution is not { } locked || locked.JobId != id || locked.Token != token ||
            _db.Database.CurrentTransaction?.TransactionId != locked.TransactionId)
            throw new JobExecutionOwnershipLostException();
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM romd.\"MediaReconciliationJobs\" WHERE \"JobId\"={id} FOR NO KEY UPDATE", ct);
        return await _db.Set<MediaReconciliationDefinitionEntity>().SingleOrDefaultAsync(row => row.JobId == id && row.RequestMode == "Selection", ct);
    }
    private static async Task<MediaReconciliationSubject[]> PageAsync(RomdDbContext read, MediaReconciliationRequest.SelectionWork selection,
        MediaReconciliationSubject? cursor, int maximum, CancellationToken ct)
    {
        List<MediaReconciliationSubject> page = [];
        foreach (var kind in Enum.GetValues<MediaReconciliationSubjectKind>())
        {
            if (cursor is not null && kind < cursor.Kind || selection.Filter is MediaReconciliationFilter.RetainedRepresentations && kind != MediaReconciliationSubjectKind.Representation)
                continue;
            int after = cursor?.Kind == kind ? cursor.Id : 0;
            IQueryable<int> candidates = kind switch
            {
                MediaReconciliationSubjectKind.LogicalRom => read.RomFiles.Where(row => row.Id > after && row.Id <= selection.Bounds.LogicalRom).Select(row => row.Id),
                MediaReconciliationSubjectKind.OriginalLayout => read.OriginalDiscLayouts.Where(row => row.Id > after && row.Id <= selection.Bounds.OriginalLayout).Select(row => row.Id),
                MediaReconciliationSubjectKind.Representation => MediaRepresentationOwnership.Owned(read).Where(row => row.Id > after && row.Id <= selection.Bounds.Representation).Select(row => row.Id),
                MediaReconciliationSubjectKind.CatalogCdLayout => read.Database.SqlQuery<int>($"""SELECT "CatalogFileId" AS "Value" FROM romd."CatalogCdLayoutBindings" WHERE "CatalogFileId">{after} AND "CatalogFileId"<={selection.Bounds.CatalogCdLayout}"""),
                _ => throw new InvalidDataException("Unsupported reconciliation subject namespace.")
            };
            page.AddRange((await candidates.Order().Take(maximum - page.Count).ToArrayAsync(ct)).Select(id => MediaReconciliationDefinitionEntity.Subject(kind.ToString(), id)));
            if (page.Count == maximum)
                break;
        }
        return page.ToArray();
    }
    private async Task<ErrorOr<string?>> SkipAsync(RomdDbContext read, MediaReconciliationSubject subject, MediaReconciliationFilter filter, CancellationToken ct)
    {
        if (filter is not MediaReconciliationFilter.KindSubjects selected)
            return (string?)null;
        // Retained artifacts have integrity scope without a current logical kind claim. Their explicit campaign uses RetainedRepresentations.
        if (subject is MediaReconciliationSubject.Representation)
            return "kind-subject-scope";
        var loaded = await _snapshots.ReadAsync(subject, ct);
        if (loaded.IsError)
            return loaded.Errors;
        if (loaded.Value is MediaReconciliationFacts.None absent)
            return absent.Code;
        string? kind = loaded.Value switch
        {
            MediaReconciliationFacts.Logical logical => MediaKindResolver.Resolve(_kinds, logical.Content, logical.PlatformKeys).Kind?.Kind,
            MediaReconciliationFacts.Layout layout => layout.KindId,
            _ => null
        };
        if (kind != selected.Kind)
            return "kind-not-selected";
        if (selected.PlatformId is not { } platform)
            return (string?)null;
        bool matches = loaded.Value is MediaReconciliationFacts.Layout disc ? disc.Sources.Layout!.PlatformId == platform :
            await (from rom in read.DatRoms
                   join game in read.DatGames on rom.DatGameId equals game.Id
                   join dat in read.DatFiles on game.DatFileId equals dat.Id
                   where rom.RomFileId == subject.Id && dat.PlatformId == platform
                   select rom.Id).AnyAsync(ct);
        return matches ? (string?)null : "platform-not-selected";
    }
    public async Task<ErrorOr<MediaReconciliationSelectionState>> AdvanceAsync(Guid jobId, int maximum, CancellationToken ct = default)
    {
        if (maximum is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        _ = Fence(jobId);
        // Classification and descriptor reads precede the mutation transaction. Children always replan current facts before media admission.
        await using var read = new RomdDbContext(_readOptions);
        var before = await read.Set<MediaReconciliationDefinitionEntity>().SingleOrDefaultAsync(row => row.JobId == jobId && row.RequestMode == "Selection", ct);
        if (before is null)
            return MediaReconciliationErrors.Changed;
        if (before.CancelRequested || before.EnumerationComplete == true)
            return State(before, await CountsAsync(read, jobId, ct));
        var selection = (MediaReconciliationRequest.SelectionWork)before.ToRequest();
        var previous = before.ToProgress()!;
        var page = await PageAsync(read, selection, previous.Cursor, maximum, ct);
        List<string?> skips = [];
        foreach (var subject in page)
        {
            var skip = await SkipAsync(read, subject, selection.Filter, ct);
            if (skip.IsError)
                return skip.Errors;
            skips.Add(skip.Value);
        }
        await using var transaction = await _work.BeginTransactionAsync(ct);
        var definition = await LockAsync(jobId, ct);
        if (definition is null || definition.CancelRequested || definition.EnumerationComplete == true ||
            !Equals(definition.ToProgress()!.Cursor, previous.Cursor))
            return MediaReconciliationErrors.Changed;
        var counts = await CountsAsync(_db, jobId, ct);
        long accepted = counts.Created + counts.Adopted, ordinal = counts.Selected;
        MediaReconciliationSubject? cursor = previous.Cursor;
        Guid? actor = await _db.Jobs.Where(row => row.Id == jobId).Select(row => row.CreatedByUserId).SingleAsync(ct);
        for (int index = 0; index < page.Length && accepted < selection.Limit; index++)
        {
            var subject = page[index];
            string? skip = skips[index];
            Guid? child = null;
            bool owned = false;
            if (skip is null)
            {
                var demand = new MediaReconciliationDemand(Guid.NewGuid(), subject,
                    selection.ConversionRequested && subject is not MediaReconciliationSubject.Representation, MediaReconciliationCause.Selection, actor, jobId);
                var result = await _requests.StageSubjectAsync(demand, ct);
                if (result.IsError)
                {
                    if (result.Errors.All(error => error.Type == ErrorType.NotFound))
                        skip = "subject-withdrawn";
                    else
                        return result.Errors;
                }
                else
                { child = result.Value.Job.Id; owned = result.Value.OwnedBySelection; accepted++; }
            }
            // Flush child definitions and audit before their selection item; the deferred guard verifies the whole graph at commit.
            await _work.FlushAsync(ct);
            ordinal++;
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."MediaReconciliationSelectionItems" ("SelectionJobId","SubjectKind","SubjectId","Ordinal","ChildJobId","IsOwned","SkippedCode")
                VALUES ({jobId},{subject.Kind.ToString()},{subject.Id},{ordinal},CAST({child} AS uuid),{owned},CAST({skip} AS text))
                """, ct);
            cursor = subject;
        }
        bool complete = accepted >= selection.Limit || page.Length < maximum;
        await SaveProgressAsync(jobId, cursor, complete, ct);
        var state = State((await _db.Set<MediaReconciliationDefinitionEntity>().SingleAsync(row => row.JobId == jobId, ct)), await CountsAsync(_db, jobId, ct));
        await transaction.CommitAsync(ct);
        return state;
    }
    private async Task<Counts> SaveProgressAsync(Guid id, MediaReconciliationSubject? cursor, bool complete, CancellationToken ct)
    {
        var counts = await CountsAsync(_db, id, ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE romd."MediaReconciliationJobs" SET "CursorKind"=CAST({cursor?.Kind.ToString()} AS text),"CursorId"=CAST({cursor?.Id} AS integer),
                "SelectedCount"={counts.Selected},"CreatedCount"={counts.Created},"AdoptedCount"={counts.Adopted},"SkippedCount"={counts.Skipped},
                "CompletedCount"={counts.Completed},"FailedCount"={counts.Failed},"EnumerationComplete"={complete} WHERE "JobId"={id};
            UPDATE romd."Jobs" SET "Phase"={(complete ? "Waiting" : "Selecting")} WHERE "Id"={id};
            """, ct);
        return counts;
    }
    public async Task<ErrorOr<MediaReconciliationSelectionState>> StopOwnedAsync(Guid jobId, CancellationToken ct = default)
    {
        _ = Fence(jobId);
        await using var transaction = await _work.BeginTransactionAsync(ct);
        var definition = await LockAsync(jobId, ct);
        if (definition is null)
            return MediaReconciliationErrors.Changed;
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE romd."MediaReconciliationJobs" SET "CancelRequested"=true WHERE "JobId"={jobId};
            UPDATE romd."MediaReconciliationJobs" child SET "CancelRequested"=true FROM romd."Jobs" root
                WHERE child."JobId"=root."Id" AND child."OriginSelectionJobId"={jobId} AND root."CompletedAt" IS NULL;
            """, ct);
        var state = State(definition, await CountsAsync(_db, jobId, ct)) with { CancelRequested = true };
        await transaction.CommitAsync(ct);
        return state;
    }
    public async Task<ErrorOr<MediaReconciliationJournalOutcome>> FinishAsync(Guid jobId, MediaReconciliationOutcomeKind kind, string code, CancellationToken ct = default)
    {
        // Independent replay wins before stale payload, fence, and custody validation.
        await using (var read = new RomdDbContext(_readOptions))
        {
            var outcome = await read.Set<MediaReconciliationOutcomeEntity>().SingleOrDefaultAsync(row => row.JobId == jobId, ct);
            if (outcome is not null)
                return new MediaReconciliationJournalOutcome(jobId, Enum.Parse<MediaReconciliationOutcomeKind>(outcome.Kind), outcome.Code, 0, new(outcome.CompletedAtUtcTicks, TimeSpan.Zero));
        }
        Guid token = Fence(jobId);
        if (!Enum.IsDefined(kind) || kind is MediaReconciliationOutcomeKind.CompletedWithErrors or MediaReconciliationOutcomeKind.Superseded ||
            string.IsNullOrWhiteSpace(code) || code.Length > 128 || code.Any(char.IsControl))
            throw new ArgumentException("Selection requires a bounded actual terminal cause.");
        if ((await _custody.EnsureJobStoppedAsync(jobId, ct)).IsError)
            return MediaReconciliationErrors.Custody;
        await using var transaction = await _work.BeginTransactionAsync(ct);
        var definition = await LockAsync(jobId, ct);
        if (definition is null)
            return MediaReconciliationErrors.Changed;
        if (await (from child in _db.Set<MediaReconciliationDefinitionEntity>()
                   join root in _db.Jobs on child.JobId equals root.Id
                   where child.OriginSelectionJobId == jobId && root.CompletedAt == null
                   select child.JobId).AnyAsync(ct))
            return MediaReconciliationErrors.Changed;
        var counts = await CountsAsync(_db, jobId, ct);
        if (kind == MediaReconciliationOutcomeKind.Completed)
        {
            if (definition.CancelRequested || definition.EnumerationComplete != true || counts.Completed + counts.Skipped != counts.Selected)
                return MediaReconciliationErrors.Changed;
            if (counts.Failed > 0)
                kind = MediaReconciliationOutcomeKind.CompletedWithErrors;
        }
        else if (!definition.CancelRequested)
            return MediaReconciliationErrors.Changed;
        await SaveProgressAsync(jobId, definition.ToProgress()!.Cursor, definition.EnumerationComplete!.Value, ct);
        var completed = _clock.GetUtcNow();
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."MediaReconciliationOutcomes" ("JobId","Kind","Code","AcknowledgedVersion","ExecutionFenceToken","CompletedAtUtcTicks")
                VALUES ({jobId},{kind.ToString()},{code},0,{token},{completed.UtcTicks});
            UPDATE romd."Jobs" SET "Phase"={kind.ToString()},"CompletedAt"={completed.ToUnixTimeMilliseconds()},"CurrentItem"=NULL,
                "NextAttemptAtUtc"=NULL,"ExecutionFenceToken"=NULL,"ExecutionLeaseExpiresAtUtc"=NULL WHERE "Id"={jobId};
            """, ct);
        await transaction.CommitAsync(ct);
        return new MediaReconciliationJournalOutcome(jobId, kind, code, 0, completed);
    }
}
