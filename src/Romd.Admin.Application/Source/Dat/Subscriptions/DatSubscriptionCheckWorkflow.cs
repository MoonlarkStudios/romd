using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Dat;

namespace Romd.Admin.Application.Source.Dat.Subscriptions;

internal abstract record DatSubscriptionCheckOutcome : IAsyncDisposable
{
    internal sealed record Rejected(IReadOnlyList<Error> Errors) : DatSubscriptionCheckOutcome;
    internal sealed record ExistingEnrollment(DatSubscriptionCheckSnapshot Row) : DatSubscriptionCheckOutcome;
    internal sealed record CheckedFailure(DatSubscriptionCheckSnapshot? Row, DatSubscriptionCheckValues Values) : DatSubscriptionCheckOutcome;
    internal sealed record VerifiedCandidate(DatSubscriptionCheckSnapshot? Row, DatSubscriptionCheckBinding Binding,
        DatSubscriptionVerifiedDocument Preview, bool HasActive, MemoryStream Document, IStagedFile File) : DatSubscriptionCheckOutcome
    {
        internal DatSubscriptionCheckValues Values(int fileId, DateTimeOffset scheduleAt) =>
            DatSubscriptionCheckTransition.Verified(Row?.Values ?? new(), Binding, Preview, fileId, HasActive, scheduleAt);
        public override async ValueTask DisposeAsync()
        {
            try
            { await File.DisposeAsync(); }
            finally { await Document.DisposeAsync(); }
        }
    }
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
    internal async ValueTask DisposePreservingOutcomeAsync(bool committed, bool failed, ILogger logger, string catalogId)
    {
        try
        {
            await DisposeAsync();
        }
        catch (Exception error) when (committed || failed)
        {
            // Structured arguments may still render as raw text in a log sink.
            string loggedCatalogId = catalogId.ReplaceLineEndings(" ");
            if (committed)
                logger.LogWarning(error, "Subscription check for {CatalogId} committed but cleanup failed", loggedCatalogId);
            else
                logger.LogWarning(error, "Subscription check for {CatalogId} failed and cleanup also failed", loggedCatalogId);
        }
    }
    internal static Rejected Reject(Error error) => new([error]);
}

