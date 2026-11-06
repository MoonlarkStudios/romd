using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;

namespace Romd.Persistence.Repositories.Reconciliation;

/// <summary>Accepts intent without native services, byte inspection, policy projection or premature caller flushes.</summary>
public sealed partial class MediaReconciliationRequests : IMediaReconciliationRequests
{
    private readonly RomdDbContext _db;
    private readonly TimeProvider _clock;
    public MediaReconciliationRequests(RomdDbContext db, TimeProvider clock)
    {
        this._db = db;
        this._clock = clock;
    }
    private static readonly Error _conflict = Error.Conflict("MediaReconciliation.RequestConflict", "The request identity belongs to different work.");
    private IQueryable<MediaReconciliationJobEntity> Jobs => _db.Set<MediaReconciliationJobEntity>()
        .AsNoTracking().Include(row => row.Definition).ThenInclude(row => row.Outcome);
    private async Task<MediaReconciliationJob> JobAsync(Guid id, CancellationToken ct)
    {
        var staged = _db.Set<MediaReconciliationJobEntity>().Local.SingleOrDefault(row => row.Id == id && _db.Entry(row).State == EntityState.Added);
        return (staged ?? await Jobs.SingleAsync(row => row.Id == id, ct)).ToDomain();
    }
    private void RequireTransaction()
    {
        if (_db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Request acceptance requires the caller's ReadCommitted transaction.");
    }
    private Task LockRequestAsync(Guid id, CancellationToken ct) => _db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT pg_advisory_xact_lock(hashtextextended({'r' + id.ToString("N")},0))", ct);
    private async Task<MediaReconciliationRequestEntity?> PriorAsync(Guid id, CancellationToken ct) =>
        _db.Set<MediaReconciliationRequestEntity>().Local.SingleOrDefault(row => row.Id == id) ??
        await _db.Set<MediaReconciliationRequestEntity>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
    private static bool SameAttribution(MediaReconciliationRequestEntity prior, bool conversion, MediaReconciliationCause cause, Guid? actor, Guid? selection, Guid? retryOf) =>
        prior.ConversionRequested == conversion && prior.Cause == cause.ToString() && prior.ActorId == actor && prior.OriginSelectionJobId == selection && prior.RetryOfJobId == retryOf;
    public Task<ErrorOr<MediaReconciliationAcceptance>> StageSubjectAsync(MediaReconciliationDemand demand, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(demand);
        if (demand.Cause == MediaReconciliationCause.Retry)
            throw new ArgumentException("Retries require the original terminal job identity.", nameof(demand));
        return StageSubjectCoreAsync(demand, null, ct);
    }
    private async Task<ErrorOr<MediaReconciliationAcceptance>> StageSubjectCoreAsync(MediaReconciliationDemand demand, Guid? retryOf, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(demand);
        ct.ThrowIfCancellationRequested();
        RequireTransaction();
        await LockRequestAsync(demand.RequestId, ct);
        if (await PriorAsync(demand.RequestId, ct) is { } prior)
        {
            var existing = await JobAsync(prior.JobId, ct);
            return SameAttribution(prior, demand.ConversionRequested, demand.Cause, demand.ActorId, demand.SelectionJobId, retryOf) &&
                existing.Request is MediaReconciliationRequest.SubjectWork work && work.Subject.Equals(demand.Subject)
                ? new MediaReconciliationAcceptance(existing, false, demand.SelectionJobId is not null && existing.OriginSelectionJobId == demand.SelectionJobId) : _conflict;
        }
        string kind = demand.Subject.Kind.ToString();
        int id = demand.Subject.Id;
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({9416 + (int)demand.Subject.Kind},{id})", ct);
        var tracked = _db.Set<MediaReconciliationSubjectEntity>().Local.SingleOrDefault(row => row.SubjectKind == kind && row.SubjectId == id);
        var state = tracked ?? await _db.Set<MediaReconciliationSubjectEntity>().AsTracking().SingleOrDefaultAsync(row => row.SubjectKind == kind && row.SubjectId == id, ct);
        // A retained context may contain an earlier transaction's acknowledged demand. Never reload current staged mutations.
        if (tracked is not null && _db.Entry(tracked).State == EntityState.Unchanged)
            await _db.Entry(tracked).ReloadAsync(ct);
        MediaReconciliationJob? current = state is null ? null : await JobAsync(state.LatestJobId, ct);
        if (current is not null && !current.IsTerminal)
        {
            var definition = _db.Set<MediaReconciliationJobEntity>().Local.SingleOrDefault(row => row.Id == current.Id && _db.Entry(row).State == EntityState.Added)?.Definition ??
                await _db.Set<MediaReconciliationDefinitionEntity>().AsNoTracking().SingleAsync(row => row.JobId == current.Id, ct);
            if (definition.CancelRequested)
                return Error.Conflict("MediaReconciliation.Cancelling", "The active request is stopping; retry after its sources are released.");
        }
        else
        {
            bool exists = demand.Subject switch
            {
                MediaReconciliationSubject.LogicalRom => await _db.RomFiles.AnyAsync(row => row.Id == id, ct),
                MediaReconciliationSubject.OriginalLayout => await _db.OriginalDiscLayouts.AnyAsync(row => row.Id == id, ct),
                MediaReconciliationSubject.CatalogCdLayout => await _db.Database.SqlQuery<int>($"""SELECT 1 AS "Value" FROM romd."CatalogCdLayoutBindings" WHERE "CatalogFileId"={id}""").AnyAsync(ct),
                MediaReconciliationSubject.Representation => await MediaRepresentationOwnership.Owned(_db).AnyAsync(row => row.Id == id, ct),
                _ => throw new InvalidOperationException("Unsupported subject namespace.")
            };
            if (!exists)
                return Error.NotFound("MediaReconciliation.SubjectMissing", "The requested media subject is no longer retained.");
        }
        bool created = current is null || current.IsTerminal;
        if (created)
        {
            current = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SubjectWork(demand.Subject),
                $"{kind} {id}", demand.SelectionJobId, demand.ActorId, _clock.GetUtcNow());
            _db.Add(MediaReconciliationJobEntity.FromDomain(current, _clock.GetUtcNow()));
        }
        if (state is null)
        {
            state = new() { SubjectKind = kind, SubjectId = id, LatestJobId = current!.Id, RequestedVersion = 1, ConversionRequested = demand.ConversionRequested };
            _db.Add(state);
        }
        else
        {
            state.RequestedVersion = checked(state.RequestedVersion + 1);
            if (created)
            { state.LatestJobId = current!.Id; state.ConversionRequested = demand.ConversionRequested; }
            else
                state.ConversionRequested |= demand.ConversionRequested;
        }
        _db.Add(new MediaReconciliationRequestEntity
        {
            Id = demand.RequestId,
            JobId = current!.Id,
            ActorId = demand.ActorId,
            Cause = demand.Cause.ToString(),
            RequestedVersion = state.RequestedVersion,
            ConversionRequested = demand.ConversionRequested,
            OriginSelectionJobId = demand.SelectionJobId,
            RetryOfJobId = retryOf,
            CreatedAtUtcTicks = _clock.GetUtcNow().UtcTicks
        });
        return new MediaReconciliationAcceptance(current, created, demand.SelectionJobId is not null && current.OriginSelectionJobId == demand.SelectionJobId);
    }
    public Task<ErrorOr<MediaReconciliationAcceptance>> StageSelectionAsync(Guid requestId, MediaReconciliationFilter filter,
        long limit, bool conversionRequested, MediaReconciliationCause cause, Guid? actorId, CancellationToken ct = default)
    {
        if (cause == MediaReconciliationCause.Retry)
            throw new ArgumentException("Retries require the original terminal job identity.", nameof(cause));
        return StageSelectionCoreAsync(requestId, filter, limit, conversionRequested, cause, actorId, null, null, ct);
    }
    private async Task<ErrorOr<MediaReconciliationAcceptance>> StageSelectionCoreAsync(Guid requestId, MediaReconciliationFilter filter,
        long limit, bool conversionRequested, MediaReconciliationCause cause, Guid? actorId, MediaReconciliationBounds? acceptedBounds, Guid? retryOf, CancellationToken ct)
    {
        RequireTransaction();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(filter);
        if (requestId == Guid.Empty || actorId == Guid.Empty || !Enum.IsDefined(cause) || cause == MediaReconciliationCause.Selection ||
            cause is MediaReconciliationCause.Admin or MediaReconciliationCause.Retry && actorId is null)
            throw new ArgumentException("A selection requires its exact request and actor attribution.");
        // Validate the closed definition before accepting even an idempotent repeat.
        _ = new MediaReconciliationRequest.SelectionWork(filter, new(0, 0, 0, 0), limit, conversionRequested);
        await LockRequestAsync(requestId, ct);
        if (await PriorAsync(requestId, ct) is { } prior)
        {
            var existing = await JobAsync(prior.JobId, ct);
            bool sameFilter = existing.Request is MediaReconciliationRequest.SelectionWork selection && selection.Limit == limit && (selection.Filter, filter) switch
            {
                (MediaReconciliationFilter.AllSubjects, MediaReconciliationFilter.AllSubjects) => true,
                (MediaReconciliationFilter.RetainedRepresentations, MediaReconciliationFilter.RetainedRepresentations) => true,
                (MediaReconciliationFilter.KindSubjects left, MediaReconciliationFilter.KindSubjects right) => left.Kind == right.Kind && left.PlatformId == right.PlatformId,
                _ => false
            };
            return sameFilter && SameAttribution(prior, conversionRequested, cause, actorId, null, retryOf) ? new MediaReconciliationAcceptance(existing, false, false) : _conflict;
        }
        bool retainedOnly = filter is MediaReconciliationFilter.RetainedRepresentations;
        var bounds = acceptedBounds ?? new MediaReconciliationBounds(retainedOnly ? 0 : await _db.RomFiles.MaxAsync(row => (int?)row.Id, ct) ?? 0,
            retainedOnly ? 0 : await _db.OriginalDiscLayouts.MaxAsync(row => (int?)row.Id, ct) ?? 0,
            await _db.RomRepresentations.MaxAsync(row => (int?)row.Id, ct) ?? 0,
            retainedOnly ? 0 : await _db.CatalogReleaseFiles.MaxAsync(row => (int?)row.Id, ct) ?? 0);
        var job = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SelectionWork(filter, bounds, limit, conversionRequested),
            "Media selection", null, actorId, _clock.GetUtcNow());
        _db.Add(MediaReconciliationJobEntity.FromDomain(job, _clock.GetUtcNow()));
        _db.Add(new MediaReconciliationRequestEntity
        {
            Id = requestId,
            JobId = job.Id,
            ActorId = actorId,
            Cause = cause.ToString(),
            RequestedVersion = 0,
            ConversionRequested = conversionRequested,
            RetryOfJobId = retryOf,
            CreatedAtUtcTicks = _clock.GetUtcNow().UtcTicks
        });
        return new MediaReconciliationAcceptance(job, true, false);
    }
}
