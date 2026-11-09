using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Security;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Compressed;

/// <summary>Application retention policy and catalog effects around the worker's fenced fact-store scope.</summary>
public sealed class RomCdImportPublication(RomCdArchivePublisher publisher, IRomCdImportStore store, RomCdStorageRecipes recipes,
    ITrackedTitleReader tracked, ITrackedTitleRepository tracking, ILibraryRepository libraries,
    IRomPayloadAssertionImpactReader impact, ITitlePayloadAvailabilityProjection payload, IAdminEventOutbox outbox,
    IMediaReconciliationRequests requests, IAuditContext audit, TimeProvider clock) : IRomCdImportPublication
{
    public async Task<ErrorOr<RomIngestResult.CdArchive>> PublishAsync(RomCdImportPlan plan, RomCdIngestIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.TrackedOnly && !await tracked.HasTrackedAsync([plan.Binding.TitleId], ct))
            return CatalogErrors.UntrackedRom();
        var gate = new Gate(this, plan, intent);
        try
        {
            await publisher.PublishAsync(plan.Measured, plan.Source.Artifact,
                new Dictionary<string, ITempFile>(StringComparer.Ordinal) { [plan.Binding.Cue.Path] = plan.Cue.Artifact }, gate, ct);
            return gate.Receipt ?? throw new InvalidDataException("CD publication committed without its complete receipt.");
        }
        catch (Refused error) { return error.Errors; }
    }

    private async Task<(IRomCdImportScope Scope, RomCdArchivePublicationAuthority Authority)> PrepareAsync(
        RomCdImportPlan plan, RomCdIngestIntent intent, CancellationToken ct)
    {
        if (intent.TrackedOnly && !await tracked.HasTrackedAsync([plan.Binding.TitleId], ct))
            throw new Refused([CatalogErrors.UntrackedRom()]);
        var recipe = recipes.ForPlatform(plan.Binding.PlatformKey);
        if (recipe is null || !recipes.PlatformsFor(plan.Source.Inspection.Boot).Contains(plan.Binding.PlatformKey, StringComparer.Ordinal))
            throw new Refused([RomCdImportErrors.UnsupportedLayout]);
        var locked = await store.LockAsync(plan, recipe.PlatformKeys, audit.ActorId, clock.GetUtcNow(), ct);
        if (locked.IsError)
            throw new Refused(locked.Errors);
        var scope = locked.Value;
        if (scope.RetainedArchives.Any(graph => graph.ArtifactSha256 == plan.Source.Artifact.FileSha256 &&
            (graph.ArtifactSize != plan.Source.Artifact.FileSize || graph.Container.Fingerprint != plan.Measured.Container.Fingerprint)))
            throw new InvalidDataException("An immutable CHD artifact cannot acquire contradictory observed header or metadata facts.");
        var retained = scope.RetainedArchives.Where(graph => plan.Measured.MatchesLayout(graph) &&
            graph.Container is RomContainerFacts.Chd observed && plan.Measured.Container is RomContainerFacts.Chd incoming &&
            observed.LogicalBytes == incoming.LogicalBytes && observed.UnitBytes == incoming.UnitBytes &&
            observed.StoredRawSha1 == incoming.StoredRawSha1 && observed.StoredOverallSha1 == incoming.StoredOverallSha1)
            .OrderByDescending(graph => recipe.Matches(graph))
            .ThenByDescending(graph => graph.Id == scope.CurrentArchive?.Id)
            .ThenByDescending(graph => graph.ArtifactSha256 == plan.Source.Artifact.FileSha256)
            .ThenBy(graph => graph.Id).FirstOrDefault();
        if (retained is not null && (recipe.Matches(retained) || !recipe.Matches(plan.Measured)))
            return (scope, plan.Measured.MatchesRetained(retained)
                ? new RomCdArchivePublicationAuthority.Publish(scope.BinRomIds, retained)
                : new RomCdArchivePublicationAuthority.Keep(retained));
        return (scope, new RomCdArchivePublicationAuthority.Publish(scope.BinRomIds, retained: null));
    }

    private async Task<(RomIngestResult.CdArchive Receipt, JobItem? Outcome)> CompleteAsync(RomCdImportPlan plan,
        RomCdIngestIntent intent, IRomCdImportScope scope, RomRepresentationFacts target, CancellationToken ct)
    {
        var completed = await scope.CompleteAsync(target, ct);
        if (completed.IsError)
            throw new Refused(completed.Errors);
        var retained = completed.Value;
        var sourceIds = new HashSet<int>();
        IEnumerable<Sha1> identities = plan.Binding.Bins.Select(bin => bin.Track.Identity.Sha1);
        if (plan.OriginalCue is not null)
            identities = identities.Append(plan.OriginalCue.Verification.Sha1);
        foreach (var hash in identities.Distinct().OrderBy(hash => hash.ToString(), StringComparer.Ordinal))
            sourceIds.UnionWith(await impact.ReadSourceEntryIdsBySha1Async(hash, ct));
        if (sourceIds.Count > 0)
            await payload.RefreshSourceEntryPayloadAssertionsAsync(sourceIds, ct);
        if (retained.LinkedRequirements > 0)
            await libraries.MarkUpdatesAvailableByPlatformAsync(plan.Binding.PlatformId, ct);
        bool archiveOnly = intent.ArchiveOnly || intent.TrackedOnly;
        if (!archiveOnly)
            await tracking.TrackByTitleIdsAsync([plan.Binding.TitleId], ct);
        var demand = await requests.StageSubjectAsync(new(Guid.NewGuid(), new MediaReconciliationSubject.CatalogCdLayout(plan.Binding.Cue.CatalogFileId),
            false, MediaReconciliationCause.Import, audit.IsSystem ? null : audit.ActorId, null), ct);
        if (demand.IsError)
            throw new Refused(demand.Errors);
        await outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, ct);
        await outbox.EnqueueAsync(AdminRealtimeEventTypes.CoverageStatsChanged, ct);
        await outbox.EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, ct);
        var receipt = new RomIngestResult.CdArchive(retained.Archive, retained.OriginalCue)
        { IsNew = scope.HasNewLogicalOwners, MatchedTitleIds = [plan.Binding.TitleId], PlatformId = plan.Binding.PlatformId };
        var outcome = intent.UploadItem is { } item ? JobItem.ForRom(item.JobId, item.RelativePath, plan.Source.Artifact.FileSize,
            receipt.IsNew ? RomIngestOutcome.Ingested : RomIngestOutcome.Deduplicated, romFileId: null, [plan.Binding.TitleId],
            plan.Binding.PlatformId, null, createdAt: clock.GetUtcNow(), archiveOnly) : null;
        return (receipt, outcome);
    }

    private sealed class Gate(RomCdImportPublication owner, RomCdImportPlan plan, RomCdIngestIntent intent) : IRomCdArchivePublicationGate
    {
        private IRomCdImportScope? _scope;
        public RomIngestResult.CdArchive? Receipt { get; private set; }
        public async Task<RomCdArchivePublicationAuthority> PrepareAsync(RomMeasuredRepresentation representation, CancellationToken ct)
        {
            if (!ReferenceEquals(representation, plan.Measured) || _scope is not null)
                throw new InvalidOperationException("CD authority is single-use for the exact prepared measurement.");
            var prepared = await owner.PrepareAsync(plan, intent, ct);
            _scope = prepared.Scope;
            return prepared.Authority;
        }
        public async Task<JobItem?> CompleteAsync(RomRepresentationFacts retained, CancellationToken ct)
        {
            if (_scope is null || Receipt is not null)
                throw new InvalidOperationException("CD completion requires its single prepared transaction scope.");
            var completed = await owner.CompleteAsync(plan, intent, _scope, retained, ct);
            Receipt = completed.Receipt;
            return completed.Outcome;
        }
    }
    private sealed class Refused(IReadOnlyList<Error> errors) : Exception
    {
        public List<Error> Errors { get; } = errors.ToList();
    }
}
