using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Dat;
using Romd.Persistence;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Subscriptions;

public sealed class DatSubscriptionService : IDatSubscriptionService
{
    private const string CatalogId = "redump/psx/discs";
    private static readonly string[] Terminal = ["Completed", "CompletedWithErrors", "Failed", "Cancelled", "Deferred"];
    private readonly RomdDbContext _db;
    private readonly IDatRepository _dats;
    private readonly IFileStorageService _storage;
    private readonly IDatReplacementReview _review;
    private readonly ISignedDatCatalogClient _client;
    private readonly TimeProvider _time;

    public DatSubscriptionService(RomdDbContext db, IDatRepository dats, IFileStorageService storage,
        IDatReplacementReview review, ISignedDatCatalogClient client, TimeProvider time)
    {
        _db = db; _dats = dats; _storage = storage; _review = review; _client = client; _time = time;
    }

    public async Task<ErrorOr<DatSubscriptionStatus>> GetAsync(int datId, CancellationToken ct)
    {
        var dat = await _dats.GetByIdAsync(datId, ct);
        if (dat is null) return Error.NotFound("DatSubscription.NotFound", "The catalog no longer exists.");
        bool available = _client.Enabled && await EligibleAsync(dat, ct);
        var row = await _db.DatSubscriptions.SingleOrDefaultAsync(s => s.DatSourceId == dat.DatSourceId, ct);
        return await StatusAsync(row, available, ct);
    }

    public async Task<ErrorOr<DatSubscriptionStatus>> CheckAsync(int datId, CancellationToken ct)
    {
        var dat = await _dats.GetByIdAsync(datId, ct);
        if (dat is null) return Error.NotFound("DatSubscription.NotFound", "The catalog no longer exists.");
        if (!_client.Enabled || !await EligibleAsync(dat, ct)) return Unavailable();
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        if (!await LockAsync(dat.DatSourceId, ct)) return Busy();
        var row = await _db.DatSubscriptions.AsTracking().SingleOrDefaultAsync(s => s.DatSourceId == dat.DatSourceId, ct);
        if (row?.JobId is Guid running && await _db.Jobs.AnyAsync(j => j.Id == running && !Terminal.Contains(j.Phase), ct)) return Busy();
        if (row is null)
        {
            row = new DatSubscriptionEntity { DatSourceId = dat.DatSourceId, CatalogId = CatalogId };
            _db.DatSubscriptions.Add(row);
        }
        // No candidate ingestion or claims before approval. Holding this short-lived
        // transaction serializes explicit checks/approval, including first enrollment.
        var fetched = await _client.FetchPlayStationAsync(ct);
        row.LastCheckedAt = _time.GetUtcNow();
        if (fetched.IsError)
        {
            row.State = "CheckFailed";
            row.Message = fetched.FirstError.Description;
        }
        else
        {
            await using var candidate = new MemoryStream(fetched.Value, writable: false);
            var preview = await _review.PreviewAsync(datId, candidate, ct);
            if (preview.IsError)
            {
                row.State = "CheckFailed";
                row.Message = preview.FirstError.Description;
            }
            else
            {
                candidate.Position = 0;
                var stored = await _storage.StoreAsync(candidate, ct: ct);
                row.CandidateFileId = stored.File.Id;
                row.CandidateSha256 = preview.Value.CandidateSha256;
                row.ActiveSha256 = preview.Value.ActiveSha256;
                row.State = preview.Value.Unchanged ? "UpToDate" : "UpdateAvailable";
                row.Message = null;
                row.JobId = null;
            }
        }
        DatSubscriptionSchedule.Advance(row, _time.GetUtcNow());
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await StatusAsync(row, true, ct);
    }

    public async Task<ErrorOr<DatReplacementPreview>> PreviewAsync(int datId, CancellationToken ct)
    {
        var dat = await _dats.GetByIdAsync(datId, ct);
        if (dat is null) return Error.NotFound("DatSubscription.NotFound", "The catalog no longer exists.");
        var row = await _db.DatSubscriptions.SingleOrDefaultAsync(s => s.DatSourceId == dat.DatSourceId, ct);
        if (row?.State != "UpdateAvailable" || row.CandidateFileId is not int fileId) return CheckAgain();
        await using var candidate = await _storage.RetrieveByIdAsync(fileId, ct);
        if (candidate is null) return CheckAgain();
        var result = await _review.PreviewAsync(datId, candidate, ct);
        if (!result.IsError && (result.Value.ActiveSha256 != row.ActiveSha256 || result.Value.CandidateSha256 != row.CandidateSha256)) return CheckAgain();
        return result;
    }

