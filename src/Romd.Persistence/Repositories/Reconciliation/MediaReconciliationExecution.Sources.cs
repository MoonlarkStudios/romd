using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Source.Rom;
using Romd.Persistence.MediaFacts;

namespace Romd.Persistence.Repositories.Reconciliation;

public sealed partial class MediaReconciliationExecution
{
    private async Task<MediaReconciliationSources?> RefreshSourcesAsync(MediaReconciliationSubject subject, MediaReconciliationSources source, CancellationToken ct)
    {
        if (source.Layout is not null || source.ReferenceFingerprint is not null)
            await CatalogTopologyFence.AcquireAsync(_db, ct);
        if (source.Layout is MediaReconciliationLayout.Original layout)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT 1 FROM romd."OriginalDiscLayouts" WHERE "Id"={layout.LayoutId} FOR NO KEY UPDATE;
                SELECT 1 FROM romd."CatalogReleases" WHERE "Id"={layout.ReleaseId} FOR NO KEY UPDATE;
                """, ct);
            if (subject is not MediaReconciliationSubject.OriginalLayout selected || selected.Id != layout.LayoutId ||
                !await _db.OriginalDiscLayouts.AnyAsync(row => row.Id == layout.LayoutId && row.Revision == layout.Revision && row.Issue == null &&
                    (row.State == "pending" || row.State == "accepted" && row.ReleaseId == layout.ReleaseId && row.Fingerprint == layout.CatalogFingerprint), ct) ||
                !await _db.CatalogReleases.AnyAsync(row => row.Id == layout.ReleaseId && row.PlatformId == layout.PlatformId &&
                    row.Fingerprint == layout.CatalogFingerprint && !row.HasTitleConflict, ct))
                return null;
        }
        if (source.Layout is MediaReconciliationLayout.Catalog catalog)
        {
            if (!subject.Equals(catalog.Subject))
                return null;
            await CatalogCdLayoutRead.LockScopeAsync(_db, catalog.Subject.Id, ct);
            if (!CatalogCdLayoutRead.Matches(await CatalogCdLayoutRead.ReadAsync(_db, catalog.Subject.Id, ct), catalog))
                return null;
        }
        foreach (var artifact in source.Inputs.OfType<MediaReconciliationInput.Artifact>().Where(input => input.CatalogWitness is not null))
        {
            var witness = artifact.CatalogWitness!;
            await CatalogCdLayoutRead.LockScopeAsync(_db, witness.Requirement.CatalogFileId, ct);
            var binding = await CatalogCdLayoutRead.ReadAsync(_db, witness.Requirement.CatalogFileId, ct);
            if (!CatalogCdLayoutRead.Matches(binding, witness) || binding!.RepresentationId != artifact.Graph.Id)
                return null;
        }
        var graphs = source.Inputs.Select(input => input switch
        {
            MediaReconciliationInput.Logical logical => logical.Graph,
            MediaReconciliationInput.Artifact artifact => artifact.Graph,
            _ => null
        }).Where(graph => graph is not null).Cast<RomRepresentationFacts>().DistinctBy(graph => graph.Id).OrderBy(graph => graph.Id).ToArray();
        foreach (int id in graphs.SelectMany(graph => graph.Outputs).Where(output => output.RomId is not null).Select(output => output.RomId!.Value).Distinct().Order())
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM romd.\"Roms\" WHERE \"Id\"={id} FOR NO KEY UPDATE", ct);
        foreach (int id in graphs.Select(graph => graph.Id))
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(9415,{id}); SELECT 1 FROM romd.\"Representations\" WHERE \"Id\"={id} FOR NO KEY UPDATE", ct);
        int[] files = graphs.Select(graph => graph.FileId).Concat(graphs.SelectMany(graph => graph.Descriptors).Select(descriptor => descriptor.FileId))
            .Concat(source.Inputs.OfType<MediaReconciliationInput.Stored>().Select(input => input.FileId)).Distinct().Order().ToArray();
        foreach (int file in files)
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM romd.\"Files\" WHERE \"Id\"={file} FOR NO KEY UPDATE", ct);
        var current = await MediaFactLockSet.ReadAsync(_db, graphs.Select(graph => graph.Id).ToArray(), ct);
        var refreshed = new List<MediaReconciliationInput>();
        foreach (var input in source.Inputs)
        {
            if (input is MediaReconciliationInput.Stored stored)
            {
                if (!await _db.Files.AnyAsync(row => row.Id == stored.FileId && row.Sha256 == stored.Identity.Sha256 && row.Size == stored.Identity.Size, ct))
                    return null;
                if (stored.Role == MediaReconciliationSourceRole.Sidecar)
                {
                    if (source.Layout is not MediaReconciliationLayout.Original original)
                        return null;
                    var owners = graphs.Where(graph => graph.Manifest is RomReconstructionManifest.CdTracks cd && cd.Members.Any(member =>
                        member is RomCdStoredMember { Role: RomCdStoredRole.Sidecar } && member.Path == stored.Path && member.Identity == stored.Identity) &&
                        graph.Descriptors.Any(descriptor => descriptor.FileId == stored.FileId)).Select(graph => graph.Id).ToArray();
                    if (!await _db.CdArchiveOwners.AnyAsync(owner => owner.LayoutId == original.LayoutId && owners.Contains(owner.RepresentationId), ct))
                        return null;
                }
                refreshed.Add(stored);
            }
            else
            {
                int id = input is MediaReconciliationInput.Logical logical ? logical.Graph.Id : ((MediaReconciliationInput.Artifact)input).Graph.Id;
                if (!current.TryGetValue(id, out var graph))
                    return null;
                if (input is MediaReconciliationInput.Logical member)
                {
                    var output = graph.Outputs.SingleOrDefault(output => output.Id == member.Output.Id);
                    if (output?.RomId != member.Output.RomId || !graph.Verifications.Any(proof => proof.Id == member.VerificationId))
                        return null;
                    refreshed.Add(new MediaReconciliationInput.Logical(member.Role, member.Path!, graph, output!.Id, member.VerificationId));
                }
                else
                {
                    var artifact = (MediaReconciliationInput.Artifact)input;
                    if (artifact.VerificationId is { } proof && !graph.Verifications.Any(row => row.Id == proof))
                        return null;
                    refreshed.Add(new MediaReconciliationInput.Artifact(graph, artifact.VerificationId, artifact.CatalogWitness));
                }
            }
        }
        if (source.Layout is MediaReconciliationLayout.Catalog capturedCatalog)
        {
            var binding = await CatalogCdLayoutRead.ReadAsync(_db, capturedCatalog.Subject.Id, ct);
            if (!CatalogCdLayoutRead.Matches(binding, capturedCatalog))
                return null;
            foreach (var cue in source.Inputs.OfType<MediaReconciliationInput.Stored>())
            {
                if (cue.Role != MediaReconciliationSourceRole.Cue || !current.TryGetValue(binding!.RepresentationId, out var graph) ||
                    graph.Manifest is not RomReconstructionManifest.CdTracks cd || cd.Members[binding.MemberOrdinal] is not RomCdStoredMember member ||
                    member.Path != cue.Path || member.Identity != cue.Identity || !graph.Descriptors.Any(row => row.Ordinal == binding.MemberOrdinal && row.FileId == cue.FileId))
                    return null;
            }
        }
        IReadOnlyList<Guid> inspections = source.LogicalInspectionIds;
        if (subject is MediaReconciliationSubject.LogicalRom logicalSubject)
        {
            var content = (await new Romd.Persistence.Repositories.RomContentCatalog(_db, _readOptions).ReadLockedPublicationAsync([logicalSubject.Id], ct)).GetValueOrDefault(logicalSubject.Id);
            if (content is null)
                return null;
            inspections = content.Inspections.Select(observation => observation.Id).ToArray();
            if (source.ReferenceFingerprint is { } reference && MediaReconciliationReferenceFingerprint.Compute(content.References) != reference)
                return null;
        }
        return new(refreshed, source.Layout, inspections, source.ReferenceFingerprint);
    }
}
