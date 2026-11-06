using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Infrastructure.Storage;
using Romd.Domain.Source.Rom;
using Romd.Media.PlayStation;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <remarks>Serving-policy observers use synthetic resident facts; they do not qualify a native build or emulator.</remarks>
public sealed class ChdDeliveryAdmissionTests
{
    [Fact]
    public void Assess_ReadyWithoutDeploymentAssets_RefusesMissingNativeAssets()
    {
        var fixture = ChdDeliveryFixture.Create();
        var runtime = new ChdDeploymentRuntime(new NativeToolOptions(), false);
        runtime.Verify();
        runtime.IsReady.ShouldBeTrue();
        runtime.Assets.ShouldBeNull();
        var media = RomMediaPolicyComposition.Create();
        new ChdDeliveryAdmission(runtime, new MediaHostCapabilities(runtime, media, encodes: false), media.Kinds)
            .Assess(fixture.Evidence).ShouldBeNull();
    }

    [Fact]
    public void Assess_HostThatCannotDecodeTheEvidencedKind_RefusesBeforeVerificationWithoutInvalidating()
    {
        var fixture = ChdDeliveryFixture.Create();
        var state = new RuntimeState(fixture.Native);
        var capabilities = Substitute.For<IMediaHostCapabilities>();
        capabilities.CanDecode(Arg.Any<string>()).Returns(false);
        var admission = new ChdDeliveryAdmission(() => state.Ready, () => state.Candidate, () => state.Ready = false, _ =>
        {
            state.Verifications++;
            return fixture.Native;
        }, capabilities, RomMediaPolicyComposition.Create().Kinds);

        admission.Assess(fixture.Evidence).ShouldBeNull();

        capabilities.Received().CanDecode("ps2-dvd");
        capabilities.DidNotReceive().CanEncode(Arg.Any<string>());
        state.Verifications.ShouldBe(0);
        state.Ready.ShouldBeTrue();
    }

    /// <summary>The serving decision names only the decoder that may read the bytes; runtime claims belong to the media kind.</summary>
    [Fact]
    public void Assess_DeployedTuple_AdmitsTheDecoderWithoutARuntimeClaim()
    {
        var fixture = ChdDeliveryFixture.Create();
        var state = new RuntimeState(fixture.Native);
        var assessment = Admission(fixture, state).Assess(fixture.Evidence).ShouldNotBeNull();
        assessment.ShouldBe(new RomChdDeliveryAssessment(fixture.Evidence.Decoder));
        assessment.Matches(fixture.Evidence).ShouldBeTrue();
        state.Verifications.ShouldBe(1);
        state.Ready.ShouldBeTrue();
    }

    [Theory]
    [InlineData("binary-size")]
    [InlineData("binary-hash")]
    [InlineData("build")]
    [InlineData("family")]
    [InlineData("upstream-version")]
    public void Assess_RebuiltBinaryWithTheSameTuple_StillServesTheStoredChd(string changed)
    {
        var fixture = ChdDeliveryFixture.Create();
        var rebuilt = changed switch
        {
            "binary-size" => fixture.Native with { BinaryBytes = fixture.Native.BinaryBytes + 1 },
            "binary-hash" => fixture.Native with { BinarySha256 = new string('1', 64) },
            "build" => fixture.Native with { BuildId = new string('1', 64) },
            "family" => fixture.Native with { FamilyVersion = "rebuilt-family" },
            _ => fixture.Native with { UpstreamVersion = "rebuilt-describe" }
        };
        var state = new RuntimeState(rebuilt);

        Admission(fixture, state, verify: native => native.ExpectedIdentity).Assess(fixture.Evidence)
            .ShouldNotBeNull().Decoder.ShouldBe(fixture.Evidence.Decoder);
        state.Verifications.ShouldBe(1);
        state.Ready.ShouldBeTrue();
    }

    [Theory]
    [InlineData("rid")]
    [InlineData("pin")]
    [InlineData("recipe")]
    public void Assess_StoredEvidenceOfAnotherTuple_RefusesBeforeVerificationWithoutInvalidating(string changed)
    {
        var fixture = ChdDeliveryFixture.Create();
        var deployed = Change(fixture.Native, changed);
        var state = new RuntimeState(deployed);

        Admission(fixture, state, verify: native => native.ExpectedIdentity).Assess(fixture.Evidence).ShouldBeNull();

        state.Verifications.ShouldBe(0);
        state.Ready.ShouldBeTrue();
    }

    [Theory]
    [InlineData("rid")]
    [InlineData("pin")]
    [InlineData("recipe")]
    public void Assess_VerifiedResidentOfAnotherTuple_RefusesAndInvalidates(string changed)
    {
        var fixture = ChdDeliveryFixture.Create();
        var state = new RuntimeState(fixture.Native);
        var admission = Admission(fixture, state, verify: _ => Change(fixture.Native, changed));

        admission.Assess(fixture.Evidence).ShouldBeNull();
        state.Verifications.ShouldBe(1);
        state.Ready.ShouldBeFalse();
        admission.Assess(fixture.Evidence).ShouldBeNull();
        state.Verifications.ShouldBe(1);
    }

