using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

public enum MediaReconciliationSourceRole { Whole = 1, Bin = 2, Cue = 3, Sidecar = 4, Artifact = 5 }

/// <summary>Captured physical and logical identities. These describe custody, not permission to publish or delete.</summary>
public abstract class MediaReconciliationInput
{
    private MediaReconciliationInput(MediaReconciliationSourceRole role, string? path) => (Role, Path) = (role, path);
    public MediaReconciliationSourceRole Role { get; }
    public string? Path { get; }
    public sealed class Logical : MediaReconciliationInput
    {
        public Logical(MediaReconciliationSourceRole role, string path, RomRepresentationFacts graph, int outputId, Guid verificationId)
            : base(role, CheckedPath(path))
        {
            ArgumentNullException.ThrowIfNull(graph);
            var output = graph.Outputs.SingleOrDefault(output => output.Id == outputId);
            if (role is not (MediaReconciliationSourceRole.Whole or MediaReconciliationSourceRole.Bin) || output?.RomId is null ||
                !graph.Verifications.Any(proof => proof.Id == verificationId) ||
                role == MediaReconciliationSourceRole.Whole && graph.Manifest is not RomReconstructionManifest.WholeFile)
                throw new ArgumentException("A logical source requires its exact owned output and retained independent measurement.");
            Graph = graph;
            Output = output;
            VerificationId = verificationId;
        }
        public RomRepresentationFacts Graph { get; }
        public RomRepresentationOutput Output { get; }
        public Guid VerificationId { get; }
    }
    public sealed class Stored : MediaReconciliationInput
    {
        public Stored(MediaReconciliationSourceRole role, string path, int fileId, RomCdFileIdentity identity)
            : base(role, CheckedPath(path))
        {
            ArgumentNullException.ThrowIfNull(identity);
            if (role is not (MediaReconciliationSourceRole.Cue or MediaReconciliationSourceRole.Sidecar) || fileId <= 0)
                throw new ArgumentException("A stored source requires an exact descriptor file.");
            FileId = fileId;
            Identity = identity;
        }
        public int FileId { get; }
        public RomCdFileIdentity Identity { get; }
    }
    public sealed class Artifact : MediaReconciliationInput
    {
        public Artifact(RomRepresentationFacts graph, Guid? verificationId, MediaReconciliationLayout.Catalog? catalogWitness) : base(MediaReconciliationSourceRole.Artifact, null)
        {
            ArgumentNullException.ThrowIfNull(graph);
            if (verificationId is { } id && !graph.Verifications.Any(proof => proof.Id == id))
                throw new ArgumentException("The selected proof must belong to the exact retained artifact.");
            if (catalogWitness is not null && (graph.Manifest is not RomReconstructionManifest.CdTracks cd ||
                !cd.Members.OfType<RomCdStoredMember>().Any(member => member.Role == RomCdStoredRole.Cue && member.Path == catalogWitness.Requirement.Path)))
                throw new ArgumentException("A catalog artifact witness requires its actual retained CD descriptor.", nameof(catalogWitness));
            Graph = graph;
            VerificationId = verificationId;
            CatalogWitness = catalogWitness;
        }
        public RomRepresentationFacts Graph { get; }
        public Guid? VerificationId { get; }
        public MediaReconciliationLayout.Catalog? CatalogWitness { get; }
    }
    private static string CheckedPath(string path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 500 && !path.Any(char.IsControl)
        ? path : throw new ArgumentException("A bounded original source name is required.", nameof(path));
}

/// <summary>Exact accepted layout/catalog revision, rather than a persisted eligibility projection.</summary>
public abstract class MediaReconciliationLayout
{
    private MediaReconciliationLayout(MediaReconciliationSubject subject, Guid revision, int platformId, int releaseId, string catalogFingerprint, string entryPoint)
    {
        if (revision == Guid.Empty || platformId <= 0 || releaseId <= 0 ||
            string.IsNullOrWhiteSpace(catalogFingerprint) || catalogFingerprint.Length > 128 || catalogFingerprint.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(entryPoint) || entryPoint.Length > 240 || entryPoint.Any(char.IsControl))
            throw new ArgumentException("A complete bounded layout and current catalog binding are required.");
        Subject = subject;
        Revision = revision;
        PlatformId = platformId;
        ReleaseId = releaseId;
        CatalogFingerprint = catalogFingerprint;
        EntryPoint = entryPoint;
    }
    public MediaReconciliationSubject Subject { get; }
    public Guid Revision { get; }
    public int PlatformId { get; }
    public int ReleaseId { get; }
    public string CatalogFingerprint { get; }
    public string EntryPoint { get; }
    public sealed class Original : MediaReconciliationLayout
    {
        public Original(int layoutId, Guid revision, int platformId, int releaseId, string catalogFingerprint, string entryPoint)
            : base(new MediaReconciliationSubject.OriginalLayout(layoutId), revision, platformId, releaseId, catalogFingerprint, entryPoint) => LayoutId = layoutId;
        public int LayoutId { get; }
    }
    public sealed class Catalog : MediaReconciliationLayout
    {
        public Catalog(int bindingId, Guid revision, int platformId, CatalogDescriptorRequirement requirement, string generatorProfile)
            : base(Scope(requirement), revision, platformId, requirement.ReleaseId, requirement.ReleaseFingerprint, requirement.Path)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bindingId);
            if (string.IsNullOrWhiteSpace(generatorProfile) || generatorProfile.Length > 128 || generatorProfile.Any(char.IsControl))
                throw new ArgumentException("The actual descriptor generator profile is required.", nameof(generatorProfile));
            BindingId = bindingId;
            Requirement = requirement;
            GeneratorProfile = generatorProfile;
        }
        public int BindingId { get; }
        public CatalogDescriptorRequirement Requirement { get; }
        public string GeneratorProfile { get; }
        private static MediaReconciliationSubject Scope(CatalogDescriptorRequirement requirement)
        {
            ArgumentNullException.ThrowIfNull(requirement);
            if (string.IsNullOrWhiteSpace(requirement.FileFingerprint) || requirement.FileFingerprint.Length > 120 || requirement.FileFingerprint.Any(char.IsControl) ||
                requirement.Size is < 1 or > 65536 || requirement.Sha1.IsEmpty || requirement.Md5.IsEmpty || requirement.Crc32.IsEmpty)
                throw new ArgumentException("An exact current catalog descriptor requirement is required.", nameof(requirement));
            return new MediaReconciliationSubject.CatalogCdLayout(requirement.CatalogFileId);
        }
    }
}

