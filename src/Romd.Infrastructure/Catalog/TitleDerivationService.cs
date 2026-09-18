using System.Runtime.CompilerServices;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Titles.Matching;
using Romd.Persistence;
using Romd.Persistence.Entities;

namespace Romd.Infrastructure.Catalog;

/// <summary>
///     Catalog-owned derivation: turns provider claims into source entries, canonical titles,
///     and entry-to-title links, one batch at a time. Flushes before yielding a batch's
///     assignments and clears the tracker only at batch boundaries, per the flush protocol in
///     docs/decisions/neutral-source-identity.md. Title matching is also batch-scoped, so memory
///     is bounded by the derivation batch rather than the platform catalog.
/// </summary>
public sealed class TitleDerivationService(
    RomdDbContext context,
    ITitleMatcher titleMatcher) : ITitleDerivationService
{
    public IAsyncEnumerable<ErrorOr<ClaimAssignment>> ReconcileAsync(
        int catalogSourceId,
        IAsyncEnumerable<TitleClaim> claims,
        CancellationToken cancellationToken = default) =>
        DeriveAsync(catalogSourceId, claims, deleteUnseen: true, cancellationToken);

    public IAsyncEnumerable<ErrorOr<ClaimAssignment>> UpsertAsync(
        int catalogSourceId,
        IAsyncEnumerable<TitleClaim> claims,
        CancellationToken cancellationToken = default) =>
        DeriveAsync(catalogSourceId, claims, deleteUnseen: false, cancellationToken);

    private async IAsyncEnumerable<ErrorOr<ClaimAssignment>> DeriveAsync(
        int catalogSourceId,
        IAsyncEnumerable<TitleClaim> claims,
        bool deleteUnseen,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string runId = Guid.NewGuid().ToString("N");
        // Conflict dispositions survive batch boundaries: the run stamp records that a key
        // was processed, not how it went, so conflicted keys are carried here for the life
        // of the invocation. Bounded by the number of distinct conflicted keys — the broken
        // path — not by stream length.
        var conflictedRunKeys = new HashSet<string>();

        var batch = new List<TitleClaim>(TitleMatchingContract.MaxBatchSize);

        await foreach (var claim in claims.WithCancellation(cancellationToken))
        {
            batch.Add(claim);

            if (batch.Count >= TitleMatchingContract.MaxBatchSize)
            {
                foreach (var assignment in await ProcessBatchAsync(
                             catalogSourceId, runId, batch, conflictedRunKeys, cancellationToken))
                {
                    yield return assignment;
                }

                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            foreach (var assignment in await ProcessBatchAsync(
                         catalogSourceId, runId, batch, conflictedRunKeys, cancellationToken))
            {
                yield return assignment;
            }
        }

        // Reconcile deletion: only after the full stream completed, and purely
        // stamp-based — no provider-payload knowledge lives in this service. Providers
        // whose payload rows reference entries (e.g. DAT games, Restrict FK) must retire
        // those payloads before reconciling, or use UpsertAsync and prune provider-side
        // (the DAT version-retention grace policy). Links cascade at the database.
        if (deleteUnseen)
        {
            await context.SourceEntries
                .Where(e => e.CatalogSourceId == catalogSourceId
                            && (e.LastReconcileRunId == null || e.LastReconcileRunId != runId))
                .ExecuteDeleteAsync(cancellationToken);
        }
    }

    private async Task<List<ErrorOr<ClaimAssignment>>> ProcessBatchAsync(
        int catalogSourceId,
        string runId,
        List<TitleClaim> batch,
        HashSet<string> conflictedRunKeys,
        CancellationToken cancellationToken)
    {
        // Canonicalize within the batch: the first claim for each key carries the entry's
        // facts; later duplicates project its result. Canonicalization is stream-level, not
        // batch-level: keys whose entry already carries THIS run's stamp were processed by an
        // earlier batch of this invocation, so their facts are settled and this batch's
        // claims for them are projections too (no re-stamping, no reclassification).
        var canonicalByKey = new Dictionary<string, TitleClaim>();
        var firstIndexByKey = new Dictionary<string, int>();
        for (int i = 0; i < batch.Count; i++)
        {
            var claim = batch[i];
            if (string.IsNullOrWhiteSpace(claim.EntryKey) || canonicalByKey.ContainsKey(claim.EntryKey))
            {
                continue;
            }

            canonicalByKey[claim.EntryKey] = claim;
            firstIndexByKey[claim.EntryKey] = i;
        }

        var keys = canonicalByKey.Keys.ToList();

        var existingEntries = await context.SourceEntries
            .Where(e => e.CatalogSourceId == catalogSourceId && keys.Contains(e.EntryKey))
            .Select(e => new { e.Id, e.EntryKey, e.PlatformId, e.Name, e.LastReconcileRunId })
            .ToListAsync(cancellationToken);

        var entryIdByKey = existingEntries.ToDictionary(e => e.EntryKey, e => e.Id);

        // Settled means processed successfully by an earlier batch of this run; a stamped
        // entry whose key was conflicted is NOT settled — its disposition is the error.
        var settledKeys = existingEntries
            .Where(e => e.LastReconcileRunId == runId && !conflictedRunKeys.Contains(e.EntryKey))
            .Select(e => e.EntryKey)
            .ToHashSet();

        // An established platform never moves, and a claim disagreeing with it is a confused
        // provider: reject the claim rather than link across platforms (the invariant the
        // curation commands already enforce). The entry still counts as seen for reconcile.
        // Keys conflicted in earlier batches stay conflicted: every later duplicate projects
        // the error, regardless of its own facts.
        var conflictedKeys = existingEntries
            .Where(e => !settledKeys.Contains(e.EntryKey)
                        && !conflictedRunKeys.Contains(e.EntryKey)
                        && e.PlatformId is int establishedPlatformId
                        && canonicalByKey[e.EntryKey].PlatformId is int claimedPlatformId
                        && establishedPlatformId != claimedPlatformId)
            .Select(e => e.EntryKey)
            .ToHashSet();

        foreach (string key in keys)
        {
            if (conflictedRunKeys.Contains(key))
            {
                conflictedKeys.Add(key);
            }
        }

        conflictedRunKeys.UnionWith(conflictedKeys);

        var activeEntries = existingEntries
            .Where(e => !settledKeys.Contains(e.EntryKey))
            .ToList();

        // ── Entries: stamp seen, refresh facts (active, non-conflicted only), insert missing ──

        if (activeEntries.Count > 0)
        {
            var seenIds = activeEntries.Select(e => e.Id).ToList();
            await context.SourceEntries
                .Where(e => seenIds.Contains(e.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(e => e.LastReconcileRunId, runId),
                    cancellationToken);
        }

        var mutableEntries = activeEntries
            .Where(e => !conflictedKeys.Contains(e.EntryKey))
            .ToList();

        foreach (var platformGroup in mutableEntries
                     .Where(e => e.PlatformId is null && canonicalByKey[e.EntryKey].PlatformId is not null)
                     .GroupBy(e => canonicalByKey[e.EntryKey].PlatformId!.Value))
        {
            var idsToStamp = platformGroup.Select(e => e.Id).ToList();
            await context.SourceEntries
                .Where(e => idsToStamp.Contains(e.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(e => e.PlatformId, platformGroup.Key),
                    cancellationToken);
        }

        // Display names follow the claim (a provider with a stable key may correct the name;
        // for DAT they coincide, so this is a no-op there).
        foreach (var stale in mutableEntries.Where(e => canonicalByKey[e.EntryKey].Name != e.Name))
        {
            string refreshedName = canonicalByKey[stale.EntryKey].Name;
            await context.SourceEntries
                .Where(e => e.Id == stale.Id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(e => e.Name, refreshedName),
                    cancellationToken);
        }

        var newEntriesByKey = new Dictionary<string, SourceEntryEntity>();
        foreach (string key in keys)
        {
            if (entryIdByKey.ContainsKey(key))
            {
                continue;
            }

            var canonical = canonicalByKey[key];
            newEntriesByKey[key] = new SourceEntryEntity
            {
                CatalogSourceId = catalogSourceId,
                EntryKey = key,
                Name = canonical.Name,
                PlatformId = canonical.PlatformId,
                LastReconcileRunId = runId
            };
        }

        if (newEntriesByKey.Count > 0)
        {
            context.SourceEntries.AddRange(newEntriesByKey.Values);
            await context.SaveChangesAsync(cancellationToken);

            foreach (var (key, entity) in newEntriesByKey)
            {
                entryIdByKey[key] = entity.Id;
            }
        }

        // ── Links ──

        var batchEntryIds = entryIdByKey.Values.ToList();
        var linkedTitleByEntryId = await context.TitleSourceLinks
            .Where(l => batchEntryIds.Contains(l.SourceEntryId))
            .Select(l => new { l.SourceEntryId, l.TitleId })
            .ToDictionaryAsync(l => l.SourceEntryId, l => l.TitleId, cancellationToken);

        // BIOS classification outranks curation: an entry reclassified as BIOS loses its
        // title link — BIOS gets identity, never links (ADR decision). Settled and
        // conflicted keys are exempt: their facts are not this claim's to change.
        var biosLinkedEntryIds = keys
            .Where(key => canonicalByKey[key].IsBios
                          && !settledKeys.Contains(key)
                          && !conflictedKeys.Contains(key))
            .Select(key => entryIdByKey[key])
            .Where(linkedTitleByEntryId.ContainsKey)
            .ToList();

        if (biosLinkedEntryIds.Count > 0)
        {
            await context.TitleSourceLinks
                .Where(l => biosLinkedEntryIds.Contains(l.SourceEntryId))
                .ExecuteDeleteAsync(cancellationToken);

            foreach (int entryId in biosLinkedEntryIds)
            {
                linkedTitleByEntryId.Remove(entryId);
            }
        }

        // Existing curation wins: derive only for unlinked, non-BIOS, routed canonical keys
        // whose facts this run may apply (not settled, not conflicted).
        var derivationCandidates = firstIndexByKey
            .OrderBy(pair => pair.Value)
            .Select(pair => (Key: pair.Key, Claim: canonicalByKey[pair.Key]))
            .Where(pair => pair.Claim is { IsBios: false, PlatformId: not null }
                           && !settledKeys.Contains(pair.Key)
                           && !conflictedKeys.Contains(pair.Key)
                           && !linkedTitleByEntryId.ContainsKey(entryIdByKey[pair.Key]))
            .Select(pair => new
            {
                Key = pair.Key,
                PlatformId = pair.Claim.PlatformId!.Value,
                pair.Claim.Name
            })
            .ToList();

        var derivedByKey = new Dictionary<string, TitleMatch>();
        foreach (var platformGroup in derivationCandidates.GroupBy(candidate => candidate.PlatformId))
        {
            var candidates = platformGroup.ToList();
            var matches = await titleMatcher.MatchOrCreateBatchAsync(
                platformGroup.Key,
                candidates.Select(candidate => candidate.Name).ToList(),
                cancellationToken);

            for (int i = 0; i < candidates.Count; i++)
            {
                derivedByKey[candidates[i].Key] = matches[i];
            }
        }

        var newLinksByEntryId = new Dictionary<int, TitleSourceLinkEntity>();
        foreach (var (key, derived) in derivedByKey)
        {
            int entryId = entryIdByKey[key];
            if (linkedTitleByEntryId.ContainsKey(entryId) || newLinksByEntryId.ContainsKey(entryId))
            {
                continue;
            }

            newLinksByEntryId[entryId] = new TitleSourceLinkEntity
            {
                SourceEntryId = entryId,
                TitleId = derived.TitleId
            };
        }

        if (newLinksByEntryId.Count > 0)
        {
            context.TitleSourceLinks.AddRange(newLinksByEntryId.Values);
            await context.SaveChangesAsync(cancellationToken);

            foreach (var (entryId, link) in newLinksByEntryId)
            {
                linkedTitleByEntryId[entryId] = link.TitleId;
            }
        }

        // Coordinated batch-boundary clear: everything above is flushed, and callers hold no
        // staged rows across a pull (flush protocol, docs/decisions/neutral-source-identity.md).
        context.ChangeTracker.Clear();

        // ── Assignments, one per claim, in claim order; duplicates (in-batch and cross-batch)
        // project the canonical result, with TitleWasCreated true only at the creating
        // claim's position; platform-conflicted claims are answered with a validation error ──

        var assignments = new List<ErrorOr<ClaimAssignment>>(batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            var claim = batch[i];
            if (string.IsNullOrWhiteSpace(claim.EntryKey))
            {
                assignments.Add(Error.Validation(
                    "Derivation.EmptyEntryKey",
                    "Claims must carry a non-empty entry key."));
                continue;
            }

            if (conflictedKeys.Contains(claim.EntryKey))
            {
                assignments.Add(Error.Validation(
                    "Derivation.PlatformConflict",
                    $"Entry '{claim.EntryKey}' is established on another platform; " +
                    "claims cannot move an entry's platform."));
                continue;
            }

            int entryId = entryIdByKey[claim.EntryKey];
            int? titleId = linkedTitleByEntryId.TryGetValue(entryId, out int linkedTitleId)
                ? linkedTitleId
                : null;
            bool created = derivedByKey.TryGetValue(claim.EntryKey, out var derived)
                           && derived.TitleWasCreated
                           && firstIndexByKey[claim.EntryKey] == i;

            assignments.Add(new ClaimAssignment(claim.EntryKey, entryId, titleId, created));
        }

        return assignments;
    }
}