    [Theory]
    [InlineData("representation")]
    [InlineData("measurement-identity")]
    [InlineData("parameters")]
    [InlineData("decoder")]
    public void FreshEvidence_ContradictoryCompleteFacts_RejectBeforeAdmission(string changed)
    {
        var fixture = ChdDeliveryFixture.Create();
        var facts = fixture.Artifact.Facts;
        var proof = fixture.Evidence.Measurement;
        Should.Throw<ArgumentException>(() =>
        {
            var container = (RomContainerFacts.Chd)facts.Container;
            var measured = changed == "decoder"
                ? new RomRepresentationVerification(Guid.NewGuid(), proof.ArtifactSha256, proof.ArtifactSize, new RomDecoderIdentity.Managed("foreign"),
                    proof.CheckedProfile, proof.VerifierBuild, proof.ObservedAt, proof.Outputs, container)
                : changed == "measurement-identity" ? ChdDeliveryFixture.Create(24).Evidence.Measurement : proof;
            var retained = new RomRepresentationFacts(changed == "representation" ? 0 : facts.Id, facts.FileId, facts.ArtifactSha256, facts.ArtifactSize,
                changed == "parameters" ? new RomContainerFacts.Chd(5, container.LogicalBytes, 8192, 2048, 1, container.Codecs, false,
                    container.StoredRawSha1, container.StoredOverallSha1, container.Metadata) : container,
                facts.Manifest, facts.Outputs, [measured], facts.Descriptors);
            _ = new RomChdDeliveryEvidence(retained, fixture.Artifact.OutputId, measured.Id);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FreshEvidence_MissingOutputOrMeasurement_RejectsInsteadOfBorrowingProof(bool missingOutput)
    {
        var fixture = ChdDeliveryFixture.Create();
        Should.Throw<InvalidOperationException>(() => new RomChdDeliveryEvidence(fixture.Artifact.Facts,
            missingOutput ? int.MaxValue : fixture.Artifact.OutputId, missingOutput ? fixture.Evidence.Measurement.Id : Guid.NewGuid()));
    }

    [Fact]
    public void Assess_NotReadyAndLaterInvalidation_RefusesWithoutReusingEarlierApproval()
    {
        var fixture = ChdDeliveryFixture.Create();
        var state = new RuntimeState(fixture.Native) { Ready = false };
        var admission = Admission(fixture, state);
        admission.Assess(fixture.Evidence).ShouldBeNull();
        state.Verifications.ShouldBe(0);
        state.Ready = true;
        admission.Assess(fixture.Evidence).ShouldNotBeNull();
        state.Verifications.ShouldBe(1);
        state.Ready = false;
        admission.Assess(fixture.Evidence).ShouldBeNull();
        state.Verifications.ShouldBe(1);
    }

    [Fact]
    public void Assess_InvalidatedDuringVerification_RefusesTheEarlierReadySnapshot()
    {
        var fixture = ChdDeliveryFixture.Create();
        var state = new RuntimeState(fixture.Native);
        var admission = Admission(fixture, state, verify: _ => { state.Ready = false; return fixture.Native; });
        admission.Assess(fixture.Evidence).ShouldBeNull();
        state.Verifications.ShouldBe(1);
    }

    [Fact]
    public void Assess_RealMissingNativeBuild_RefusesAndInvalidatesWithoutLoading()
    {
        var fixture = ChdDeliveryFixture.Create(rid: NativeRuntimeIdentifier.Current!);
        var state = new RuntimeState(fixture.Native);
        ChdNativeCandidateException? rejection = null;
        var admission = Admission(fixture, state, verify: native =>
        {
            try
            { return native.VerifyAndLoad(); }
            catch (ChdNativeCandidateException error) { rejection = error; throw; }
        });
        admission.Assess(fixture.Evidence).ShouldBeNull();
        rejection.ShouldNotBeNull().Rejection.Cause.ShouldBe("ManifestMissing");
        state.Ready.ShouldBeFalse();
        state.Verifications.ShouldBe(1);
        admission.Assess(fixture.Evidence).ShouldBeNull();
        state.Verifications.ShouldBe(1);
    }

    [Fact]
    public void Assess_UnexpectedVerificationError_PropagatesOriginalAndInvalidates()
    {
        var fixture = ChdDeliveryFixture.Create();
        var state = new RuntimeState(fixture.Native);
        var failure = new InvalidOperationException("test-only programming failure");
        var admission = Admission(fixture, state, verify: _ => throw failure);
        Should.Throw<InvalidOperationException>(() => admission.Assess(fixture.Evidence))
            .ShouldBeSameAs(failure);
        state.Ready.ShouldBeFalse();
        state.Verifications.ShouldBe(1);
        admission.Assess(fixture.Evidence).ShouldBeNull();
        state.Verifications.ShouldBe(1);
    }

    private static ChdNativeIdentity Change(ChdNativeIdentity native, string changed) => changed switch
    {
        "rid" => native with { Rid = native.Rid == "linux-arm64" ? "linux-x64" : "linux-arm64" },
        "pin" => native with { UpstreamCommit = new string('a', 40) },
        _ => native with { RecipeVersion = native.RecipeVersion + 1 }
    };

    private static ChdDeliveryAdmission Admission(ChdDeliveryFixture fixture, RuntimeState state,
        Func<ChdNativeCandidate, ChdNativeIdentity>? verify = null) =>
        new(() => state.Ready, () => state.Candidate, () => state.Ready = false, native =>
        {
            native.ShouldBeSameAs(state.Candidate);
            state.Verifications++;
            return verify is null ? fixture.Native : verify(native);
        });

    private sealed class RuntimeState(ChdNativeIdentity deployed)
    {
        public bool Ready { get; set; } = true;
        public ChdNativeCandidate? Candidate { get; } = new(
            Path.Combine(Path.GetTempPath(), "romd-chd-delivery-admission-" + Guid.NewGuid().ToString("N")), deployed);
        public int Verifications { get; set; }
    }
}
