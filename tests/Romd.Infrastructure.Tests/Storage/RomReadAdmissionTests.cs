using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Each production entry's read admission, decided before the reader opens storage.</summary>
public sealed class RomReadAdmissionTests
{
    private static readonly RomLogicalIdentity Logical = MediaFactFixtures.Identity(new byte[] { 1, 2, 3, 4 });
    private static readonly Sha256 Artifact = Sha256.Parse(new string('b', 64));
    private const string ManagedBuild = "fixture-rvz/v1";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Identity_CompleteMeasurementRequired_AdmitsOnlyMeasuredLogicalBytes(bool measured)
    {
        var content = Content(RomContainerFacts.Identity.Instance, Logical.Sha256, Logical.Size, measured);
        var decision = Admission(RomRepresentationEncoding.Identity).Original(content);
        if (measured)
        {
            decision.ShouldNotBeNull().Access.ShouldBe(RomArtifactAccess.RandomAccess);
            decision.ChdEvidence.ShouldBeNull();
        }
        else
            decision.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Identity_ContradictoryArtifact_CannotBecomeAContentDescriptor(bool sizeOnly)
    {
        Should.Throw<ArgumentException>(() => Content(RomContainerFacts.Identity.Instance,
            sizeOnly ? Logical.Sha256 : Artifact, sizeOnly ? Logical.Size + 1 : Logical.Size, false))
            .Message.ShouldBe("A whole-file representation requires one exact output.");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Rvz_ExactMeasurementAndCurrentAuthority_AdmitsOriginalAndRechecksStoredBinding(bool measured, bool approved)
    {
        var content = Content(MediaFactFixtures.Rvz(Logical.Size, 1, 5, 19, 131072, false), Artifact, 3, measured);
        var authority = Substitute.For<IRomChdDeliveryAdmission>();
        if (measured && approved)
            authority.AssessManaged(content.Preferred).Returns(content.Preferred.BindManaged(ManagedBuild));
        var admission = Admission(RomRepresentationEncoding.Rvz, authority);
        var decision = admission.Original(content);
        bool admitted = measured && approved;
        (decision is not null).ShouldBe(admitted);
        var binding = measured ? content.Preferred.BindManaged(ManagedBuild) : new RomManagedReadBinding(7, 11, Guid.NewGuid(), ManagedBuild);
        var target = new RomDeliveryTarget(content.RomId, content.FileId, RomDeliveryFormat.Rvz, "disc.rvz", Artifact, 3,
            Logical.Sha256, Logical.Size, Artifact, 3, RomRepresentationEncoding.Rvz, ReadBinding: binding);
        var recheck = admission.StoredTarget.ShouldNotBeNull();
        recheck(target, content).ShouldBe(admitted);
        recheck(target with { ReadBinding = binding with { OutputId = binding.OutputId + 1 } }, content).ShouldBeFalse();
        recheck(target with { ReadBinding = binding with { MeasurementId = Guid.NewGuid() } }, content).ShouldBeFalse();
        authority.AssessManaged(content.Preferred).Returns((RomManagedReadBinding?)null);
        recheck(target, content).ShouldBeFalse();
    }

    [Fact]
    public void Chd_ServedEvidenceForCurrentArtifact_AdmitsSynchronousRawWithThatEvidence()
    {
        var fixture = ChdDeliveryFixture.Create();
        var delivery = Delivery(fixture);

        var decision = Admission(RomRepresentationEncoding.Chd, delivery).Original(fixture.Descriptor);

        decision.ShouldNotBeNull().Access.ShouldBe(RomArtifactAccess.SynchronousRaw);
        decision.ChdEvidence.ShouldBeSameAs(fixture.Artifact.ChdEvidence.Single());
        delivery.Received(1).Assess(fixture.Artifact.ChdEvidence.Single());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unapproved")]
    [InlineData("different-native")]
    public void Chd_ServingAuthorityMissingOrRefuses_RefusesOriginal(string refusal)
    {
        var fixture = ChdDeliveryFixture.Create();
        var delivery = Substitute.For<IRomChdDeliveryAdmission>();
        if (refusal == "different-native")
            delivery.Assess(fixture.Artifact.ChdEvidence.Single()).Returns(new RomChdDeliveryAssessment(new RomChdDecoderBuild(
                fixture.Evidence.Decoder.Rid, fixture.Evidence.Decoder.UpstreamCommit, 2)));

        Admission(RomRepresentationEncoding.Chd, refusal == "missing" ? null : delivery).Original(fixture.Descriptor).ShouldBeNull();
    }

    [Fact]
    public void Chd_EvidenceOnlyForAnotherArtifact_RefusesWithoutAssessing()
    {
        var fixture = ChdDeliveryFixture.Create();
        var delivery = Delivery(fixture);
        var old = fixture.Artifact;
        var unmeasured = new RomRepresentationFacts(8, 3, Artifact, old.ArtifactSize, old.Container, old.Facts.Manifest,
            [new(12, fixture.Descriptor.RomId, RomOutputSelector.Whole.Instance, fixture.Descriptor.Identity)], [], []);
        var current = new RomContentDescriptor(fixture.Descriptor.RomId, 12, fixture.Descriptor.Identity, "disc.iso",
            [new(unmeasured, 12, true), new(old.Facts, old.OutputId, false, old.ArtifactMd5, old.ArtifactSha1)], [], []);

        Admission(RomRepresentationEncoding.Chd, delivery).Original(current).ShouldBeNull();
        delivery.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public void Chd_StoredTarget_IsRecheckedThroughTheServingAuthorityOnEveryCall()
    {
        var fixture = ChdDeliveryFixture.Create();
        var delivery = Delivery(fixture);
        var recheck = Admission(RomRepresentationEncoding.Chd, delivery).StoredTarget.ShouldNotBeNull();

        recheck(fixture.Target(RomDeliveryFormat.Chd), fixture.Descriptor).ShouldBeTrue();
        delivery.Assess(fixture.Artifact.ChdEvidence.Single()).Returns((RomChdDeliveryAssessment?)null);
        recheck(fixture.Target(RomDeliveryFormat.Chd), fixture.Descriptor).ShouldBeFalse();
    }

    private static RomContentDescriptor Content(RomContainerFacts container, Sha256 artifact, long artifactBytes, bool measured)
    {
        RomRepresentationVerification[] proofs = measured ? [MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), artifact, artifactBytes, Logical, container,
            new RomDecoderIdentity.Managed(ManagedBuild), "complete-output/v1", "fixture-verifier", DateTimeOffset.UnixEpoch, null, null)] : [];
        var facts = new RomRepresentationFacts(7, 2, artifact, artifactBytes, container, RomReconstructionManifest.WholeFile.Instance,
            [new(11, 1, RomOutputSelector.Whole.Instance, Logical)], proofs, []);
        return new(1, 11, Logical, "disc.iso", [new(facts, 11, true)], [], []);
    }

    private static RomReadAdmission Admission(RomRepresentationEncoding encoding, IRomChdDeliveryAdmission? delivery = null) =>
        RomEncodingComposition.Entries.Single(entry => entry.Descriptor.Encoding == encoding)
            .ReadAdmission(new RomReadAdmissionDependencies(delivery));

    private static IRomChdDeliveryAdmission Delivery(ChdDeliveryFixture fixture)
    {
        var delivery = Substitute.For<IRomChdDeliveryAdmission>();
        delivery.Assess(fixture.Artifact.ChdEvidence.Single()).Returns(new RomChdDeliveryAssessment(fixture.Evidence.Decoder));
        return delivery;
    }
}
