using ErrorOr;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

/// <summary>Committed scope facts. These are read models, never persisted kinds or publication authority.</summary>
public abstract class MediaReconciliationFacts
{
    private MediaReconciliationFacts() { }
    public sealed class Logical(RomContentDescriptor content, IReadOnlyList<string?> platformKeys) : MediaReconciliationFacts
    {
        public RomContentDescriptor Content { get; } = content;
        public IReadOnlyList<string?> PlatformKeys { get; } = Array.AsReadOnly(platformKeys.ToArray());
    }
    public sealed class Layout : MediaReconciliationFacts
    {
        public Layout(string kindId, MediaReconciliationSources sources, IReadOnlyList<RomRepresentationFacts> archives, int? currentArchiveId, bool ownershipNeedsAlignment, MediaReconciliationInput.Artifact? adoptionCandidate)
        {
            if (sources.Layout is null || sources.Inputs.Count(input => input.Role == MediaReconciliationSourceRole.Cue) != 1 ||
                !sources.Inputs.OfType<MediaReconciliationInput.Logical>().Any() ||
                archives.Any(graph => graph.Manifest is not RomReconstructionManifest.CdTracks) ||
                archives.Select(graph => graph.Id).Distinct().Count() != archives.Count ||
                currentArchiveId is { } current && !archives.Any(graph => graph.Id == current))
                throw new ArgumentException("Layout facts require complete scoped sources and actual retained archives.");
            if (adoptionCandidate is not null && (sources.Layout is not MediaReconciliationLayout.Original original ||
                adoptionCandidate.CatalogWitness is not { } witness || witness.PlatformId != original.PlatformId ||
                witness.ReleaseId != original.ReleaseId || witness.CatalogFingerprint != original.CatalogFingerprint ||
                adoptionCandidate.Graph.Outputs.Count != sources.Inputs.OfType<MediaReconciliationInput.Logical>().Count() ||
                adoptionCandidate.Graph.Outputs.Any(output => !sources.Inputs.OfType<MediaReconciliationInput.Logical>().Any(source =>
                    source.Output.RomId == output.RomId && source.Output.Identity == output.Identity))))
                throw new ArgumentException("Adoption candidates require one complete current catalog scope and every actual original logical identity.");
            KindId = kindId;
            Sources = sources;
            Archives = Array.AsReadOnly(archives.ToArray());
            CurrentArchiveId = currentArchiveId;
            OwnershipNeedsAlignment = ownershipNeedsAlignment;
            AdoptionCandidate = adoptionCandidate;
        }
        public string KindId { get; }
        public MediaReconciliationSources Sources { get; }
        public IReadOnlyList<RomRepresentationFacts> Archives { get; }
        public int? CurrentArchiveId { get; }
        public bool OwnershipNeedsAlignment { get; }
        public MediaReconciliationInput.Artifact? AdoptionCandidate { get; }
    }
    public sealed class Representation(RomRepresentationFacts graph) : MediaReconciliationFacts { public RomRepresentationFacts Graph { get; } = graph; }
    public sealed class None(string code) : MediaReconciliationFacts { public string Code { get; } = code; }
}
public interface IMediaReconciliationSnapshotReader
{
    Task<ErrorOr<MediaReconciliationFacts>> ReadAsync(MediaReconciliationSubject subject, CancellationToken ct = default);
}

/// <summary>Actual worker-side encoding identity and its whole-file codec binding, captured before media admission.</summary>
public sealed record MediaReconciliationEncoder(string Build, RomIsoDiscKind? WholeDiscKind);
public interface IMediaReconciliationMediaProfile
{
    Sha256 LimitsFingerprint { get; }
    RomDecoderIdentity.Managed OriginalInspectionDecoder { get; }
    MediaReconciliationEncoder? Encoder(string kind);
}

public abstract class MediaReconciliationDirective
{
    private MediaReconciliationDirective() { }
    public sealed class Inspect(MediaReconciliationCapture capture, MediaReconciliationInput.Logical source) : MediaReconciliationDirective
    {
        public MediaReconciliationCapture Capture { get; } = capture;
        public MediaReconciliationInput.Logical Source { get; } = source;
    }
    public sealed class Verify(MediaReconciliationCapture capture, RomMediaVerificationSource source) : MediaReconciliationDirective
    {
        public MediaReconciliationCapture Capture { get; } = capture;
        public RomMediaVerificationSource Source { get; } = source;
    }
    public sealed class Adopt(MediaReconciliationCapture capture) : MediaReconciliationDirective { public MediaReconciliationCapture Capture { get; } = capture; }
    public sealed class Encode(MediaReconciliationCapture capture) : MediaReconciliationDirective { public MediaReconciliationCapture Capture { get; } = capture; }
    public sealed class Release(MediaReconciliationCapture capture, int representationId, Guid proofId) : MediaReconciliationDirective
    {
        public MediaReconciliationCapture Capture { get; } = capture;
        public int RepresentationId { get; } = representationId;
        public Guid ProofId { get; } = proofId;
    }
    public sealed class Settle(MediaReconciliationOutcomeKind outcome, string code) : MediaReconciliationDirective
    {
        public MediaReconciliationOutcomeKind Outcome { get; } = outcome;
        public string Code { get; } = code;
    }
    public sealed class Unavailable(string code) : MediaReconciliationDirective { public string Code { get; } = code; }
}
public interface IMediaReconciliationPlanning
{
    Task<ErrorOr<MediaReconciliationDirective>> NextAsync(Guid jobId, MediaReconciliationDemandState demand, CancellationToken ct = default);
}
