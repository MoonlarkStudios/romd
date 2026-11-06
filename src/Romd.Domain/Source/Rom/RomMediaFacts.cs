using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

/// <summary>Exact logical bytes, independent of container, placement, platform and policy.</summary>
public sealed record RomLogicalIdentity
{
    public RomLogicalIdentity(long size, Sha256 sha256, Sha1 sha1, Md5 md5, Crc32 crc32)
    {
        if (size < 0 || sha256.IsEmpty || sha1.IsEmpty || md5.IsEmpty || crc32.IsEmpty)
            throw new ArgumentException("Logical identity requires a length and every measured hash.");
        Size = size;
        Sha256 = sha256;
        Sha1 = sha1;
        Md5 = md5;
        Crc32 = crc32;
    }
    public long Size { get; }
    public Sha256 Sha256 { get; }
    public Sha1 Sha1 { get; }
    public Md5 Md5 { get; }
    public Crc32 Crc32 { get; }
}

/// <summary>A decoder identity is a managed build or an exact native source/build tuple.</summary>
public abstract class RomDecoderIdentity : IEquatable<RomDecoderIdentity>
{
    private RomDecoderIdentity() { }
    public abstract string Key { get; }
    public bool Equals(RomDecoderIdentity? other) => other is not null && Key == other.Key;
    public sealed override bool Equals(object? other) => other is RomDecoderIdentity identity && Equals(identity);
    public sealed override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);

    public sealed class Managed : RomDecoderIdentity
    {
        public Managed(string build)
        {
            RomFactLabels.Validate(build, 256);
            Build = build;
        }
        public string Build { get; }
        public override string Key => "managed:" + Build;
    }
    public sealed class Native : RomDecoderIdentity
    {
        public Native(RomChdDecoderBuild build) { ArgumentNullException.ThrowIfNull(build); Build = build; }
        public RomChdDecoderBuild Build { get; }
        public override string Key => "native:" + Build.BuildLabel;
    }
}

public sealed record RomMeasuredOutput
{
    public RomMeasuredOutput(RomOutputSelector selector, RomLogicalIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(identity);
        Selector = selector;
        Identity = identity;
    }
    public RomOutputSelector Selector { get; }
    public RomLogicalIdentity Identity { get; }
}

/// <summary>One immutable, complete measurement by one decoder. It grants no runtime or request scope.</summary>
public sealed class RomRepresentationVerification
{
    public RomRepresentationVerification(Guid id, Sha256 artifactSha256, long artifactSize, RomDecoderIdentity decoder,
        string checkedProfile, string verifierBuild, DateTimeOffset observedAt, IReadOnlyList<RomMeasuredOutput> outputs, RomContainerFacts observedContainer,
        Sha1? computedRawSha1 = null, Sha1? computedOverallSha1 = null)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(observedContainer);
        RomFactLabels.Validate(checkedProfile, 128);
        RomFactLabels.Validate(verifierBuild, 256);
        var captured = outputs.ToArray();
        if (id == Guid.Empty || artifactSha256.IsEmpty || artifactSize < 0 || captured.Length is < 1 or > 199 ||
            captured.Any(output => output is null) || captured.Select(output => output.Selector).Distinct().Count() != captured.Length ||
            (decoder is RomDecoderIdentity.Native) != (observedContainer is RomContainerFacts.Chd) ||
            (decoder is RomDecoderIdentity.Native ? computedRawSha1 is not { IsEmpty: false } || computedOverallSha1 is not { IsEmpty: false }
                : computedRawSha1 is not null || computedOverallSha1 is not null))
            throw new ArgumentException("A verification requires exact artifact, decoder and complete distinct output measurements.");
        Id = id;
        ArtifactSha256 = artifactSha256;
        ArtifactSize = artifactSize;
        Decoder = decoder;
        CheckedProfile = checkedProfile;
        VerifierBuild = verifierBuild;
        ObservedAt = observedAt.ToUniversalTime();
        ObservedParametersSha256 = observedContainer.Fingerprint;
        Outputs = Array.AsReadOnly(captured);
        ComputedRawSha1 = computedRawSha1;
        ComputedOverallSha1 = computedOverallSha1;
    }
    public Guid Id { get; }
    public Sha256 ArtifactSha256 { get; }
    public long ArtifactSize { get; }
    public RomDecoderIdentity Decoder { get; }
    public string CheckedProfile { get; }
    public string VerifierBuild { get; }
    public DateTimeOffset ObservedAt { get; }
    public IReadOnlyList<RomMeasuredOutput> Outputs { get; }
    public Sha256? ObservedParametersSha256 { get; }
    public Sha1? ComputedRawSha1 { get; }
    public Sha1? ComputedOverallSha1 { get; }
}

