using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;
namespace Romd.Persistence.MetadataProviders;

public sealed class ProviderIdentityStore(RomdDbContext db, ITitleEnrichmentEvidenceReader evidence, TimeProvider clock) : IProviderIdentityStore
{
    public Task<string?> ReadTrustedAsync(int titleId, string providerId, CancellationToken ct) =>
        db.TitleExternalIds.Where(link => link.TitleId == titleId && link.Provider == providerId
            && !db.Set<TitleProviderMatchStateEntity>().Any(state => state.TitleId == titleId
                && state.ProviderId == providerId && state.SuppressAutomaticMatch)
            && ProviderIdentityDatabaseFunctions.IsTrusted(titleId, providerId, ProviderIdentityPolicy.Version))
            .Select(link => link.ExternalId).SingleOrDefaultAsync(ct);

    public async Task<ProviderIdentitySnapshot?> ReadAsync(int titleId, CancellationToken ct)
    {
        // The fingerprint must describe the exact facts returned to discovery, even if
        // catalog evidence is reassigned or paused during this multi-query read.
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Provider identity snapshots require their own consistent read transaction.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var snapshot = await ReadSnapshotAsync(titleId, ct);
        await transaction.CommitAsync(ct);
        return snapshot;
    }

    private async Task<ProviderIdentitySnapshot?> ReadSnapshotAsync(int titleId, CancellationToken ct)
    {
        var title = await db.Titles.SingleOrDefaultAsync(x => x.Id == titleId, ct);
        if (title is null)
            return null;
        var platform = await db.Platforms.SingleAsync(x => x.Id == title.PlatformId, ct);
        var local = await evidence.ReadAsync(titleId, ct);
        var facts = new IdentityFacts(title.Name, local.Select(x => x.Name).Distinct().Order(StringComparer.Ordinal).ToArray(),
            local.Select(x => int.TryParse(x.Year, out var y) && y is >= 1900 and <= 2200 ? (int?)y : null).OfType<int>().Distinct().Order().ToArray(),
            new[] { platform.Name, platform.ShortName }, "Local title name and system; names and years from catalog release evidence. Provider-enriched dates are excluded.");
        var links = await db.TitleExternalIds.Where(x => x.TitleId == titleId).ToListAsync(ct);
        var states = await db.Set<TitleProviderMatchStateEntity>().Where(x => x.TitleId == titleId).ToListAsync(ct);
        // Discovery and workbench queries use the same current-input and recursive-trust
        // predicates. Source pause/reassignment invalidates evidence without refresh jobs.
        var fingerprint = await db.Titles.Where(x => x.Id == titleId)
            .Select(x => ProviderIdentityDatabaseFunctions.Fingerprint(x.Id, ProviderIdentityPolicy.Version)).SingleAsync(ct);
        var trust = await db.TitleExternalIds.Where(x => x.TitleId == titleId)
            .Select(x => new { x.Provider, Trusted = ProviderIdentityDatabaseFunctions.IsTrusted(x.TitleId, x.Provider, ProviderIdentityPolicy.Version) })
            .ToDictionaryAsync(x => x.Provider, x => x.Trusted, ct);
        var rows = links.Select(x => x.Provider).Concat(states.Select(x => x.ProviderId)).Distinct().Select(provider =>
        {
            var link = links.SingleOrDefault(x => x.Provider == provider);
            var state = states.SingleOrDefault(x => x.ProviderId == provider);
            var trusted = trust.GetValueOrDefault(provider);
            return new ProviderIdentityLink(provider, link?.ExternalId, link?.IsConfirmed == true, state?.SuppressAutomaticMatch == true,
                trusted, state?.Revision ?? Guid.Empty, state?.InputFingerprint, state?.LastAttemptAt,
                state?.DecisionJson is { } decision ? JsonSerializer.Deserialize<ProviderIdentityDecisionDto>(decision) : null);
        }).ToArray();
        return new(titleId, title.Revision, fingerprint, facts, rows);
    }
    public async Task<IStagedProviderIdentityDecision> StageDecisionAsync(ProviderIdentitySnapshot snapshot, string providerId, Guid expectedRevision,
        ProviderIdentityDecisionDto decision, ProviderGameDto? accepted, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction;
        if (transaction is null || transaction.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Provider identity decisions require the caller's ReadCommitted transaction.");
        if (JobExecutionFenceScope.Current is { } fence
            && (db.LockedJobExecution is not { } locked || locked.TransactionId != transaction.TransactionId
                || locked.JobId != fence.JobId || locked.Token != fence.Token))
            throw new JobExecutionOwnershipLostException();
        var previouslyTracked = db.ChangeTracker.Entries().Select(entry => entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        try
        {
            if (await db.Titles.Where(x => x.Id == snapshot.TitleId && x.Revision == snapshot.TitleRevision)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, x => x.Revision), ct) != 1)
                return Receipt(false);
            var fingerprint = await db.Titles.Where(x => x.Id == snapshot.TitleId)
                .Select(x => ProviderIdentityDatabaseFunctions.Fingerprint(x.Id, ProviderIdentityPolicy.Version)).SingleAsync(ct);
            if (fingerprint != snapshot.Fingerprint)
                return Receipt(false);
            var currentLinks = await db.TitleExternalIds.Where(x => x.TitleId == snapshot.TitleId)
                .Select(x => new
                {
                    ProviderId = x.Provider,
                    GameId = x.ExternalId,
                    Revision = db.Set<TitleProviderMatchStateEntity>()
                        .Where(s => s.TitleId == x.TitleId && s.ProviderId == x.Provider)
                        .Select(s => (Guid?)s.Revision).FirstOrDefault() ?? Guid.Empty,
                    Trusted = ProviderIdentityDatabaseFunctions.IsTrusted(x.TitleId, x.Provider, ProviderIdentityPolicy.Version)
                }).ToListAsync(ct);
            // Bridge source associations must still be the associations used for the decision.
            if (snapshot.Links.Any(x => x.Trusted && !currentLinks.Any(y => y.ProviderId == x.ProviderId && y.GameId == x.GameId && y.Revision == x.Revision && y.Trusted)))
                return Receipt(false);
            var link = await db.TitleExternalIds.AsTracking().SingleOrDefaultAsync(x => x.TitleId == snapshot.TitleId && x.Provider == providerId, ct);
            var state = await db.Set<TitleProviderMatchStateEntity>().AsTracking().SingleOrDefaultAsync(x => x.TitleId == snapshot.TitleId && x.ProviderId == providerId, ct);
            if (link?.IsConfirmed == true || state?.SuppressAutomaticMatch == true || (state?.Revision ?? Guid.Empty) != expectedRevision)
                return Receipt(false);
            if (state is null)
                db.Add(state = new TitleProviderMatchStateEntity { TitleId = snapshot.TitleId, ProviderId = providerId });
            state.DecisionJson = JsonSerializer.Serialize(decision);
            state.InputFingerprint = snapshot.Fingerprint;
            state.Evidence = decision.Explanation;
            state.MatcherVersion = ProviderIdentityPolicy.Version;
            state.LastAttemptAt = clock.GetUtcNow();
            state.AutomaticallyTrusted = accepted is not null;
            state.Revision = Guid.NewGuid();
            var candidate = accepted ?? decision.Candidates.FirstOrDefault()?.Game;
            state.GameJson = candidate is null ? null : JsonSerializer.Serialize(candidate);
            // Existing unconfirmed identities are audited, never silently replaced with another game.
            if (accepted is not null && link is not null && link.ExternalId != accepted.Id)
            {
                state.AutomaticallyTrusted = false;
                state.Evidence = "Existing automatic association conflicts with current evidence. Review required.";
                state.DecisionJson = JsonSerializer.Serialize(decision with { State = "NeedsReview", Explanation = state.Evidence });
            }
            else if (accepted is not null && link is null)
                db.Add(TitleExternalIdEntity.FromDomain(TitleExternalId.CreateNew(snapshot.TitleId, providerId, accepted.Id, 1, clock.GetUtcNow())));
            return Receipt(true);
        }
        catch (Exception primaryFailure)
        {
            // No receipt escaped to the owner: release only identity rows acquired by this attempt.
            try
            { using var rejected = Receipt(false); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Provider identity staging and tracking cleanup failed.", primaryFailure, cleanupFailure);
            }
            throw;
        }

        IStagedProviderIdentityDecision Receipt(bool applied) => new StagedDecision(db, applied,
            db.ChangeTracker.Entries().Where(entry => !previouslyTracked.Contains(entry.Entity)
                && entry.Entity is TitleExternalIdEntity or TitleProviderMatchStateEntity)
                .Select(entry => entry.Entity).ToArray());
    }

    private sealed class StagedDecision(RomdDbContext context, bool applied, object[] owned) : IStagedProviderIdentityDecision
    {
        private bool _disposed;
        public bool Applied => applied;
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            // The application retains this receipt until its transaction has settled. Never
            // detach staged Added/Modified rows while the owner's flush is still ahead of us.
            foreach (var entity in owned)
                context.Entry(entity).State = EntityState.Detached;
        }
    }
}
