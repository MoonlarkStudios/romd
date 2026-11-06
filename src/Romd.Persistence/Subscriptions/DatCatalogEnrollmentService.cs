using EnrollmentErrors = Romd.Admin.Application.Source.Dat.DatEnrollmentErrors;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Pagination;
using Romd.Domain.Source.Dat;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Subscriptions;

/// <summary>Subscription enrollment and scheduled checks; candidates remain separate from ingestion until approval.</summary>
public sealed partial class DatCatalogEnrollmentService : IDatCatalogEnrollmentService, IDatSubscriptionStopStore, IDatSubscriptionCheckReader, IDatSubscriptionCheckStore
{
    private static readonly string[] Terminal = ["Completed", "CompletedWithErrors", "Failed", "Cancelled", "Deferred"];
    private readonly RomdDbContext _db;
    private readonly ISignedDatCatalogClient _client;
    private readonly IDatRepository _dats;
    private readonly IFileStorageService _storage;
    private readonly IDatReplacementReview _review;
    private readonly TimeProvider _time;
    public DatCatalogEnrollmentService(RomdDbContext db, ISignedDatCatalogClient client, IDatRepository dats,
        IFileStorageService storage, IDatReplacementReview review, TimeProvider time)
    {
        _db = db;
        _db.SavedChanges += RecordSuccessfulCheckFlush;
        _client = client;
        _dats = dats;
        _storage = storage;
        _review = review;
        _time = time;
    }

