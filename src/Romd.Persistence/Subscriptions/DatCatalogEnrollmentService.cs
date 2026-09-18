using Romd.Persistence.ReferenceData;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Security;
using Romd.Domain.Source.Dat;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Subscriptions;

/// <summary>Subscription enrollment and scheduled checks; candidates remain separate from ingestion until approval.</summary>
public sealed class DatCatalogEnrollmentService : IDatCatalogEnrollmentService
{
    private static readonly string[] Terminal = ["Completed", "CompletedWithErrors", "Failed", "Cancelled", "Deferred"];
    private readonly RomdDbContext _db;
    private readonly ISignedDatCatalogClient _client;
    private readonly IDatRepository _dats;
    private readonly IFileStorageService _storage;
    private readonly IDatReplacementReview _review;
    private readonly IUploadJobCreator _uploads;
    private readonly ICurrentUser _user;
    private readonly TimeProvider _time;
    public DatCatalogEnrollmentService(RomdDbContext db, ISignedDatCatalogClient client, IDatRepository dats,
        IFileStorageService storage, IDatReplacementReview review, IUploadJobCreator uploads, ICurrentUser user, TimeProvider time)
    {
        _db = db; _client = client; _dats = dats; _storage = storage; _review = review;
        _uploads = uploads; _user = user; _time = time;
    }

    public async Task<ErrorOr<Success>> StopAsync(int id, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var identity = await _db.DatSubscriptions.Where(x => x.Id == id).Select(x => x.CatalogId).SingleOrDefaultAsync(ct);
        if (identity is null) return EnrollmentErrors.NotFound();
        if (!await LockCatalogAsync(identity, ct)) return EnrollmentErrors.Busy();
        var row = await _db.DatSubscriptions.AsTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return EnrollmentErrors.NotFound();
        if (row.DatSourceId is int sourceId)
        {
            if (!await LockSourceAsync(sourceId, ct)) return EnrollmentErrors.Busy();
            await _db.Entry(row).ReloadAsync(ct);
        }
        if (await RunningAsync(row, ct)) return EnrollmentErrors.Busy();
        // Only detach update tracking. Installed versions, source lifecycle, files,
        // and completed jobs remain owned by their existing repositories.
        _db.DatSubscriptions.Remove(row);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success;
    }

