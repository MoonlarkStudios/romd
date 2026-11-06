using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Source.Rom;
using Romd.Persistence.MediaFacts;

namespace Romd.Persistence.Repositories.Reconciliation;

public sealed partial class MediaReconciliationExecution
{
    private async Task<ErrorOr<Success>> AlignOwnershipAsync(MediaReconciliationCapture capture, MediaReconciliationFacts current, RomRepresentationFacts target, Guid verification, CancellationToken ct)
    {
        if (current is MediaReconciliationFacts.Logical logical)
        {
            int output = target.Outputs.Single(row => row.RomId == logical.Content.RomId && row.Selector is RomOutputSelector.Whole).Id;
            await _db.RomFiles.Where(row => row.Id == logical.Content.RomId).ExecuteUpdateAsync(set => set.SetProperty(row => row.PreferredOutputId, output), ct);
            var redundant = logical.Content.Artifacts.Where(artifact => artifact.Facts.Manifest is RomReconstructionManifest.WholeFile && artifact.RepresentationId != target.Id).ToArray();
            await ReleaseWholeOutputsAsync(logical.Content.RomId, redundant.Select(row => (row.OutputId, row.FileId)).ToArray(), target.FileId, ct);
            return Result.Success;
        }
        var layout = (MediaReconciliationFacts.Layout)current;
        var context = capture.Sources.Layout!;
        int? previous = layout.CurrentArchiveId;
        if (context is MediaReconciliationLayout.Original original)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."CdArchiveOwners" ("LayoutId","RepresentationId","VerificationId","PublicationId","LayoutRevision","PlatformId","ReleaseId","CatalogFingerprint","EntryPoint","PublishedAtUtcTicks")
                VALUES ({original.LayoutId},{target.Id},{verification},{capture.JobId},{context.Revision},{context.PlatformId},{context.ReleaseId},{context.CatalogFingerprint},{context.EntryPoint},{_clock.GetUtcNow().UtcTicks})
                ON CONFLICT ("LayoutId") DO UPDATE SET "RepresentationId"=EXCLUDED."RepresentationId","VerificationId"=EXCLUDED."VerificationId","PublicationId"=EXCLUDED."PublicationId",
                    "LayoutRevision"=EXCLUDED."LayoutRevision","PlatformId"=EXCLUDED."PlatformId","ReleaseId"=EXCLUDED."ReleaseId","CatalogFingerprint"=EXCLUDED."CatalogFingerprint",
                    "EntryPoint"=EXCLUDED."EntryPoint","PublishedAtUtcTicks"=EXCLUDED."PublishedAtUtcTicks"
                """, ct);
        }
        else if (context is MediaReconciliationLayout.Catalog catalog)
        {
            if (previous != target.Id)
            {
                var store = new CatalogCdLayoutStore(_db);
                if (!await store.WithdrawAsync(catalog.BindingId, catalog.Revision, ct))
                    return MediaReconciliationErrors.Changed;
                int ordinal = Enumerable.Range(0, ((RomReconstructionManifest.CdTracks)target.Manifest).Members.Count)
                    .Single(index => ((RomReconstructionManifest.CdTracks)target.Manifest).Members[index] is RomCdStoredMember { Role: RomCdStoredRole.Cue });
                var bound = await store.PublishAsync(new(Guid.NewGuid(), catalog.Requirement, target.Id, ordinal, catalog.GeneratorProfile, _clock.GetUtcNow()), ct);
                if (bound.IsError)
                {
                    if (bound.Errors.Any(error => error.Code == CatalogCdLayoutErrors.SourceChanged.Code))
                        return MediaReconciliationErrors.Changed;
                    return bound.Errors;
                }
            }
        }
        else
            throw new InvalidDataException("Ownership requires a closed original or catalog layout context.");
        var sources = capture.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().ToArray();
        var wholeGraphs = capture.Sources.Inputs.Select(input => input switch
        {
            MediaReconciliationInput.Logical logical => logical.Graph,
            MediaReconciliationInput.Artifact artifact => artifact.Graph,
            _ => null
        }).Where(graph => graph?.Manifest is RomReconstructionManifest.WholeFile).Cast<RomRepresentationFacts>().DistinctBy(graph => graph.Id).ToArray();
        if (context is MediaReconciliationLayout.Original originalBacking && previous is { } priorOwner && priorOwner != target.Id)
        {
            int oldFile = layout.Archives.Single(graph => graph.Id == priorOwner).FileId;
            int[] members = sources.Select(source => source.Output.RomId!.Value).Distinct().ToArray();
            // Transfer this layout's backing before removing its old archive ownership. Other layouts
            // retain their exact foreign archive dependency and its complete measured interpretation.
            await _db.OriginalDiscLayoutFiles.Where(row => row.OriginalDiscLayoutId == originalBacking.LayoutId && row.FileId == oldFile && members.Contains(row.RomId))
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.FileId, target.FileId), ct);
        }
        foreach (var members in sources.GroupBy(source => source.Output.RomId!.Value).OrderBy(group => group.Key))
        {
            int rom = members.Key;
            var output = target.Outputs.Where(row => row.RomId == rom).OrderBy(row => ((RomOutputSelector.Member)row.Selector).Ordinal).First();
            int? preferred = await _db.RomFiles.Where(row => row.Id == rom).Select(row => row.PreferredOutputId).SingleAsync(ct);
            int? preferredGraph = await _db.RepresentationOutputs.Where(row => row.Id == preferred).Select(row => (int?)row.RepresentationId).SingleOrDefaultAsync(ct);
            bool wholePreferred = preferredGraph is { } selected && await _db.RepresentationOutputs.AnyAsync(row => row.Id == preferred && row.MemberOrdinal == null, ct);
            // A valid foreign CD preference remains stable when another layout using the same BIN is published.
            if (preferredGraph is null || wholePreferred || preferredGraph == previous)
                await _db.RomFiles.Where(row => row.Id == rom).ExecuteUpdateAsync(set => set.SetProperty(row => row.PreferredOutputId, output.Id), ct);
            var originals = wholeGraphs.Where(graph => graph.Id != target.Id).SelectMany(graph => graph.Outputs.Where(output => output.RomId == rom)
                .Select(output => (output.Id, graph.FileId))).Distinct().ToArray();
            await ReleaseWholeOutputsAsync(rom, originals, target.FileId, ct);
        }
        if (previous is { } old && old != target.Id && !await _db.CdArchiveOwners.AnyAsync(owner => owner.RepresentationId == old, ct) &&
            !await CatalogCdLayoutRead.HasOwnerAsync(_db, old, ct))
        {
            var prior = layout.Archives.Single(graph => graph.Id == old);
            foreach (var output in prior.Outputs.Where(output => output.RomId is not null))
            {
                int rom = output.RomId!.Value;
                if (!await _db.OriginalDiscLayoutFiles.AnyAsync(file => file.RomId == rom && file.FileId == prior.FileId, ct) &&
                    !await _db.RomFiles.AnyAsync(row => row.Id == rom && row.PreferredOutputId == output.Id, ct))
                    await _db.RepresentationOutputs.Where(row => row.Id == output.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.RomId, (int?)null), ct);
            }
        }
        return Result.Success;
    }
    private async Task ReleaseWholeOutputsAsync(int rom, IReadOnlyCollection<(int Output, int File)> redundant, int targetFile, CancellationToken ct)
    {
        int[] files = redundant.Select(row => row.File).Distinct().ToArray();
        int[] outputs = redundant.Select(row => row.Output).Distinct().ToArray();
        // Every original-layout dependency is moved to an already measured exact reconstruction before the old logical owner is detached.
        await _db.OriginalDiscLayoutFiles.Where(row => row.RomId == rom && files.Contains(row.FileId))
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.FileId, targetFile), ct);
        await _db.RepresentationOutputs.Where(row => row.RomId == rom && outputs.Contains(row.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.RomId, (int?)null), ct);
    }
}