    public async Task<ErrorOr<Success>> StopStagedAsync(int id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Stopping a subscription requires a caller-owned transaction.");
        var identity = await _db.DatSubscriptions.Where(x => x.Id == id).Select(x => x.CatalogId).SingleOrDefaultAsync(ct);
        if (identity is null)
            return EnrollmentErrors.NotFound();
        if (!await LockCatalogAsync(identity, ct))
            return EnrollmentErrors.Busy();
        var row = await _db.DatSubscriptions.AsTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null)
            return EnrollmentErrors.NotFound();
        if (row.DatSourceId is int sourceId)
        {
            if (!await LockSourceAsync(sourceId, ct))
                return EnrollmentErrors.Busy();
            await _db.Entry(row).ReloadAsync(ct);
        }
        if (await RunningAsync(row, ct))
            return EnrollmentErrors.Busy();
        // Only detach update tracking. Installed versions, source lifecycle, files,
        // and completed jobs remain owned by their existing repositories.
        _db.DatSubscriptions.Remove(row);
        return Result.Success;
    }

    public async Task<DatCatalogDirectory> ListCatalogsAsync(CancellationToken ct)
    {
        if (!_client.Enabled)
            return new DatCatalogDirectory(false, "Catalog subscriptions are not configured on this server.", []);
        var result = await _client.DiscoverAsync(ct);
        return new DatCatalogDirectory(true, result.IsError ? "The catalog directory could not be verified. Your existing catalogs are unchanged. Try again." : null,
            result.IsError ? [] : result.Value);
    }
    public async Task<IReadOnlyList<DatCatalogSubscription>> ListAsync(CancellationToken ct)
    {
        var subscriptions = new List<DatCatalogSubscription>();
        foreach (var row in await _db.DatSubscriptions.OrderBy(x => x.CatalogId).ThenBy(x => x.Id).ToListAsync(ct))
            subscriptions.Add(await StatusAsync(row, ct));
        return subscriptions;
    }
    public async Task<ErrorOr<DatCatalogSubscription>> GetAsync(int id, CancellationToken ct)
    {
        var row = await _db.DatSubscriptions.SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? EnrollmentErrors.NotFound() : await StatusAsync(row, ct);
    }

    private IQueryable<DatSubscriptionEntity> EligibleChecks() => _db.DatSubscriptions.Where(s =>
        _db.Platforms.Any(p => p.IsEnabled != false && (p.Id == s.PlatformId ||
            (s.PlatformId == null && _db.DatFiles.Any(d => d.DatSourceId == s.DatSourceId && d.PlatformId == p.Id && d.Lifecycle == "Active"))))
        && (s.DatSourceId == null || _db.DatSources.Any(d => d.Id == s.DatSourceId && _db.CatalogSources.Any(c => c.Id == d.CatalogSourceId && c.Status == "Active"))));

    private IQueryable<DatSubscriptionEntity> DueChecks(DateTimeOffset now)
    {
        var yesterday = now.AddDays(-1);
        return EligibleChecks().Where(s => s.State != "ReadyToImport" && s.State != "UpdateAvailable"
            && (s.NextCheckAt <= now || (s.NextCheckAt == null && (s.LastCheckedAt == null || s.LastCheckedAt <= yesterday)))
            && !_db.Jobs.Any(j => j.Id == s.JobId && !Terminal.Contains(j.Phase)));
    }
    public async Task<IReadOnlyList<int>> GetDueCheckIdsAsync(CancellationToken ct) => !_client.Enabled ? [] :
        await DueChecks(_time.GetUtcNow()).OrderBy(s => s.NextCheckAt ?? s.LastCheckedAt).ThenBy(s => s.Id).Select(s => s.Id).Take(25).ToListAsync(ct);

    // Expected provider failures advance the schedule in the check transaction.
    // Unexpected failures are recorded in a fresh scope after that transaction rolls back.
    public async Task RecordScheduledFailureAsync(int id, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var catalog = await _db.DatSubscriptions.Where(s => s.Id == id).Select(s => s.CatalogId).SingleOrDefaultAsync(ct);
        if (catalog is null || !await LockCatalogAsync(catalog, ct))
            return;
        var row = await _db.DatSubscriptions.AsTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
        if (row is null)
            return;
        if (row.DatSourceId is int sourceId)
        {
            if (!await LockSourceAsync(sourceId, ct))
                return;
            await _db.Entry(row).ReloadAsync(ct);
        }
        // Do not overwrite a manual check, removal, or accepted job that won the race.
        if (!await DueChecks(_time.GetUtcNow()).AnyAsync(s => s.Id == id, ct))
            return;
        row.State = "CheckFailed";
        row.Message = EnrollmentErrors.CheckFailed().Description;
        row.LastCheckedAt = _time.GetUtcNow();
        DatSubscriptionSchedule.Advance(row, _time.GetUtcNow());
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    public async Task<ErrorOr<DatReplacementPreview>> PreviewAsync(int id, CancellationToken ct)
    {
        var row = await _db.DatSubscriptions.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null)
            return EnrollmentErrors.NotFound();
        return await ReviewAsync(row, ct);
    }
    public async Task<ErrorOr<PagedList<DatEntryChange>>> ListChangesAsync(int id, DatChangePageRequest page, CancellationToken ct)
    {
        var row = await PinnedRowAsync(id, page, ct);
        if (row.IsError)
            return row.Errors;
        return await ReadReviewAsync(row.Value, (activeId, name, stream) => _review.ListChangesAsync(activeId, name, stream, page, ct), ct);
    }
    public async Task<ErrorOr<DatEntryChangeDetail>> GetChangeAsync(int id, string entryName, DatChangePageRequest page, CancellationToken ct)
    {
        var row = await PinnedRowAsync(id, page, ct);
        if (row.IsError)
            return row.Errors;
        return await ReadReviewAsync(row.Value, (activeId, name, stream) => _review.GetEntryChangeAsync(activeId, name, stream, entryName, page, ct), ct);
    }
    // The subscription's reviewed pair pins every change read; a stale pin is a conflict.
    private async Task<ErrorOr<DatSubscriptionEntity>> PinnedRowAsync(int id, DatChangePageRequest page, CancellationToken ct)
    {
        var row = await _db.DatSubscriptions.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null)
            return EnrollmentErrors.NotFound();
        if (row.ActiveSha256 != page.ActiveSha256 || row.CandidateSha256 != page.CandidateSha256)
            return EnrollmentErrors.Stale();
        return row;
    }
    private Task<ErrorOr<DatReplacementPreview>> ReviewAsync(DatSubscriptionEntity row, CancellationToken ct) =>
        ReadReviewAsync<DatReplacementPreview>(row, async (activeId, name, stream) =>
        {
            var result = activeId is int id ? await _review.PreviewAsync(id, stream, ct) : await _review.PreviewInitialAsync(name, stream, ct);
            if (!result.IsError && (result.Value.ActiveSha256 != row.ActiveSha256 || result.Value.CandidateSha256 != row.CandidateSha256))
                return EnrollmentErrors.Stale();
            return result;
        }, ct);

    private async Task<ErrorOr<T>> ReadReviewAsync<T>(DatSubscriptionEntity row,
        Func<int?, string, Stream, Task<ErrorOr<T>>> read, CancellationToken ct)
    {
        if (row.State is not ("ReadyToImport" or "UpdateAvailable") || row.CandidateFileId is not int fileId)
            return EnrollmentErrors.Stale();
        var active = await ActiveAsync(row, ct);
        if (row.DatSourceId is not null && active is null)
            return EnrollmentErrors.Stale();
        // Rows from the retired per-source tracking carry no system until a check adopts them; the guard above
        // guarantees their bound source's active version, whose system they track.
        if ((row.PlatformId ?? active?.PlatformId) is not int platformId || !await _db.Platforms.AnyAsync(x => x.Id == platformId, ct))
            return EnrollmentErrors.PlatformMissing();
        var systemKey = await _db.Platforms.Where(x => x.Id == platformId).Select(x => x.CanonicalKey).SingleAsync(ct);
        var definition = await _db.Platforms.Where(x => x.CanonicalKey == systemKey).Where(x => x.Ownership != null).Select(x => new { x.Retired }).SingleOrDefaultAsync(ct);
        if (definition is null || definition.Retired)
            return EnrollmentErrors.PlatformMissing();
        if (active is not null && (active.PlatformId != platformId || !await SourceEnabledAsync(active.DatSourceId, ct)))
            return EnrollmentErrors.Stale();
        if (active is null && await _db.DatFiles.AnyAsync(x => x.PlatformId == platformId && x.Name == row.ExpectedName && x.Lifecycle == "Active", ct))
            return EnrollmentErrors.Stale();
        await using var document = await _storage.RetrieveByIdAsync(fileId, ct);
        if (document is null)
            return EnrollmentErrors.Stale();
        var result = await read(active?.Id, row.ExpectedName ?? active!.Name, document);
        return result.IsError ? ServerHeld(result.Errors) : result;
    }

    // The saved candidate and the installed document are held by the server, so a review that rejects them
    // depends on server state: a conflict, not invalid input.
    private static List<Error> ServerHeld(IEnumerable<Error> errors) => errors
        .Select(error => error.Type == ErrorType.Validation && error.Code is "DatReview.InvalidDocument" or "DatReview.Identity"
            ? Error.Conflict(error.Code, error.Description)
            : error)
        .ToList();

    private async Task<DatFile?> ActiveAsync(DatSubscriptionEntity row, CancellationToken ct)
    {
        if (row.DatSourceId is int sourceId)
            return await _dats.GetActiveBySourceIdAsync(sourceId, ct);
        if (row.JobId is null || await RunningAsync(row, ct) || row.CandidateFileId is not int fileId)
            return null;
        // The upload's exact retained document identifies its result without a
        // new completion scheduler. Do not infer a source from a display name.
        var ids = await _db.DatFiles.Where(x => x.FileId == fileId && x.PlatformId == row.PlatformId && x.Lifecycle == "Active")
            .Select(x => x.Id).Take(2).ToListAsync(ct);
        return ids.Count == 1 ? await _dats.GetByIdAsync(ids[0], ct) : null;
    }
    private async Task<DatCatalogSubscription> StatusAsync(DatSubscriptionEntity row, CancellationToken ct)
    {
        var active = await ActiveAsync(row, ct);
        var state = row.State;
        var message = row.Message;
        if (state == "Applying")
        {
            if (await RunningAsync(row, ct))
                state = "Applying";
            else if (active is not null && active.FileId == row.CandidateFileId)
            { state = "UpToDate"; message = null; }
            else
            { state = "CheckFailed"; message = "The catalog was not activated. View the job for details, then retry the check."; }
        }
        var paused = !_client.Enabled || !await EligibleChecks().AnyAsync(s => s.Id == row.Id, ct);
        var nextCheck = paused || state is "ReadyToImport" or "UpdateAvailable" ? (DateTimeOffset?)null
            : row.NextCheckAt ?? row.LastCheckedAt?.AddDays(1) ?? _time.GetUtcNow();
        return new(row.Id, row.CatalogId, row.SystemId, row.ExpectedName ?? active?.Name ?? row.CatalogId,
            row.PlatformId ?? active?.PlatformId, active?.Id, state, row.LastCheckedAt, message, row.CandidateSha256, row.JobId, nextCheck, paused);
    }
    private Task<bool> RunningAsync(DatSubscriptionEntity row, CancellationToken ct) => _db.Jobs.AnyAsync(x => x.Id == row.JobId && !Terminal.Contains(x.Phase), ct);
    private Task<bool> SourceEnabledAsync(int sourceId, CancellationToken ct) => _db.DatSources.AnyAsync(s => s.Id == sourceId && _db.CatalogSources.Any(c => c.Id == s.CatalogSourceId && c.Status == "Active"), ct);
    private Task<bool> LockCatalogAsync(string id, CancellationToken ct) => _db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(146, hashtext({id})) AS \"Value\"").SingleAsync(ct);
    private Task<bool> LockSourceAsync(int id, CancellationToken ct) => _db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(145, {id}) AS \"Value\"").SingleAsync(ct);
}