public sealed record RomRepresentationOutput
{
    public RomRepresentationOutput(int id, int? romId, RomOutputSelector selector, RomLogicalIdentity identity)
    {
        if (id <= 0 || romId is <= 0)
            throw new ArgumentException("Output ownership requires positive retained identities.");
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(identity);
        Id = id;
        RomId = romId;
        Selector = selector;
        Identity = identity;
    }
    public int Id { get; }
    public int? RomId { get; }
    public RomOutputSelector Selector { get; }
    public RomLogicalIdentity Identity { get; }
}

public sealed record RomRetainedDescriptor
{
    public RomRetainedDescriptor(int ordinal, int fileId, Sha256 sha256, long size)
    {
        if (ordinal is < 0 or > 198 || fileId <= 0 || sha256.IsEmpty || size is < 1 or > 16777216)
            throw new ArgumentException("A retained descriptor requires its exact owned file.");
        Ordinal = ordinal;
        FileId = fileId;
        Sha256 = sha256;
        Size = size;
    }
    public int Ordinal { get; }
    public int FileId { get; }
    public Sha256 Sha256 { get; }
    public long Size { get; }
}

/// <summary>One retained artifact, its immutable layout and every independently observed decoder measurement.</summary>
public sealed class RomRepresentationFacts
{
    public RomRepresentationFacts(int id, int fileId, Sha256 artifactSha256, long artifactSize, RomContainerFacts container,
        RomReconstructionManifest manifest, IReadOnlyList<RomRepresentationOutput> outputs,
        IReadOnlyList<RomRepresentationVerification> verifications, IReadOnlyList<RomRetainedDescriptor> descriptors,
        IReadOnlyList<RomRepresentationCreation>? creations = null, IReadOnlyList<RomCdBootInspection>? cdBootInspections = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(verifications);
        ArgumentNullException.ThrowIfNull(descriptors);
        var capturedCreations = creations?.ToArray() ?? [];
        var capturedBoots = cdBootInspections?.ToArray() ?? [];
        var capturedOutputs = outputs.ToArray();
        var capturedProofs = verifications.ToArray();
        var capturedDescriptors = descriptors.ToArray();
        if (id <= 0 || fileId <= 0 || artifactSha256.IsEmpty || artifactSize < 0 || capturedOutputs.Length is < 1 or > 199 ||
            capturedOutputs.Any(output => output is null) || capturedProofs.Any(proof => proof is null) || capturedDescriptors.Any(descriptor => descriptor is null) ||
            capturedOutputs.Select(output => output.Id).Distinct().Count() != capturedOutputs.Length ||
            capturedOutputs.Select(output => output.Selector).Distinct().Count() != capturedOutputs.Length ||
            capturedProofs.Select(proof => proof.Id).Distinct().Count() != capturedProofs.Length ||
            capturedProofs.Select(proof => proof.Decoder).Distinct().Count() != capturedProofs.Length ||
            capturedDescriptors.Select(descriptor => descriptor.Ordinal).Distinct().Count() != capturedDescriptors.Length)
            throw new ArgumentException("A retained representation requires complete distinct identities and decoder measurements.");
        if (manifest is RomReconstructionManifest.WholeFile)
        {
            var output = capturedOutputs.Length == 1 ? capturedOutputs[0] : null;
            if (capturedOutputs.Length != 1 || output?.Selector is not RomOutputSelector.Whole || capturedDescriptors.Length != 0 ||
                (container is RomContainerFacts.Identity && (artifactSha256 != output.Identity.Sha256 || artifactSize != output.Identity.Size)) ||
                (container is RomContainerFacts.Rvz rvz && rvz.LogicalBytes != output.Identity.Size) ||
                (container is RomContainerFacts.Chd chd && chd.LogicalBytes != output.Identity.Size))
                throw new ArgumentException("A whole-file representation requires one exact output.");
        }
        else if (manifest is RomReconstructionManifest.CdTracks cd)
        {
            if (container is not RomContainerFacts.Chd chd || chd.UnitBytes != 2448 || chd.LogicalBytes != cd.LogicalFrames * 2448 ||
                chd.Metadata.Count == 0 || capturedProofs.Length == 0 ||
                capturedOutputs.Length != cd.Members.OfType<RomCdBinMember>().Count() ||
                capturedDescriptors.Length != cd.Members.OfType<RomCdStoredMember>().Count() ||
                capturedOutputs.Any(output => output.Selector is not RomOutputSelector.Member member || !cd.ContainsOutput(member) ||
                    !Same(output.Identity, cd.Members[member.Ordinal].Identity)) ||
                capturedDescriptors.Any(descriptor => descriptor.Ordinal >= cd.Members.Count ||
                    cd.Members[descriptor.Ordinal] is not RomCdStoredMember member ||
                    descriptor.Sha256 != member.Identity.Sha256 || descriptor.Size != member.Identity.Size))
                throw new ArgumentException("A CD representation requires every exact BIN output and retained descriptor.");
        }
        foreach (var proof in capturedProofs)
        {
            if (proof.ArtifactSha256 != artifactSha256 || proof.ArtifactSize != artifactSize || proof.ObservedParametersSha256 != container.Fingerprint ||
                (container is RomContainerFacts.Chd) != (proof.Decoder is RomDecoderIdentity.Native) ||
                proof.Outputs.Count != capturedOutputs.Length || proof.Outputs.Any(measured =>
                    !capturedOutputs.Any(output => output.Selector == measured.Selector && output.Identity == measured.Identity)) ||
                (container is RomContainerFacts.Chd chd && (proof.ComputedRawSha1 != chd.StoredRawSha1 ||
                    proof.ComputedOverallSha1 != chd.StoredOverallSha1 ||
                    manifest is RomReconstructionManifest.WholeFile && capturedOutputs[0].Identity.Sha1 != proof.ComputedRawSha1)))
                throw new ArgumentException("A retained measurement must match every exact artifact, layout and output fact.");
        }
        if (container is RomContainerFacts.Chd header && (artifactSize < 124 ||
            header.Metadata.Sum(entry => (long)entry.Payload.Length + 16) + 124 > artifactSize))
            throw new ArgumentException("CHD header and metadata must fit the retained artifact.");
        if (capturedBoots.Any(inspection => inspection is null || manifest is not RomReconstructionManifest.CdTracks cd ||
                !capturedProofs.Any(proof => proof.Id == inspection.VerificationId && proof.Decoder is RomDecoderIdentity.Native) ||
                !RomCdBootInspection.Fits(cd, inspection.Boot)) ||
            capturedBoots.Select(inspection => inspection.Id).Distinct().Count() != capturedBoots.Length ||
            capturedBoots.Select(inspection => (inspection.InspectorBuild, inspection.Boot.Profile)).Distinct().Count() != capturedBoots.Length)
            throw new ArgumentException("CD boot inspections must bind retained native proof and exact first-track geometry.");
        if (capturedCreations.Any(creation => creation is null || container is RomContainerFacts.Identity ||
                creation.ParametersSha256 != container.Fingerprint || (creation.NativeEncoder is not null) != (container is RomContainerFacts.Chd) ||
                !capturedProofs.Any(proof => proof.Id == creation.VerificationId) || creation.Sources.Count != capturedOutputs.Length ||
                creation.Sources.Any(source => !capturedOutputs.Any(output => output.Id == source.TargetOutputId) ||
                    source.VerificationId == creation.VerificationId && (source.RepresentationId != id || source.FileId != fileId ||
                        source.ArtifactSha256 != artifactSha256 || source.ArtifactSize != artifactSize ||
                        !capturedOutputs.Any(output => output.Id == source.OutputId && output.Identity == capturedOutputs.Single(target => target.Id == source.TargetOutputId).Identity) ||
                        creation.ComparedAt <= capturedProofs.Single(proof => proof.Id == creation.VerificationId).ObservedAt)) ||
                (manifest is RomReconstructionManifest.CdTracks) != (creation.Cd is not null) ||
                creation.Cd is { } encodedCd && !capturedBoots.Any(inspection => inspection.Id == encodedCd.BootInspectionId)) ||
            capturedCreations.Select(creation => creation.Id).Distinct().Count() != capturedCreations.Length)
            throw new ArgumentException("Creation provenance must describe every exact measured encoded output.");
        Creations = Array.AsReadOnly(capturedCreations);
        CdBootInspections = Array.AsReadOnly(capturedBoots);
        Id = id;
        FileId = fileId;
        ArtifactSha256 = artifactSha256;
        ArtifactSize = artifactSize;
        Container = container;
        Manifest = manifest;
        Outputs = Array.AsReadOnly(capturedOutputs);
        Verifications = Array.AsReadOnly(capturedProofs);
        Descriptors = Array.AsReadOnly(capturedDescriptors);
    }
    public int Id { get; }
    public int FileId { get; }
    public Sha256 ArtifactSha256 { get; }
    public long ArtifactSize { get; }
    public RomContainerFacts Container { get; }
    public RomReconstructionManifest Manifest { get; }
    public IReadOnlyList<RomRepresentationOutput> Outputs { get; }
    public IReadOnlyList<RomRepresentationVerification> Verifications { get; }
    public IReadOnlyList<RomRetainedDescriptor> Descriptors { get; }
    public IReadOnlyList<RomRepresentationCreation> Creations { get; }
    public IReadOnlyList<RomCdBootInspection> CdBootInspections { get; }

    private static bool Same(RomLogicalIdentity left, RomCdFileIdentity right) => left.Size == right.Size && left.Sha256 == right.Sha256 &&
        left.Sha1 == right.Sha1 && left.Md5 == right.Md5 && left.Crc32 == right.Crc32;
}

internal static class RomFactLabels
{
    internal static void Validate(string value, int length)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > length || value.Any(char.IsControl))
            throw new ArgumentException("Fact provenance requires a bounded nonblank label.");
    }
}