    public async Task<ErrorOr<DatCatalogDirectory>> DiscoverAsync(CancellationToken ct)
    {
        var subscriptions = new List<DatCatalogSubscription>();
        foreach (var row in await _db.DatSubscriptions.OrderBy(x => x.CatalogId).ThenBy(x => x.Id).ToListAsync(ct))
            subscriptions.Add(await StatusAsync(row, ct));
        if (!_client.Enabled) return new DatCatalogDirectory(false, "Catalog subscriptions are not configured on this server.", [], subscriptions);
        var result = await _client.DiscoverAsync(ct);
        return new DatCatalogDirectory(true, result.IsError ? "The catalog directory could not be verified. Your existing catalogs are unchanged. Try again." : null,
            result.IsError ? [] : result.Value, subscriptions);
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

    private IQueryable<DatSubscriptionEntity> DueChecks()
    {
        var now = _time.GetUtcNow(); var yesterday = now.AddDays(-1);
        return EligibleChecks().Where(s => s.State != "ReadyToImport" && s.State != "UpdateAvailable"
            && (s.NextCheckAt <= now || (s.NextCheckAt == null && (s.LastCheckedAt == null || s.LastCheckedAt <= yesterday)))
            && !_db.Jobs.Any(j => j.Id == s.JobId && !Terminal.Contains(j.Phase)));
    }
    public async Task<IReadOnlyList<int>> GetDueCheckIdsAsync(CancellationToken ct) => !_client.Enabled ? [] :
        await DueChecks().OrderBy(s => s.NextCheckAt ?? s.LastCheckedAt).ThenBy(s => s.Id).Select(s => s.Id).Take(25).ToListAsync(ct);

    public async Task<ErrorOr<DatCatalogSubscription>> CheckScheduledAsync(int id, CancellationToken ct)
    {
        var catalog = await _db.DatSubscriptions.Where(s => s.Id == id).Select(s => s.CatalogId).SingleOrDefaultAsync(ct);
        return catalog is null ? EnrollmentErrors.NotFound() : await CheckCoreAsync(catalog, id, ct);
    }
    public Task<ErrorOr<DatCatalogSubscription>> CheckAsync(string catalogId, CancellationToken ct) => CheckCoreAsync(catalogId, null, ct);

    // Expected provider failures advance the schedule in the check transaction.
    // Unexpected failures are recorded in a fresh scope after that transaction rolls back.
    public async Task RecordScheduledFailureAsync(int id, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var catalog = await _db.DatSubscriptions.Where(s => s.Id == id).Select(s => s.CatalogId).SingleOrDefaultAsync(ct);
        if (catalog is null || !await LockCatalogAsync(catalog, ct)) return;
        var row = await _db.DatSubscriptions.AsTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
        if (row is null) return;
        if (row.DatSourceId is int sourceId)
        {
            if (!await LockSourceAsync(sourceId, ct)) return;
            await _db.Entry(row).ReloadAsync(ct);
        }
        // Do not overwrite a manual check, removal, or accepted job that won the race.
        if (!await DueChecks().AnyAsync(s => s.Id == id, ct)) return;
        row.State = "CheckFailed"; row.Message = EnrollmentErrors.CheckFailed().Description;
        row.LastCheckedAt = _time.GetUtcNow();
        DatSubscriptionSchedule.Advance(row, _time.GetUtcNow());
        await _db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }
    private async Task<ErrorOr<DatCatalogSubscription>> CheckCoreAsync(string catalogId, int? scheduledId, CancellationToken ct)
    {
        if (!_client.Enabled) return EnrollmentErrors.Unavailable();
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        if (!await LockCatalogAsync(catalogId, ct)) return EnrollmentErrors.Busy();
        var matches = await _db.DatSubscriptions.AsTracking().Where(x => x.CatalogId == catalogId && (scheduledId == null || x.Id == scheduledId)).Take(2).ToListAsync(ct);
        if (matches.Count > 1) return EnrollmentErrors.Ambiguous();
        var row = matches.SingleOrDefault();
        if (row?.DatSourceId is int existingSource)
        {
            if (!await LockSourceAsync(existingSource, ct)) return EnrollmentErrors.Busy();
            await _db.Entry(row).ReloadAsync(ct);
        }
        if (scheduledId is int dueId && (row is null || !await DueChecks().AnyAsync(x => x.Id == dueId, ct))) return EnrollmentErrors.Busy();
        if (row is not null && await RunningAsync(row, ct)) return EnrollmentErrors.Busy();
        var directory = await _client.DiscoverAsync(ct);
        var catalog = directory.IsError ? null : directory.Value.SingleOrDefault(x => x.CatalogId == catalogId);
        if (catalog is null || catalog.Health != "healthy" || catalog.DocumentHash is null)
        {
            if (row is null) return EnrollmentErrors.Unavailable();
            row.State = "CheckFailed"; row.Message = EnrollmentErrors.CheckFailed().Description; row.LastCheckedAt = _time.GetUtcNow();
            DatSubscriptionSchedule.Advance(row, _time.GetUtcNow());
            await _db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return await StatusAsync(row, ct);
        }
        var definition = await _db.Platforms.Where(x => x.CanonicalKey == catalog.SystemId).Where(x => x.Ownership != null).Select(x => new { x.Retired }).SingleOrDefaultAsync(ct);
        if (definition is null || definition.Retired)
            return EnrollmentErrors.PlatformMissing();
        var platformId = await PlatformAsync(catalog.SystemId, ct);
        if (platformId is null) return EnrollmentErrors.PlatformMissing();
        row ??= new DatSubscriptionEntity { CatalogId = catalogId };
        var active = await ActiveAsync(row, ct);
        if (row.DatSourceId is null && active is null && row.JobId is null)
        {
            var existing = await _db.DatFiles.Where(x => x.PlatformId == platformId && x.Name == catalog.Name && x.Lifecycle == "Active")
                .Select(x => x.Id).Take(2).ToListAsync(ct);
            if (existing.Count > 1) return EnrollmentErrors.Ambiguous();
            if (existing.Count == 1) active = await _dats.GetByIdAsync(existing[0], ct);
        }
        if (active is not null)
        {
            if (active.PlatformId != platformId || active.Name != catalog.Name || !await SourceEnabledAsync(active.DatSourceId, ct)) return EnrollmentErrors.Unavailable();
            if (!await LockSourceAsync(active.DatSourceId, ct)) return EnrollmentErrors.Busy();
            // Legacy source actions hold the source lock, not the catalog lock.
            // Reload after acquiring it so their accepted job cannot be overwritten.
            var bound = await _db.DatSubscriptions.AsTracking().SingleOrDefaultAsync(x => x.DatSourceId == active.DatSourceId, ct);
            if (bound is not null)
            {
                await _db.Entry(bound).ReloadAsync(ct);
                if (bound.CatalogId != catalogId || (row.Id != 0 && row.Id != bound.Id)) return EnrollmentErrors.Ambiguous();
                row = bound;
                if (await RunningAsync(row, ct)) return EnrollmentErrors.Busy();
            }
            row.DatSourceId = active.DatSourceId;
        }
        else if (row.DatSourceId is not null) return EnrollmentErrors.Unavailable();
        if (row.Id == 0) _db.DatSubscriptions.Add(row);
        row.PlatformId = platformId; row.SystemId = catalog.SystemId; row.ExpectedName = catalog.Name;
        row.LastCheckedAt = _time.GetUtcNow();
        var fetched = await _client.FetchAsync(catalog.CatalogId, catalog.SystemId, catalog.Name, ct);
        if (fetched.IsError)
        {
            row.State = "CheckFailed"; row.Message = EnrollmentErrors.CheckFailed().Description;
        }
        else
        {
            await using var document = new MemoryStream(fetched.Value, writable: false);
            var preview = active is null ? await _review.PreviewInitialAsync(catalog.Name, document, ct) : await _review.PreviewAsync(active.Id, document, ct);
            if (preview.IsError) { row.State = "CheckFailed"; row.Message = preview.FirstError.Description; }
            else
            {
                document.Position = 0;
                var stored = await _storage.StoreAsync(document, ct: ct);
                row.CandidateFileId = stored.File.Id; row.CandidateSha256 = preview.Value.CandidateSha256;
                row.ActiveSha256 = preview.Value.ActiveSha256; row.JobId = null; row.Message = null;
                row.State = preview.Value.Unchanged ? "UpToDate" : active is null ? "ReadyToImport" : "UpdateAvailable";
            }
        }
        DatSubscriptionSchedule.Advance(row, _time.GetUtcNow());
        await _db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return await StatusAsync(row, ct);
    }

    public async Task<ErrorOr<DatReplacementPreview>> PreviewAsync(int id, CancellationToken ct)
    {
        var row = await _db.DatSubscriptions.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return EnrollmentErrors.NotFound();
        return await ReviewAsync(row, ct);
    }
    public async Task<ErrorOr<DatChangePage>> ChangesAsync(int id, DatChangeQuery query, CancellationToken ct)
    {
        var row = await _db.DatSubscriptions.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return EnrollmentErrors.NotFound();
        if (row.ActiveSha256 != query.ActiveSha256 || row.CandidateSha256 != query.CandidateSha256) return EnrollmentErrors.Stale();
        return await ReadReviewAsync(row, (activeId, name, stream) => _review.ChangesAsync(activeId, name, stream, query, ct), ct);
    }
    private Task<ErrorOr<DatReplacementPreview>> ReviewAsync(DatSubscriptionEntity row, CancellationToken ct) =>
        ReadReviewAsync<DatReplacementPreview>(row, async (activeId, name, stream) =>
        {
            var result = activeId is int id ? await _review.PreviewAsync(id, stream, ct) : await _review.PreviewInitialAsync(name, stream, ct);
            if (!result.IsError && (result.Value.ActiveSha256 != row.ActiveSha256 || result.Value.CandidateSha256 != row.CandidateSha256)) return EnrollmentErrors.Stale();
            return result;
        }, ct);

    private async Task<ErrorOr<T>> ReadReviewAsync<T>(DatSubscriptionEntity row,
        Func<int?, string, Stream, Task<ErrorOr<T>>> read, CancellationToken ct)
    {
        if (row.State is not ("ReadyToImport" or "UpdateAvailable") || row.CandidateFileId is not int fileId) return EnrollmentErrors.Stale();
        var active = await ActiveAsync(row, ct);
        if (row.DatSourceId is not null && active is null) return EnrollmentErrors.Stale();
        if (row.PlatformId is not int platformId || !await _db.Platforms.AnyAsync(x => x.Id == platformId, ct)) return EnrollmentErrors.PlatformMissing();
        var systemKey = await _db.Platforms.Where(x => x.Id == platformId).Select(x => x.CanonicalKey).SingleAsync(ct);
        var definition = await _db.Platforms.Where(x => x.CanonicalKey == systemKey).Where(x => x.Ownership != null).Select(x => new { x.Retired }).SingleOrDefaultAsync(ct);
        if (definition is null || definition.Retired) return EnrollmentErrors.PlatformMissing();
        if (active is not null && (active.PlatformId != platformId || !await SourceEnabledAsync(active.DatSourceId, ct))) return EnrollmentErrors.Stale();
        if (active is null && await _db.DatFiles.AnyAsync(x => x.PlatformId == platformId && x.Name == row.ExpectedName && x.Lifecycle == "Active", ct)) return EnrollmentErrors.Stale();
        await using var document = await _storage.RetrieveByIdAsync(fileId, ct);
        if (document is null) return EnrollmentErrors.Stale();
        return await read(active?.Id, row.ExpectedName!, document);
    }

    public async Task<ErrorOr<UploadJobCreationResult>> ApplyAsync(int id, string activeHash, string candidateHash, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var identity = await _db.DatSubscriptions.Where(x => x.Id == id).Select(x => x.CatalogId).SingleOrDefaultAsync(ct);
        if (identity is null) return EnrollmentErrors.NotFound();
        if (!await LockCatalogAsync(identity, ct)) return EnrollmentErrors.Busy();
        var row = await _db.DatSubscriptions.AsTracking().SingleAsync(x => x.Id == id, ct);
        if (row.DatSourceId is int existingSource)
        {
            if (!await LockSourceAsync(existingSource, ct)) return EnrollmentErrors.Busy();
            await _db.Entry(row).ReloadAsync(ct);
        }
        if (row.ActiveSha256 != activeHash || row.CandidateSha256 != candidateHash) return EnrollmentErrors.Stale();
        if (row.JobId is Guid accepted) return new UploadJobCreationResult(accepted, accepted.ToString("N"), $"/jobs/{accepted}");
        if (!_client.Enabled) return EnrollmentErrors.Unavailable();
        var active = await ActiveAsync(row, ct);
        if (active is not null)
        {
            if (!await LockSourceAsync(active.DatSourceId, ct)) return EnrollmentErrors.Busy();
            await _db.Entry(row).ReloadAsync(ct);
            if (row.ActiveSha256 != activeHash || row.CandidateSha256 != candidateHash) return EnrollmentErrors.Stale();
            if (row.JobId is Guid existing) return new UploadJobCreationResult(existing, existing.ToString("N"), $"/jobs/{existing}");
        }
        var review = await ReviewAsync(row, ct);
        if (review.IsError) return review.Errors;
        await using var document = await _storage.RetrieveByIdAsync(row.CandidateFileId!.Value, ct);
        if (document is null) return EnrollmentErrors.Stale();
        UploadJobCreationResult result;
        if (active is null)
            result = await _uploads.CreateAsync(document, "subscribed-catalog.dat", new UploadJobOptions { PlatformId = row.PlatformId, CreatedByUserId = _user.UserId }, ct);
        else
        {
            var replaced = await _review.ApplyAsync(active.Id, document, activeHash, candidateHash, ct);
            if (replaced.IsError) return replaced.Errors;
            result = new(replaced.Value.JobId, replaced.Value.BackgroundJobId, replaced.Value.StatusUrl);
            row.DatSourceId = active.DatSourceId;
        }
        row.JobId = result.JobId; row.State = "Applying"; row.Message = null;
        await _db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return result;
    }

    private async Task<DatFile?> ActiveAsync(DatSubscriptionEntity row, CancellationToken ct)
    {
        if (row.DatSourceId is int sourceId) return await _dats.GetActiveBySourceIdAsync(sourceId, ct);
        if (row.JobId is null || await RunningAsync(row, ct) || row.CandidateFileId is not int fileId) return null;
        // The upload's exact retained document identifies its result without a
        // new completion scheduler. Do not infer a source from a display name.
        var ids = await _db.DatFiles.Where(x => x.FileId == fileId && x.PlatformId == row.PlatformId && x.Lifecycle == "Active")
            .Select(x => x.Id).Take(2).ToListAsync(ct);
        return ids.Count == 1 ? await _dats.GetByIdAsync(ids[0], ct) : null;
    }
    private async Task<DatCatalogSubscription> StatusAsync(DatSubscriptionEntity row, CancellationToken ct)
    {
        var active = await ActiveAsync(row, ct);
        var state = row.State; var message = row.Message;
        if (state == "Applying")
        {
            if (await RunningAsync(row, ct)) state = "Applying";
            else if (active is not null && active.FileId == row.CandidateFileId) { state = "UpToDate"; message = null; }
            else { state = "CheckFailed"; message = "The catalog was not activated. View the job for details, then retry the check."; }
        }
        var paused = !_client.Enabled || !await EligibleChecks().AnyAsync(s => s.Id == row.Id, ct);
        var nextCheck = paused || state is "ReadyToImport" or "UpdateAvailable" ? (DateTimeOffset?)null
            : row.NextCheckAt ?? row.LastCheckedAt?.AddDays(1) ?? _time.GetUtcNow();
        return new(row.Id, row.CatalogId, row.SystemId, row.ExpectedName ?? active?.Name ?? row.CatalogId,
            row.PlatformId ?? active?.PlatformId, active?.Id, state, row.LastCheckedAt, message, row.CandidateSha256, row.JobId, nextCheck, paused);
    }
    private async Task<int?> PlatformAsync(string systemId, CancellationToken ct)
    {
        return await _db.Platforms.Where(x => x.CanonicalKey == systemId)
            .Select(x => (int?)x.Id).SingleOrDefaultAsync(ct);
    }
    private Task<bool> RunningAsync(DatSubscriptionEntity row, CancellationToken ct) => _db.Jobs.AnyAsync(x => x.Id == row.JobId && !Terminal.Contains(x.Phase), ct);
    private Task<bool> SourceEnabledAsync(int sourceId, CancellationToken ct) => _db.DatSources.AnyAsync(s => s.Id == sourceId && _db.CatalogSources.Any(c => c.Id == s.CatalogSourceId && c.Status == "Active"), ct);
    private Task<bool> LockCatalogAsync(string id, CancellationToken ct) => _db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(146, hashtext({id})) AS \"Value\"").SingleAsync(ct);
    private Task<bool> LockSourceAsync(int id, CancellationToken ct) => _db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(145, {id}) AS \"Value\"").SingleAsync(ct);
}

internal static class EnrollmentErrors
{
    internal static Error NotFound() => Error.NotFound("DatEnrollment.NotFound", "The subscription no longer exists.");
    internal static Error Busy() => Error.Conflict("DatEnrollment.Busy", "A check or import is already running. Try again after it finishes.");
    internal static Error Unavailable() => Error.Validation("DatEnrollment.Unavailable", "This catalog is not currently available for subscription. Your installed catalogs are unchanged.");
    internal static Error CheckFailed() => Error.Failure("DatEnrollment.CheckFailed", "The catalog could not be downloaded and verified. Your installed catalogs are unchanged. Retry the check.");
    internal static Error Stale() => Error.Conflict("DatEnrollment.Stale", "The catalog changed after this review. Check for updates again before applying.");
    internal static Error PlatformMissing() => Error.Validation("DatEnrollment.PlatformMissing", "This publisher system key is unresolved. Register the system explicitly before subscribing; discovery never creates or reassigns reference identities.");
    internal static Error Ambiguous() => Error.Conflict("DatEnrollment.Ambiguous", "More than one local source matches this catalog. Open the source you want to track from Systems.");
}
