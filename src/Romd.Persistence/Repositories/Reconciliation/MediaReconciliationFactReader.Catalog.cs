using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Persistence.MediaFacts;

namespace Romd.Persistence.Repositories.Reconciliation;

internal sealed partial class MediaReconciliationFactReader
{
    private async Task<MediaReconciliationFactRead> CatalogLayoutAsync(int catalogFileId, bool locked, CancellationToken ct)
    {
        var binding = await CatalogCdLayoutRead.ReadAsync(_db, catalogFileId, ct);
        if (binding is null)
            return Ready(new MediaReconciliationFacts.None("catalog-layout-withdrawn"));
        var platform = await _db.Platforms.Where(row => row.Id == binding.PlatformId).Select(row => row.CanonicalKey).SingleOrDefaultAsync(ct);
        var kind = _kinds.Kinds.SingleOrDefault(kind => kind.ManifestType == MediaManifestType.CdTracks && platform is not null && kind.PlatformKeys.Contains(platform, StringComparer.Ordinal));
        if (kind is null)
            return Ready(new MediaReconciliationFacts.None("kind-unregistered"));
        var graph = (await ReadGraphsAsync([binding.RepresentationId], locked, ct)).GetValueOrDefault(binding.RepresentationId);
        if (graph?.Manifest is not RomReconstructionManifest.CdTracks manifest || graph.Outputs.Any(output => output.RomId is null) ||
            manifest.Members[binding.MemberOrdinal] is not RomCdStoredMember { Role: RomCdStoredRole.Cue } cue)
            return Ready(new MediaReconciliationFacts.None("catalog-layout-incomplete"));
        var required = binding.Requirement;
        bool exactCue = cue.Identity.Size == required.Size && cue.Identity.Sha1 == required.Sha1 &&
            cue.Identity.Md5 == required.Md5 && cue.Identity.Crc32 == required.Crc32;
        if (exactCue && !await CatalogCdLayoutRead.HasVerifiedOriginalAsync(_db, cue.Identity, ct))
            return Ready(new MediaReconciliationFacts.None("catalog-original-withdrawn"));
        var descriptor = graph.Descriptors.Single(row => row.Ordinal == binding.MemberOrdinal);
        var context = new MediaReconciliationLayout.Catalog(binding.Id, binding.Revision, binding.PlatformId, binding.Requirement, binding.GeneratorProfile);
        var proof = graph.Verifications.OrderBy(row => row.ObservedAt).ThenBy(row => row.Id).First();
        var inputs = graph.Outputs.Select(output => (MediaReconciliationInput)new MediaReconciliationInput.Logical(MediaReconciliationSourceRole.Bin,
            manifest.Members[((RomOutputSelector.Member)output.Selector).Ordinal].Path, graph, output.Id, proof.Id)).ToList();
        inputs.Add(new MediaReconciliationInput.Stored(MediaReconciliationSourceRole.Cue, cue.Path, descriptor.FileId, cue.Identity));
        int[] roots = graph.Outputs.Select(output => output.RomId!.Value).Distinct().ToArray();
        int[] wholeIds = await _db.RepresentationOutputs.Where(output => output.RomId != null && roots.Contains(output.RomId.Value) && output.MemberOrdinal == null)
            .Select(output => output.RepresentationId).Distinct().Order().Take(MediaReconciliationState.MaximumRepresentations + 1).ToArrayAsync(ct);
        if (wholeIds.Length + inputs.Count > 256)
            return Ready(new MediaReconciliationFacts.None("catalog-layout-source-limit"));
        foreach (int[] batch in wholeIds.Chunk(IMediaFactReader.MaximumBatchSize))
            foreach (var whole in (await ReadGraphsAsync(batch, locked, ct)).Values)
                inputs.Add(new MediaReconciliationInput.Artifact(whole, null, null));
        var sources = new MediaReconciliationSources(inputs, context, [], null);
        return Ready(new MediaReconciliationFacts.Layout(kind.Kind, sources, [graph], graph.Id, ownershipNeedsAlignment: false, null));
    }
}