    public async Task<ErrorOr<DatChangePage>> ChangesAsync(int datId, DatChangeQuery query, CancellationToken ct)
    {
        var dat = await _dats.GetByIdAsync(datId, ct);
        if (dat is null) return Error.NotFound("DatSubscription.NotFound", "The catalog no longer exists.");
        var row = await _db.DatSubscriptions.SingleOrDefaultAsync(s => s.DatSourceId == dat.DatSourceId, ct);
        if (row?.State != "UpdateAvailable" || row.CandidateFileId is not int fileId
            || row.ActiveSha256 != query.ActiveSha256 || row.CandidateSha256 != query.CandidateSha256) return CheckAgain();
        await using var candidate = await _storage.RetrieveByIdAsync(fileId, ct);
        if (candidate is null) return CheckAgain();
        return await _review.ChangesAsync(datId, dat.Name, candidate, query, ct);
    }

    public async Task<ErrorOr<ReplaceDatJobCreationResult>> ApplyAsync(int datId, string activeHash, string candidateHash, CancellationToken ct)
    {
        var dat = await _dats.GetByIdAsync(datId, ct);
        if (dat is null) return Error.NotFound("DatSubscription.NotFound", "The catalog no longer exists.");
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        if (!await LockAsync(dat.DatSourceId, ct)) return Busy();
        var row = await _db.DatSubscriptions.AsTracking().SingleOrDefaultAsync(s => s.DatSourceId == dat.DatSourceId, ct);
        if (row?.CandidateFileId is not int fileId || row.ActiveSha256 != activeHash || row.CandidateSha256 != candidateHash) return CheckAgain();
        // The same approved candidate returns its durable job after ambiguous HTTP delivery.
        if (row.JobId is Guid existing)
            return new ReplaceDatJobCreationResult(existing, existing.ToString("N"), $"/jobs/{existing}");
        if (!_client.Enabled || !await EligibleAsync(dat, ct)) return Unavailable();
        if (row.State != "UpdateAvailable") return CheckAgain();
        await using var candidate = await _storage.RetrieveByIdAsync(fileId, ct);
        if (candidate is null) return CheckAgain();
        var result = await _review.ApplyAsync(datId, candidate, activeHash, candidateHash, ct);
        if (result.IsError) return result.Errors;
        row.JobId = result.Value.JobId;
        row.State = "Applying";
        await _db.SaveChangesAsync(ct);
        // Existing job and dispatch intent commit with the subscription's receipt.
        await transaction.CommitAsync(ct);
        return result;
    }

    private async Task<bool> EligibleAsync(DatFile dat, CancellationToken ct) =>
        dat.Name == "Sony - PlayStation" && dat.Lifecycle == DatFileLifecycle.Active
        && await _db.Platforms.AnyAsync(p => p.Id == dat.PlatformId && p.ShortName == "psx", ct)
        && await _db.DatSources.AnyAsync(s => s.Id == dat.DatSourceId
            && _db.CatalogSources.Any(c => c.Id == s.CatalogSourceId && c.Status == "Active"), ct);

    private Task<bool> LockAsync(int sourceId, CancellationToken ct) => _db.Database
        .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(145, {sourceId}) AS \"Value\"").SingleAsync(ct);

    private async Task<DatSubscriptionStatus> StatusAsync(DatSubscriptionEntity? row, bool available, CancellationToken ct)
    {
        if (row is null) return new(available, false, "NotSubscribed", null, null, null, null);
        string state = row.State;
        string? message = row.Message;
        if (state == "Applying" && row.JobId is Guid id)
        {
            var phase = await _db.Jobs.Where(j => j.Id == id).Select(j => j.Phase).SingleOrDefaultAsync(ct);
            if (phase is null || Terminal.Contains(phase))
            {
                var active = row.DatSourceId is int sourceId ? await _dats.GetActiveBySourceIdAsync(sourceId, ct) : null;
                var file = active is null ? null : await _db.Files.SingleOrDefaultAsync(f => f.Id == active.FileId, ct);
                if (file?.Sha256.ToString() == row.CandidateSha256) state = "UpToDate";
                else { state = "CheckFailed"; message = "The update was not activated. Check its job for details, then check for updates again."; }
            }
        }
        return new(available, true, state, row.LastCheckedAt, message, row.CandidateSha256, row.JobId);
    }
    private static Error Busy() => Error.Conflict("DatSubscription.Busy", "A check or update is already in progress. Try again after it finishes.");
    private static Error CheckAgain() => Error.Conflict("DatSubscription.Stale", "This review is no longer current. Check for updates again.");
    private static Error Unavailable() => Error.Validation("DatSubscription.Unavailable", "Subscriptions are not configured for this active platform source.");
}