/// <summary>
/// Application orchestration shared by the two owning commands. No transaction/flush/commit capability.
/// Existing long-I/O advisory exclusion is retained intentionally; removing it is a separate protocol change.
/// </summary>
internal sealed class DatSubscriptionCheckWorkflow(IDatSubscriptionCheckReader reader, IDatSubscriptionCheckStore store,
    IDatRepository dats, ISignedDatCatalogClient client, IDatReplacementReview review, IFileStorageService storage, TimeProvider time)
{
    internal async Task<ErrorOr<DatSubscriptionCheckSnapshot?>> FindReplayAsync(string catalogId, int? datId, CancellationToken ct)
    {
        DatFile? requested = null;
        if (datId is int requestedId)
        {
            requested = await dats.GetByIdAsync(requestedId, ct);
            if (requested is null || requested.Lifecycle != DatFileLifecycle.Active)
                return DatEnrollmentErrors.Unavailable();
            var bound = await reader.ReadBySourceAsync(requested.DatSourceId, ct);
            if (bound?.CatalogId == catalogId)
                return bound;
        }
        var rows = await reader.ReadCatalogAsync(catalogId, ct);
        int? trackedSource = null;
        if (requested is not null && rows is [var only] && only.Values.DatSourceId is null)
            trackedSource = (await ActiveAsync(only, ct))?.DatSourceId;
        return DatSubscriptionEnrollmentReplayPolicy.Resolve(rows, requested?.DatSourceId, trackedSource);
    }

    internal async Task<DatSubscriptionCheckOutcome> PrepareAsync(string catalogId, int? rowId, bool scheduled, int? datId, CancellationToken ct)
    {
        if (!await store.TryLockCatalogStagedAsync(catalogId, ct))
            return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.Busy());
        if (rowId is null)
        {
            var replay = await FindReplayAsync(catalogId, datId, ct);
            if (replay.IsError)
                return new DatSubscriptionCheckOutcome.Rejected(replay.Errors.ToArray());
            if (replay.Value is { } existing)
                return new DatSubscriptionCheckOutcome.ExistingEnrollment(existing);
        }
        DatSubscriptionCheckSnapshot? row;
        if (rowId is int existingId)
        {
            row = await reader.ReadAsync(existingId, ct);
            if (row?.CatalogId != catalogId)
                return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.NotFound());
        }
        else
        {
            var matches = await reader.ReadCatalogAsync(catalogId, ct);
            if (matches.Count > 1)
                return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.Ambiguous());
            row = matches.SingleOrDefault();
        }
        if (row?.Values.DatSourceId is int source)
        {
            if (!await store.TryLockSourceStagedAsync(source, ct))
                return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.Busy());
            row = await reader.ReadAsync(row.Id, ct);
            if (row?.CatalogId != catalogId)
                return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.NotFound());
        }
        if (scheduled && !await reader.IsCheckDueAsync(rowId!.Value, time.GetUtcNow(), ct))
            return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.Busy());
        if (row is not null && await reader.HasRunningJobAsync(row.Values.JobId, ct))
            return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.Busy());

        var directory = await client.DiscoverAsync(ct);
        var catalog = directory.IsError ? null : directory.Value.SingleOrDefault(item => item.CatalogId == catalogId);
        if (catalog?.DocumentHash is null)
        {
            if (row is null)
                return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.Unavailable());
            return new DatSubscriptionCheckOutcome.CheckedFailure(row, DatSubscriptionCheckTransition.Failed(row.Values,
                null, DatEnrollmentErrors.CheckFailed().Description, time.GetUtcNow(), time.GetUtcNow()));
        }
        var platform = await reader.ReadPlatformDefinitionAsync(catalog.SystemId, ct);
        if (platform is null || platform.Retired)
            return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.PlatformMissing());
        var active = row is null ? null : await ActiveAsync(row, ct);
        if (row?.Values.DatSourceId is null && active is null && row?.Values.JobId is null)
        {
            if (datId is int requested)
            {
                active = await dats.GetByIdAsync(requested, ct);
                if (active is null || active.Lifecycle != DatFileLifecycle.Active)
                    return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.Unavailable());
            }
            else
            {
                var ids = await reader.ReadActiveDatIdsForNameAsync(platform.Id, catalog.Name, ct);
                if (ids.Count > 1)
                    return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.Ambiguous());
                if (ids.Count == 1)
                    active = await dats.GetByIdAsync(ids[0], ct);
            }
        }
        if (active is not null)
        {
            if (active.PlatformId != platform.Id || active.Name != catalog.Name || !await reader.IsSourceEnabledAsync(active.DatSourceId, ct))
                return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.Unavailable());
            if (!await store.TryLockSourceStagedAsync(active.DatSourceId, ct))
                return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.Busy());
            var bound = await reader.ReadBySourceAsync(active.DatSourceId, ct);
            if (bound is not null)
            {
                if (bound.CatalogId != catalogId || (row is not null && row.Id != bound.Id))
                    return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.Ambiguous());
                row = bound;
                if (await reader.HasRunningJobAsync(row.Values.JobId, ct))
                    return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.Busy());
            }
        }
        else if (row?.Values.DatSourceId is not null)
            return DatSubscriptionCheckOutcome.Reject(DatEnrollmentErrors.Unavailable());
        var binding = new DatSubscriptionCheckBinding(active?.DatSourceId ?? row?.Values.DatSourceId,
            platform.Id, catalog.SystemId, catalog.Name, time.GetUtcNow());
        var fetched = await client.FetchAsync(catalog.CatalogId, catalog.SystemId, catalog.Name, ct);
        if (fetched.IsError)
            return Failure(row, binding, DatEnrollmentErrors.CheckFailed().Description);
        var document = new MemoryStream(fetched.Value.ToArray(), writable: false);
        try
        {
            var preview = active is null ? await review.PreviewInitialAsync(catalog.Name, document, ct)
                : await review.PreviewAsync(active.Id, document, ct);
            if (preview.IsError)
            {
                await document.DisposeAsync();
                return Failure(row, binding, preview.FirstError.Description);
            }
            document.Position = 0;
            var staged = await storage.StageAsync(document, ct);
            var verified = new DatSubscriptionVerifiedDocument(preview.Value.ActiveSha256, preview.Value.CandidateSha256, preview.Value.Unchanged);
            return new DatSubscriptionCheckOutcome.VerifiedCandidate(row, binding, verified, active is not null, document, staged);
        }
        catch { await document.DisposeAsync(); throw; }
    }

    private DatSubscriptionCheckOutcome.CheckedFailure Failure(DatSubscriptionCheckSnapshot? row,
        DatSubscriptionCheckBinding binding, string message) => new(row,
        DatSubscriptionCheckTransition.Failed(row?.Values ?? new(), binding, message, binding.CheckedAt, time.GetUtcNow()));

    private async Task<DatFile?> ActiveAsync(DatSubscriptionCheckSnapshot row, CancellationToken ct)
    {
        if (row.Values.DatSourceId is int source)
            return await dats.GetActiveBySourceIdAsync(source, ct);
        if (row.Values.JobId is null || await reader.HasRunningJobAsync(row.Values.JobId, ct) || row.Values.CandidateFileId is not int fileId)
            return null;
        var ids = await reader.ReadActiveDatIdsForDocumentAsync(fileId, row.Values.PlatformId, ct);
        return ids.Count == 1 ? await dats.GetByIdAsync(ids[0], ct) : null;
    }
}
