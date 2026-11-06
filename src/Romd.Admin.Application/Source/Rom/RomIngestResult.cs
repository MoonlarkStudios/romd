using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Closed ingestion receipt. Compound CD ownership never supplies an arbitrary single-ROM identity.</summary>
public abstract class RomIngestResult
{
    private RomIngestResult() { }
    public required bool IsNew { get; init; }
    public required IReadOnlyList<int> MatchedTitleIds { get; init; }
    public required int? PlatformId { get; init; }

    public sealed class WholeFile : RomIngestResult
    {
        public required RomFile RomFile { get; init; }
    }

    /// <summary>Actual retained archive/output/verification IDs and, when published, the genuine ordinary CUE graph.</summary>
    public sealed class CdArchive : RomIngestResult
    {
        public CdArchive(RomRepresentationFacts archive, RomRepresentationFacts? originalCue)
        {
            ArgumentNullException.ThrowIfNull(archive);
            if (archive.Manifest is not RomReconstructionManifest.CdTracks cd || archive.Verifications.Count == 0 ||
                archive.Outputs.Count == 0 || archive.Outputs.Any(output => output.RomId is not > 0))
                throw new ArgumentException("A CD receipt requires the actual complete retained archive and logical BIN owners.", nameof(archive));
            var cue = cd.Members.OfType<RomCdStoredMember>().Single(member => member.Role == RomCdStoredRole.Cue);
            if (originalCue is not null && (originalCue.Container is not RomContainerFacts.Identity ||
                originalCue.Manifest is not RomReconstructionManifest.WholeFile || originalCue.Verifications.Count == 0 ||
                originalCue.Outputs is not [var output] || output.Selector is not RomOutputSelector.Whole || output.RomId is not > 0 ||
                output.Identity.Size != cue.Identity.Size || output.Identity.Sha256 != cue.Identity.Sha256 ||
                output.Identity.Sha1 != cue.Identity.Sha1 || output.Identity.Md5 != cue.Identity.Md5 || output.Identity.Crc32 != cue.Identity.Crc32))
                throw new ArgumentException("An original CUE receipt requires its genuine measured ordinary identity and exact actual descriptor bytes.", nameof(originalCue));
            Archive = archive;
            OriginalCue = originalCue;
        }
        public RomRepresentationFacts Archive { get; }
        public RomRepresentationFacts? OriginalCue { get; }
    }
}
