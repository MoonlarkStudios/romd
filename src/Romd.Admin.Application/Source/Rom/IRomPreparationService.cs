using ErrorOr;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Inspects and hashes logical content before publication; never runs inside a database transaction.</summary>
public interface IRomPreparationService
{
    int SelectPrefixLength(ReadOnlyMemory<byte> prefix, string filename);
    bool RequiresDatHashes(ReadOnlyMemory<byte> magic, string filename);
    Task<IRomPreparationAdmission?> AdmitAsync(ReadOnlyMemory<byte> magic, string filename, CancellationToken ct = default);
    Task<ErrorOr<PreparedRom>> PrepareAsync(ITempFile source, string filename, CancellationToken ct = default);
}

/// <summary>Owns inspection capacity across staging and verification; dispose if staging fails.</summary>
public interface IRomPreparationAdmission : IDisposable
{
    Task<ErrorOr<PreparedRom>> PrepareAsync(ITempFile source, string filename, CancellationToken ct = default);
}

/// <summary>Closed preparation shapes. Whole-file logical evidence and unbound CD observations have different publication contracts.</summary>
public abstract class PreparedRom : IAsyncDisposable
{
    private PreparedRom() { }
    public abstract ITempFile Artifact { get; }
    public abstract ValueTask DisposeAsync();