/// <summary>A complete source vector. Additional unrelated decoder-history rows do not change the source bytes.</summary>
public sealed class MediaReconciliationSources
{
    public MediaReconciliationSources(IReadOnlyList<MediaReconciliationInput> inputs, MediaReconciliationLayout? layout,
        IReadOnlyList<Guid> logicalInspectionIds, Sha256? referenceFingerprint)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(logicalInspectionIds);
        var captured = inputs.ToArray();
        var inspections = logicalInspectionIds.ToArray();
        if (captured.Length is < 1 or > 256 || captured.Any(input => input is null) || inspections.Length > 256 ||
            inspections.Any(id => id == Guid.Empty) || inspections.Distinct().Count() != inspections.Length ||
            captured.Where(input => input.Path is not null).Select(input => input.Path!).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                captured.Count(input => input.Path is not null) ||
            captured.OfType<MediaReconciliationInput.Artifact>().GroupBy(input => input.Graph.Id).Any(group => group.Count() > 1) ||
            referenceFingerprint is { IsEmpty: true })
            throw new ArgumentException("A source vector requires bounded distinct exact inputs and selected immutable observations.");
        Inputs = Array.AsReadOnly(captured.OrderBy(input => input.Role).ThenBy(input => input.Path, StringComparer.Ordinal)
            .ThenBy(input => input is MediaReconciliationInput.Artifact artifact ? artifact.Graph.Id : 0).ToArray());
        Layout = layout;
        LogicalInspectionIds = Array.AsReadOnly(inspections.Order().ToArray());
        ReferenceFingerprint = referenceFingerprint;
        Fingerprint = MediaReconciliationSourceFingerprint.Compute(this);
    }
    public IReadOnlyList<MediaReconciliationInput> Inputs { get; }
    public MediaReconciliationLayout? Layout { get; }
    public IReadOnlyList<Guid> LogicalInspectionIds { get; }
    public Sha256? ReferenceFingerprint { get; }
    public Sha256 Fingerprint { get; }
    /// <summary>Every authentic original must retain its exact path, role and complete independently compared identity.</summary>
    public bool MatchesOriginals(RomReconstructionManifest.CdTracks manifest)
    {
        var originals = Inputs.Where(input => input is MediaReconciliationInput.Logical or MediaReconciliationInput.Stored).ToArray();
        if (originals.Length != manifest.Members.Count)
            return false;
        return originals.All(input =>
        {
            var member = manifest.Members.SingleOrDefault(member => member.Path == input.Path);
            if (input is MediaReconciliationInput.Logical logical)
                return logical.Role == MediaReconciliationSourceRole.Bin && member is RomCdBinMember &&
                    member.Identity.Size == logical.Output.Identity.Size && member.Identity.Sha256 == logical.Output.Identity.Sha256 &&
                    member.Identity.Sha1 == logical.Output.Identity.Sha1 && member.Identity.Md5 == logical.Output.Identity.Md5 && member.Identity.Crc32 == logical.Output.Identity.Crc32;
            var stored = (MediaReconciliationInput.Stored)input;
            return member is RomCdStoredMember descriptor && member.Identity == stored.Identity &&
                descriptor.Role == (stored.Role == MediaReconciliationSourceRole.Cue ? RomCdStoredRole.Cue : RomCdStoredRole.Sidecar);
        });
    }
}
