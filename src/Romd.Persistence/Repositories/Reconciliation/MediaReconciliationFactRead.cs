using ErrorOr;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.Repositories.Reconciliation;

/// <summary>Descriptor hashing is completed outside read transactions, or bound to the exact previously captured original under publication locks.</summary>
internal abstract class MediaReconciliationFactRead
{
    private MediaReconciliationFactRead() { }
    internal sealed class Complete(MediaReconciliationFacts facts) : MediaReconciliationFactRead
    {
        internal MediaReconciliationFacts Facts { get; } = facts;
    }
    internal sealed class Layout(string kind, MediaReconciliationLayout context, IReadOnlyList<MediaReconciliationInput> inputs,
        IReadOnlyList<RomRepresentationFacts> archives, int? owner, bool alignment, int cueFileId, Sha256 cueHash, long cueSize, MediaReconciliationInput.Artifact? adoptionCandidate) : MediaReconciliationFactRead
    {
        internal int CueFileId { get; } = cueFileId;
        internal Sha256 CueHash { get; } = cueHash;
        internal long CueSize { get; } = cueSize;
        internal ErrorOr<MediaReconciliationFacts> WithCue(RomCdFileIdentity cue)
        {
            if (cue.Sha256 != CueHash || cue.Size != CueSize)
                return MediaReconciliationErrors.Changed;
            var sources = new MediaReconciliationSources([.. inputs, new MediaReconciliationInput.Stored(MediaReconciliationSourceRole.Cue, context.EntryPoint, CueFileId, cue)], context, [], null);
            if (archives.Any(graph => !MediaReconciliationFactReader.Renders(graph, sources)))
                return new MediaReconciliationFacts.None("owned-archive-originals-changed");
            return new MediaReconciliationFacts.Layout(kind, sources, archives, owner, alignment, adoptionCandidate);
        }
    }
}