    /// <summary>The caller retains ownership of the source. A replacement artifact is owned by this result.</summary>
    public sealed class WholeFile(ITempFile artifact, string logicalFilename, Sha256 contentSha256,
        DatHashResult hashes, RomRepresentationEncoding encoding, RomVerification verification, RomContainerFacts container, bool ownsArtifact = false, string? encodingProfile = null,
        RomIsoEligibility? eligibility = null, RomSourceEquivalence? sourceEquivalence = null,
        RomPs2BootObservation? ps2Observation = null, RomChdIntegrity? chdIntegrity = null,
        RomChdEncodingEvidence? chdEncodingEvidence = null, RomPs2BootLayoutFacts? chdPs2Boot = null, RomLogicalInspection? inspection = null) : PreparedRom
    {
        private readonly bool _ownsArtifact = ownsArtifact;
        public override ITempFile Artifact { get; } = artifact;
        public string LogicalFilename { get; } = logicalFilename;
        public Sha256 ContentSha256 { get; } = contentSha256;
        public DatHashResult Hashes { get; } = hashes;
        public RomRepresentationEncoding Encoding { get; } = encoding;
        public RomContainerFacts Container { get; } = ValidateContainer(container, encoding, verification);
        private readonly RomRepresentationVerification _measurement = Measure(container, verification, chdIntegrity);
        public RomRepresentationVerification Measurement => _measurement;
        public RomLogicalInspection? Inspection { get; } = ValidateInspection(inspection, verification);
        public string EncodingProfile { get; } = encodingProfile ?? (encoding == RomRepresentationEncoding.Identity
            ? RomEncodingProfiles.Identity : RomEncodingProfiles.UnknownImported);
        public RomVerification Verification { get; } = ValidateVerification(artifact, contentSha256, hashes, encoding, verification,
            encodingProfile ?? (encoding == RomRepresentationEncoding.Identity ? RomEncodingProfiles.Identity : RomEncodingProfiles.UnknownImported));
        public RomIsoEligibility? Eligibility { get; } = ValidateEligibility(eligibility, verification);
        public RomSourceEquivalence? SourceEquivalence { get; } = ValidateSourceEquivalence(sourceEquivalence, verification);
        public RomPs2BootObservation? Ps2Observation { get; } = ValidatePs2Observation(ps2Observation, eligibility, verification, ownsArtifact);
        /// <summary>Required for CHD; binds this exact output measurement without claiming encoding provenance.</summary>
        public RomChdIntegrity? ChdIntegrity { get; } = ValidateChdIntegrity(chdIntegrity, encoding, verification);
        /// <summary>Present with source equivalence for created CHD; absent for imported CHD integrity.</summary>
        public RomChdEncodingEvidence? ChdEncodingEvidence { get; } = ValidateChdEncodingEvidence(
            chdEncodingEvidence, chdIntegrity, sourceEquivalence, encoding, verification);
        /// <summary>Observed CHD logical boot layout; independent DVD eligibility may retain its earlier inspection time.</summary>
        public RomPs2BootLayoutFacts? ChdPs2Boot { get; } = ValidateChdPs2Boot(chdPs2Boot, eligibility, encoding, verification);
        /// <summary>Observed boot remains durable independently of a current positive DVD reference.</summary>
        public RomPs2BootEvidence? Ps2BootEvidence { get; } = ps2Observation is not null
            ? RomPs2BootEvidence.FromOriginal(ps2Observation)
            : chdPs2Boot is not null && chdIntegrity is not null
                ? RomPs2BootEvidence.FromChd(verification, chdPs2Boot, chdIntegrity) : null;
        public override ValueTask DisposeAsync() => _ownsArtifact ? Artifact.DisposeAsync() : ValueTask.CompletedTask;

        /// <summary>Resolve a borrowed original without changing its artifact, measured identity or ownership.</summary>
        public WholeFile WithPs2Eligibility(RomIsoEligibility? resolved)
        {
            if (Ps2Observation is null || _ownsArtifact)
                throw new InvalidOperationException("Only a borrowed original PS2 observation can receive local DVD eligibility.");
            return new(Artifact, LogicalFilename, ContentSha256, Hashes, Encoding, Verification, Container,
                encodingProfile: EncodingProfile, eligibility: resolved, sourceEquivalence: SourceEquivalence, ps2Observation: Ps2Observation, inspection: Inspection);
        }

        /// <summary>Resolve a borrowed CHD boot layout without replacing its measurement, integrity or encoding provenance.</summary>
        public WholeFile WithChdPs2Eligibility(RomIsoEligibility? resolved)
        {
            if (Encoding != RomRepresentationEncoding.Chd || ChdPs2Boot is null || _ownsArtifact)
                throw new InvalidOperationException("Only a borrowed CHD boot observation can receive local DVD eligibility.");
            if (resolved is not null && (resolved.Status != RomIsoEligibilityStatus.Supported ||
                resolved.DiscKind != RomIsoDiscKind.PlayStation2Dvd))
                throw new ArgumentException("CHD PS2 resolution requires a supported PS2 DVD observation or no eligibility.", nameof(resolved));
            return new(Artifact, LogicalFilename, ContentSha256, Hashes, Encoding, Verification, Container,
                encodingProfile: EncodingProfile, eligibility: resolved, sourceEquivalence: SourceEquivalence,
                ps2Observation: Ps2Observation, chdIntegrity: ChdIntegrity, chdEncodingEvidence: ChdEncodingEvidence, chdPs2Boot: ChdPs2Boot, inspection: Inspection);
        }

        private static RomLogicalInspection? ValidateInspection(RomLogicalInspection? inspection, RomVerification verification)
        {
            if (inspection is not null && inspection.Identity != new RomLogicalIdentity(verification.ContentSize, verification.ContentSha256,
                    verification.Sha1, verification.Md5, verification.Crc32))
                throw new ArgumentException("Inspection must describe the independently measured logical bytes.", nameof(inspection));
            return inspection;
        }

        private static RomContainerFacts ValidateContainer(RomContainerFacts container, RomRepresentationEncoding encoding, RomVerification proof)
        {
            ArgumentNullException.ThrowIfNull(container);
            if (container.Encoding != encoding || container is RomContainerFacts.Rvz rvz && rvz.LogicalBytes != proof.ContentSize ||
                container is RomContainerFacts.Chd chd && chd.LogicalBytes != proof.ContentSize ||
                container is RomContainerFacts.Identity && (proof.ArtifactSha256 != proof.ContentSha256 || proof.ArtifactSize != proof.ContentSize))
                throw new ArgumentException("Preparation requires independently observed container and logical geometry.", nameof(container));
            return container;
        }

        public static RomRepresentationVerification Measure(RomContainerFacts container, RomVerification proof, RomChdIntegrity? integrity) =>
            new(proof.Id, proof.ArtifactSha256, proof.ArtifactSize, container is RomContainerFacts.Chd
                ? new RomDecoderIdentity.Native(integrity?.Decoder ?? throw new ArgumentException("CHD measurement requires its native decoder tuple."))
                : new RomDecoderIdentity.Managed(proof.DecoderBuild), proof.Profile, proof.VerifierBuild, proof.VerifiedAt,
                [new(RomOutputSelector.Whole.Instance, new(proof.ContentSize, proof.ContentSha256, proof.Sha1, proof.Md5, proof.Crc32))], container,
                integrity?.ComputedRawSha1, integrity?.ComputedOverallSha1);

        private static RomVerification ValidateVerification(ITempFile artifact, Sha256 contentSha256,
            DatHashResult hashes, RomRepresentationEncoding encoding, RomVerification verification, string encodingProfile)
        {
            ArgumentNullException.ThrowIfNull(verification);
            if (verification.ArtifactSha256 != artifact.FileSha256 || verification.ArtifactSize != artifact.FileSize ||
                verification.ContentSha256 != contentSha256 || verification.ContentSize != hashes.BytesRead ||
                verification.Sha1 != hashes.Sha1 || verification.Md5 != hashes.Md5 || verification.Crc32 != hashes.Crc32 ||
                verification.Encoding != encoding ||
                verification.CapturedEncodingProfile is { } captured && captured != encodingProfile)
                throw new InvalidDataException("Preparation evidence does not describe the artifact and complete output.");
            return verification;
        }

        private static RomIsoEligibility? ValidateEligibility(RomIsoEligibility? eligibility, RomVerification verification)
        {
            if (eligibility is not null && (eligibility.ContentSha256 != verification.ContentSha256 ||
                eligibility.ContentSize != verification.ContentSize))
                throw new ArgumentException("Eligibility does not describe the measured logical bytes.", nameof(eligibility));
            return eligibility;
        }

        private static RomSourceEquivalence? ValidateSourceEquivalence(RomSourceEquivalence? comparison, RomVerification verification)
        {
            if (comparison is not null && !comparison.MatchesOutput(verification))
                throw new ArgumentException("Source equivalence does not bind the exact independent output measurement.", nameof(comparison));
            return comparison;
        }

        private static RomPs2BootObservation? ValidatePs2Observation(RomPs2BootObservation? observation,
            RomIsoEligibility? eligibility, RomVerification verification, bool ownsArtifact)
        {
            if (observation is not null && (!observation.Matches(verification) || ownsArtifact ||
                eligibility is not null && (eligibility.DiscKind != RomIsoDiscKind.PlayStation2Dvd ||
                    eligibility.Status != RomIsoEligibilityStatus.Supported ||
                    eligibility.Ps2DvdFacts is not { } details || details.Boot != observation.Boot ||
                    details.Sha1 != verification.Sha1 || details.Md5 != verification.Md5 || details.Crc32 != verification.Crc32)))
                throw new ArgumentException("PS2 preparation must bind this exact borrowed measurement and boot observation.", nameof(observation));
            return observation;
        }

        private static RomChdIntegrity? ValidateChdIntegrity(RomChdIntegrity? integrity,
            RomRepresentationEncoding encoding, RomVerification verification)
        {
            if (encoding != RomRepresentationEncoding.Chd)
            {
                if (integrity is not null)
                    throw new ArgumentException("CHD integrity cannot describe a non-CHD artifact.", nameof(integrity));
                return null;
            }
            if (integrity is null || !integrity.MatchesMeasurement(verification))
                throw new ArgumentException("CHD preparation requires integrity bound to this exact measurement.", nameof(integrity));
            return integrity;
        }

        private static RomChdEncodingEvidence? ValidateChdEncodingEvidence(RomChdEncodingEvidence? evidence,
            RomChdIntegrity? integrity, RomSourceEquivalence? comparison, RomRepresentationEncoding encoding,
            RomVerification verification)
        {
            if (encoding != RomRepresentationEncoding.Chd)
            {
                if (evidence is not null)
                    throw new ArgumentException("CHD encoding evidence cannot describe a non-CHD artifact.", nameof(evidence));
                return null;
            }
            if ((comparison is null) != (evidence is null))
                throw new ArgumentException("Created CHD comparison and encoding evidence must be supplied together.", nameof(evidence));
            if (evidence is not null && (integrity is null || !evidence.MatchesComparison(comparison) ||
                evidence.OutputVerificationId != verification.Id || evidence.IntegrityId != integrity.Id ||
                evidence.Parameters != integrity.Parameters))
                throw new ArgumentException("CHD encoding evidence must bind the exact comparison, output and integrity parameters.", nameof(evidence));
            return evidence;
        }

        private static RomPs2BootLayoutFacts? ValidateChdPs2Boot(RomPs2BootLayoutFacts? boot,
            RomIsoEligibility? eligibility, RomRepresentationEncoding encoding, RomVerification verification)
        {
            if (encoding != RomRepresentationEncoding.Chd)
            {
                if (boot is not null)
                    throw new ArgumentException("CHD boot facts cannot describe a non-CHD artifact.", nameof(boot));
                return null;
            }
            if (boot is not null && boot.LogicalBytes != verification.ContentSize)
                throw new ArgumentException("CHD boot facts must describe the measured logical size.", nameof(boot));
            if (eligibility is { Status: RomIsoEligibilityStatus.Supported, DiscKind: RomIsoDiscKind.PlayStation2Dvd } &&
                (boot is null ||
                    eligibility.Ps2DvdFacts is not { } details || details.Boot != boot ||
                    details.ContentSha256 != verification.ContentSha256 || details.ContentSize != verification.ContentSize ||
                    details.Sha1 != verification.Sha1 || details.Md5 != verification.Md5 || details.Crc32 != verification.Crc32))
                throw new ArgumentException("PS2 CHD eligibility must bind this exact measured boot layout and logical identity.", nameof(eligibility));
            return boot;
        }
    }

    /// <summary>The staged source is borrowed. Catalog names and generated descriptor bytes are bound after stopped inspection.</summary>
    public sealed class Cd : PreparedRom
    {
        public Cd(ITempFile artifact, RomCdInspection inspection)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            ArgumentNullException.ThrowIfNull(inspection);
            if (artifact.FileSha256 != inspection.ArtifactIdentity.Sha256 || artifact.FileSize != inspection.ArtifactIdentity.Size ||
                artifact.DatHashes is not { } hashes || hashes.BytesRead != artifact.FileSize ||
                hashes.Sha1 != inspection.ArtifactIdentity.Sha1 || hashes.Md5 != inspection.ArtifactIdentity.Md5 || hashes.Crc32 != inspection.ArtifactIdentity.Crc32)
                throw new InvalidDataException("CD preparation requires the exact staged artifact and its complete raw identity.");
            Artifact = artifact;
            Inspection = inspection;
        }
        public override ITempFile Artifact { get; }
        public RomCdInspection Inspection { get; }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
