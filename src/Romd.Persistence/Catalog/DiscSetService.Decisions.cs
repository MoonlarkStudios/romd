using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Catalog;

public sealed partial class DiscSetService
{
    private DiscSetEntity? _decisionRow;
    private DiscSetSnapshot? _decisionRead;
    private DiscSetDecisionTarget? _decisionTarget;
    private Guid _decisionTransaction;

    public Task LockTopologyAsync(CancellationToken ct)
    {
        RequireDecisionTransaction();
        return CatalogTopologyFence.AcquireAsync(db, ct);
    }

    public async Task<DiscSetDecisionTarget?> ReadAsync(int id, CancellationToken ct)
    {
        _decisionTransaction = RequireDecisionTransaction();
        _decisionRow = await db.DiscSets.AsTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        _decisionRead = null;
        _decisionTarget = _decisionRow is null ? null : new(_decisionRow.Id, _decisionRow.PlatformId, _decisionRow.Revision);
        return _decisionTarget;
    }

    public DiscSetSnapshot ReadFacts(DiscSetDecisionTarget original)
    {
        if (RequireDecisionTransaction() != _decisionTransaction || !ReferenceEquals(_decisionTarget, original)
            || _decisionRow is null || db.Entry(_decisionRow).State == EntityState.Detached)
            throw new InvalidOperationException("Read decision facts only from their original protected target.");
        return _decisionRead = Snapshot(_decisionRow, null);
    }

    public async Task<DiscSetClaims> ReadClaimsAsync(int platformId, int sourceId, IReadOnlyCollection<string> entryKeys, CancellationToken ct)
    {
        RequireDecisionTransaction();
        var keys = entryKeys.Distinct(StringComparer.Ordinal).ToArray();
        return new(await ClaimsQuery(db.EffectiveSourceEntries().Where(entry => entry.PlatformId == platformId
            && entry.CatalogSourceId == sourceId && keys.Contains(entry.EntryKey))).ToListAsync(ct));
    }

    public Task StageDecisionAsync(DiscSetSnapshot original, DiscSetDecisionChange change, string? reason, Guid actor,
        DateTimeOffset decidedAt, Guid revision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (RequireDecisionTransaction() != _decisionTransaction || !ReferenceEquals(_decisionRead, original)
            || _decisionRow is null || db.Entry(_decisionRow).State == EntityState.Detached || _decisionRow.Revision != original.Revision)
            throw new InvalidOperationException("Stage a disc-set decision only from its original protected read.");
        if (change.ReplaceCorrection)
            _decisionRow.CorrectionJson = change.Correction is null ? null : System.Text.Json.JsonSerializer.Serialize(change.Correction);
        _decisionRow.Decision = change.Decision;
        _decisionRow.TitleIds = change.TitleIds.ToArray();
        _decisionRow.DecidedBy = actor;
        _decisionRow.DecidedAt = decidedAt;
        _decisionRow.Reason = reason;
        _decisionRow.Revision = revision;
        return Task.CompletedTask;
    }

    public async Task InvalidateAffectedLibrariesAsync(int id, Guid publicationRevision, DateTimeOffset now, CancellationToken ct)
    {
        RequireDecisionTransaction();
        // Preserve both draft selections and already-published sets in the same decision commit.
        var configurations = await db.Libraries.Select(library => new { library.Id, library.ConfigurationJson }).ToListAsync(ct);
        var configured = configurations.Where(library => LibraryEntity.DeserializeConfig(library.ConfigurationJson).SelectedDiscSetIds.Contains(id))
            .Select(library => library.Id).ToArray();
        await db.Libraries.Where(library => configured.Contains(library.Id)
                || db.MaterializedLibraryDiscSets.Any(group => group.LibraryId == library.Id && group.DiscSetId == id))
            .ExecuteUpdateAsync(update => update.SetProperty(library => library.NeedsMaterialization, true)
                .SetProperty(library => library.MaterializationRevision, publicationRevision).SetProperty(library => library.UpdatedAt, now), ct);
    }

    private Guid RequireDecisionTransaction() => db.Database.CurrentTransaction?.TransactionId
        ?? throw new InvalidOperationException("Disc-set decisions require the application's transaction.");

    private static DiscSetSnapshot Snapshot(DiscSetEntity row, string? systemKey) => new(row.Id, row.PlatformId, row.Revision,
        new(row.CatalogSourceId, row.Decision, Decode(row.ProposalJson), row.CorrectionJson is null ? null : Decode(row.CorrectionJson)),
        row.DecidedBy, row.DecidedAt, row.Reason, systemKey);
}
