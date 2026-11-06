using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Content;

public sealed class MediaReconciliationPlannerTests
{
    private static readonly RomDecoderIdentity.Managed Resident = new("reconciliation-managed/current");
    private static readonly RomDecoderIdentity.Managed Previous = new("reconciliation-managed/previous");
    private static readonly RomChdDecoderBuild Native = new("linux-x64", DeliveryFacts.Short('f'), 2);
    private static MediaReconciliationPolicy Policy(ConversionMode mode, MediaRecipeRebuild rebuild) => new(
        TestMediaKinds.Production.Find("gamecube-disc")! with { ConversionMode = mode },
        facts => facts is RomContainerFacts.Rvz { Compression: 5, CompressionLevel: 19, BlockBytes: 131072 }, rebuild, (_, _) => true, null, null);
    private static MediaReconciliationPolicy CdPolicy(ConversionMode mode) => new(TestMediaKinds.Production.Find("ps1-cd-set")! with { ConversionMode = mode },
        facts => facts is RomContainerFacts.Chd { HunkBytes: 9792, UnitBytes: 2448 } chd && chd.Codecs.All(codec => codec == 0),
        MediaRecipeRebuild.MatchingParameters, (_, _) => true,
        graph => graph.Manifest is RomReconstructionManifest.CdTracks && graph.Verifications.Count > 0,
        TestMediaKinds.Production.Find("ps1-cd-set")!.ProfileVersions[0]);

    [Fact]
    public void Decide_MatchingImportedParameters_IsCanonicalWithoutInventedCreation()
    {
        var imported = Whole(2, 19, [Resident], null, null);
        var result = MediaReconciliationPlanner.Decide(Policy(ConversionMode.Automatic, MediaRecipeRebuild.MatchingParameters),
            new([imported], imported.Id, true, false), false, Capabilities(true, false, Native, Resident.Build));
        result.ShouldBeOfType<MediaReconciliationPlan.None>().Reason.ShouldBe(MediaReconciliationNoWork.Canonical);
        imported.Creations.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decide_MatchingTargetMissingResidentProof_VerifiesBeforeReleasingAnySource(bool unmeasured)
    {
        var original = DeliveryFacts.Original(1, 1, 1, 11, DeliveryFacts.Identity(8192), true).Facts;
        var target = Whole(2, 19, unmeasured ? [] : [Previous], null, null);
        var proof = target.Verifications.SingleOrDefault();
        var result = MediaReconciliationPlanner.Decide(Policy(ConversionMode.Automatic, MediaRecipeRebuild.MatchingParameters),
            new([original, target], original.Id, true, false), false, Capabilities(true, true, Native, Resident.Build));
        var verify = result.ShouldBeOfType<MediaReconciliationPlan.Verify>();
        verify.RepresentationId.ShouldBe(target.Id);
        verify.Decoder.ShouldBe(Resident);
        if (unmeasured)
            target.Verifications.ShouldBeEmpty();
        else
            target.Verifications.ShouldHaveSingleItem().ShouldBeSameAs(proof);
        original.Outputs.Single().RomId.ShouldBe(1);
        target.Outputs.Single().RomId.ShouldBe(1);
    }

    [Fact]
    public void Decide_CanonicalTargetHasResidentProof_ReleasesOnlyWithItsExactProofBinding()
    {
        var original = DeliveryFacts.Original(1, 1, 1, 11, DeliveryFacts.Identity(8192), true).Facts;
        var target = Whole(2, 19, [Previous, Resident], null, null);
        var retained = target.Verifications.ToArray();
        var result = MediaReconciliationPlanner.Decide(Policy(ConversionMode.Automatic, MediaRecipeRebuild.MatchingParameters),
            new([original, target], original.Id, true, false), false, Capabilities(true, false, Native, Resident.Build));
        var release = result.ShouldBeOfType<MediaReconciliationPlan.Release>();
        release.CanonicalRepresentationId.ShouldBe(target.Id);
        release.VerificationId.ShouldBe(retained.Single(proof => proof.Decoder == Resident).Id);
        release.Decoder.ShouldBe(Resident);
        target.Verifications.ShouldBe(retained);
        target.Outputs.Single().RomId.ShouldBe(1);
        original.Outputs.Single().RomId.ShouldBe(1);
    }

    [Theory]
    [InlineData("encoder/old")]
    [InlineData("encoder/new")]
    public void Decide_EncoderBuildOnlyChanges_KeepMatchingArtifactCanonical(string encoder)
    {
        var artifact = Whole(2, 19, [Resident], "rvz-canonical/v1", encoder);
        var capabilities = Capabilities(true, false, Native, Resident.Build);
        var result = MediaReconciliationPlanner.Decide(Policy(ConversionMode.Automatic, MediaRecipeRebuild.MatchingParameters),
            new([artifact], artifact.Id, true, false), false, capabilities);
        result.ShouldBeOfType<MediaReconciliationPlan.None>().Reason.ShouldBe(MediaReconciliationNoWork.Canonical);
        artifact.Creations.Single().EncoderBuild.ShouldBe(encoder);
        capabilities.DidNotReceive().CanEncode(Arg.Any<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("rvz-canonical/previous")]
    public void Decide_ExplicitSameParameterRebuild_RequiresCurrentRecipeCreation(string? creationRecipe)
    {
        var artifact = Whole(2, 19, [Resident], creationRecipe, creationRecipe is null ? null : "encoder/old");
        var policy = Policy(ConversionMode.Automatic, MediaRecipeRebuild.Required("Rebuild to replace the earlier encoder's output."));
        var result = MediaReconciliationPlanner.Decide(policy, new([artifact], artifact.Id, true, false), false,
            Capabilities(true, true, Native, Resident.Build));
        result.ShouldBeOfType<MediaReconciliationPlan.Encode>().Recipe.ShouldBe(policy.Recipe);
    }

    [Fact]
    public void Decide_ExplicitRebuildAlreadyCreatedUnderCurrentRecipe_DoesNotRepeatEncoding()
    {
        var artifact = Whole(2, 19, [Resident], "rvz-canonical/v1", "encoder/old");
        var policy = Policy(ConversionMode.Automatic, MediaRecipeRebuild.Required("Replace historical output."));
        MediaReconciliationPlanner.Decide(policy, new([artifact], artifact.Id, true, false), false,
            Capabilities(true, true, Native, Resident.Build)).ShouldBeOfType<MediaReconciliationPlan.None>()
            .Reason.ShouldBe(MediaReconciliationNoWork.Canonical);
    }

    [Fact]
    public void Decide_DifferentParametersDespiteCurrentRecipeWitness_Encodes()
    {
        var artifact = Whole(2, 18, [Resident], "rvz-canonical/v1", "encoder/old");
        var policy = Policy(ConversionMode.Automatic, MediaRecipeRebuild.MatchingParameters);
        MediaReconciliationPlanner.Decide(policy, new([artifact], artifact.Id, true, false), false,
            Capabilities(true, true, Native, Resident.Build)).ShouldBeOfType<MediaReconciliationPlan.Encode>();
    }

    [Fact]
    public void Decide_OnRequestIneligibleArtifactStillMissingDecoder_VerifiesIndependentlyOfConversion()
    {
        var artifact = Whole(2, 18, [Previous], null, null);
        var result = MediaReconciliationPlanner.Decide(Policy(ConversionMode.OnRequest, MediaRecipeRebuild.MatchingParameters),
            new([artifact], artifact.Id, false, false), false, Capabilities(true, false, Native, Resident.Build));
        result.ShouldBeOfType<MediaReconciliationPlan.Verify>().Decoder.ShouldBe(Resident);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decide_OnRequestWithResidentProof_EncodesOnlyWhenRequested(bool requested)
    {
        var artifact = Whole(2, 18, [Resident], null, null);
        var result = MediaReconciliationPlanner.Decide(Policy(ConversionMode.OnRequest, MediaRecipeRebuild.MatchingParameters),
            new([artifact], artifact.Id, true, false), requested, Capabilities(true, true, Native, Resident.Build));
        if (requested)
            result.ShouldBeOfType<MediaReconciliationPlan.Encode>();
        else
            result.ShouldBeOfType<MediaReconciliationPlan.None>().Reason.ShouldBe(MediaReconciliationNoWork.ConversionNotRequested);
    }

    [Fact]
    public void Decide_CurrentEligibilityRefusesEncoding_DoesNoWork()
    {
        var artifact = Whole(2, 18, [Resident], null, null);
        MediaReconciliationPlanner.Decide(Policy(ConversionMode.Automatic, MediaRecipeRebuild.MatchingParameters),
            new([artifact], artifact.Id, false, false), true, Capabilities(true, true, Native, Resident.Build))
            .ShouldBeOfType<MediaReconciliationPlan.None>().Reason.ShouldBe(MediaReconciliationNoWork.Ineligible);
    }

    [Fact]
    public void Decide_DecodeCapabilityInvalidated_DoesNotReleaseEvenWithStoredCurrentProof()
    {
        var canonical = Whole(2, 19, [Resident], null, null);
        var other = Whole(3, 18, [Resident], null, null);
        MediaReconciliationPlanner.Decide(Policy(ConversionMode.Automatic, MediaRecipeRebuild.MatchingParameters),
            new([canonical, other], other.Id, true, false), true, Capabilities(false, true, Native, Resident.Build))
            .ShouldBeOfType<MediaReconciliationPlan.Unavailable>().Reason.ShouldBe(MediaReconciliationUnavailable.Decoder);
    }

    [Fact]
    public void Decide_MissingEncoderForNoncanonicalArtifact_DefersEncoding()
    {
        var artifact = Whole(2, 18, [Resident], null, null);
        MediaReconciliationPlanner.Decide(Policy(ConversionMode.Automatic, MediaRecipeRebuild.MatchingParameters),
            new([artifact], artifact.Id, true, false), true, Capabilities(true, false, Native, Resident.Build))
            .ShouldBeOfType<MediaReconciliationPlan.Unavailable>().Reason.ShouldBe(MediaReconciliationUnavailable.Encoder);
    }

    [Theory]
    [InlineData("rid")]
    [InlineData("pin")]
    [InlineData("recipe")]
    public void Decide_CdDecoderTupleChanged_VerifiesEveryOutputWithoutChangingOlderRows(string change)
    {
        var old = new RomChdDecoderBuild("linux-x64", DeliveryFacts.Short('f'), 1);
        var current = change switch
        {
            "rid" => new RomChdDecoderBuild("linux-arm64", old.UpstreamCommit, old.RecipeVersion),
            "pin" => new RomChdDecoderBuild(old.Rid, DeliveryFacts.Short('e'), old.RecipeVersion),
            _ => new RomChdDecoderBuild(old.Rid, old.UpstreamCommit, old.RecipeVersion + 1)
        };
        var archive = Cd(false, [new RomDecoderIdentity.Native(old)]);
        var retained = archive.Verifications.Single();
        var outputs = archive.Outputs.ToArray();
        var result = MediaReconciliationPlanner.Decide(CdPolicy(ConversionMode.Automatic), new([archive], null, false, false), false,
            Capabilities(true, false, current, Resident.Build));
        var verify = result.ShouldBeOfType<MediaReconciliationPlan.Verify>();
        verify.RepresentationId.ShouldBe(archive.Id);
        verify.Decoder.ShouldBe(new RomDecoderIdentity.Native(current));
        archive.Verifications.ShouldHaveSingleItem().ShouldBeSameAs(retained);
        archive.Outputs.ShouldBe(outputs);
        outputs.Select(output => output.Selector).Distinct().Count().ShouldBe(2);
        outputs.ShouldAllBe(output => output.RomId == null);
        retained.Outputs.Count.ShouldBe(2);
    }

    [Fact]
    public void Decide_CdCurrentAndPreviousDecoderRows_PreservesBothWithoutEncoding()
    {
        var old = new RomDecoderIdentity.Native(new("linux-x64", DeliveryFacts.Short('f'), 1));
        var archive = Cd(true, [old, new RomDecoderIdentity.Native(Native)]);
        var retained = archive.Verifications.ToArray();
        MediaReconciliationPlanner.Decide(CdPolicy(ConversionMode.Automatic), new([archive], archive.Id, true, false), false,
            Capabilities(true, false, Native, Resident.Build)).ShouldBeOfType<MediaReconciliationPlan.None>()
            .Reason.ShouldBe(MediaReconciliationNoWork.Canonical);
        archive.Verifications.ShouldBe(retained);
        archive.Outputs.Select(output => output.RomId).ShouldBe([1, 1]);
    }

    [Fact]
    public void Decide_NativeDecoderClaimedReadyWithoutBuild_Defers()
    {
        var archive = Cd(true, [new RomDecoderIdentity.Native(Native)]);
        MediaReconciliationPlanner.Decide(CdPolicy(ConversionMode.Automatic), new([archive], archive.Id, true, false), true,
            Capabilities(true, true, null, Resident.Build)).ShouldBeOfType<MediaReconciliationPlan.Unavailable>()
            .Reason.ShouldBe(MediaReconciliationUnavailable.Decoder);
    }

    [Fact]
    public void Decide_CdDecoderNotQualifiedForKind_DefersWithoutClaimingEncoding()
    {
        var archive = Cd(true, [new RomDecoderIdentity.Native(Native)]);
        var capabilities = Capabilities(true, true, Native, Resident.Build);
        capabilities.Qualifies(CdPolicy(ConversionMode.Automatic).Kind.Kind, Native.Rid).Returns(false);
        MediaReconciliationPlanner.Decide(CdPolicy(ConversionMode.Automatic), new([archive], archive.Id, true, false), true, capabilities)
            .ShouldBeOfType<MediaReconciliationPlan.Unavailable>().Reason.ShouldBe(MediaReconciliationUnavailable.Decoder);
    }

    [Theory]
    [InlineData(ConversionMode.OnRequest, false)]
    [InlineData(ConversionMode.OnRequest, true)]
    [InlineData(ConversionMode.Automatic, false)]
    [InlineData(ConversionMode.Automatic, true)]
    public void Decide_EligibleOriginalCdInputs_UseTheSameConversionDecision(ConversionMode mode, bool requested)
    {
        var original = DeliveryFacts.Original(1, 1, 1, 11, DeliveryFacts.Identity(128 * 2352), true).Facts;
        var result = MediaReconciliationPlanner.Decide(CdPolicy(mode), new([original], null, true, false), requested,
            Capabilities(true, true, Native, Resident.Build));
        if (mode == ConversionMode.Automatic || requested)
            result.ShouldBeOfType<MediaReconciliationPlan.Encode>().Recipe.Value.ShouldBe("chd-ps1-canonical/v1");
        else
            result.ShouldBeOfType<MediaReconciliationPlan.None>().Reason.ShouldBe(MediaReconciliationNoWork.ConversionNotRequested);
    }

    [Fact]
    public void Decide_SeveralCanonicalArtifacts_PrefersResidentProofThenCurrentPreference()
    {
        var missing = Whole(2, 19, [Previous], null, null);
        var measured = Whole(3, 19, [Resident], null, null);
        var alsoMeasured = Whole(4, 19, [Resident], null, null);
        var policy = Policy(ConversionMode.Automatic, MediaRecipeRebuild.MatchingParameters);
        var first = MediaReconciliationPlanner.Decide(policy, new([missing, measured], missing.Id, true, false), true,
            Capabilities(true, true, Native, Resident.Build)).ShouldBeOfType<MediaReconciliationPlan.Release>();
        first.CanonicalRepresentationId.ShouldBe(measured.Id);
        var preferred = MediaReconciliationPlanner.Decide(policy, new([measured, alsoMeasured], alsoMeasured.Id, true, false), true,
            Capabilities(true, true, Native, Resident.Build)).ShouldBeOfType<MediaReconciliationPlan.Release>();
        preferred.CanonicalRepresentationId.ShouldBe(alsoMeasured.Id);
    }

    [Fact]
    public void Decide_NoOwnedTargetHoldings_StillEncodesFromSeparatelyVerifiedOriginalDependencies()
    {
        var result = MediaReconciliationPlanner.Decide(CdPolicy(ConversionMode.Automatic), new([], null, true, false), true, Capabilities(true, true, Native, Resident.Build));
        result.ShouldBeOfType<MediaReconciliationPlan.Encode>().Recipe.Value.ShouldBe("chd-ps1-canonical/v1");
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing-preference")]
    public void State_InvalidHoldings_RefusesBeforePlanning(string condition)
    {
        var artifact = Whole(2, 19, [Resident], null, null);
        IReadOnlyList<RomRepresentationFacts> representations = condition switch
        { "duplicate" => [artifact, artifact], _ => [artifact] };
        Should.Throw<ArgumentException>(() => new MediaReconciliationState(representations,
            condition == "missing-preference" ? 99 : null, true, false));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" reason")]
    [InlineData("reason\n")]
    public void Rebuild_ReasonMissingOrUnbounded_Refuses(string reason) =>
        Should.Throw<ArgumentException>(() => MediaRecipeRebuild.Required(reason));

    private static IMediaHostCapabilities Capabilities(bool decode, bool encode, RomChdDecoderBuild? native, string managed)
    {
        var capabilities = Substitute.For<IMediaHostCapabilities>();
        capabilities.CanDecode(Arg.Any<string>()).Returns(decode);
        capabilities.Qualifies(Arg.Any<string>(), Arg.Any<string>()).Returns(decode);
        capabilities.CanEncode(Arg.Any<string>()).Returns(encode);
        capabilities.Decoder(RomRepresentationEncoding.Chd).Returns(native);
        capabilities.ManagedDecoderBuild.Returns(managed);
        return capabilities;
    }
    private static RomRepresentationFacts Whole(int id, int level, IReadOnlyList<RomDecoderIdentity> decoders,
        string? creationRecipe, string? encoderBuild)
    {
        var identity = DeliveryFacts.Identity(8192);
        var container = new RomContainerFacts.Rvz(0x01000000, 1, 5, level, 131072, false, identity.Size);
        var output = new RomRepresentationOutput(id * 10, 1, RomOutputSelector.Whole.Instance, identity);
        var hash = Sha256.Parse(id.ToString("x64", System.Globalization.CultureInfo.InvariantCulture));
        var proofs = Measurements(hash, 4096, container, [output], decoders);
        RomRepresentationCreation[] creations = creationRecipe is null ? [] :
        [new(Guid.NewGuid(), proofs[0].Id, RecipeId.Parse(creationRecipe), encoderBuild!, DeliveryFacts.ObservedAt,
            container.Fingerprint!.Value, null, [new(output.Id, 90, 900, Guid.NewGuid(), 90, identity.Sha256, identity.Size)])];
        return new(id, id, hash, 4096, container, RomReconstructionManifest.WholeFile.Instance, [output], proofs, [], creations);
    }
    private static RomRepresentationFacts Cd(bool owned, IReadOnlyList<RomDecoderIdentity> decoders)
    {
        const int frames = 128;
        var identity = DeliveryFacts.Identity(frames * 2352);
        var bin = new RomCdFileIdentity(identity.Size, identity.Sha256, identity.Sha1, identity.Md5, identity.Crc32);
        var cue = new RomCdFileIdentity(98, DeliveryFacts.Hash('d'), DeliveryFacts.Short('d'), DeliveryFacts.Md5Value('d'), Crc32.FromUInt32(2));
        var manifest = new RomReconstructionManifest.CdTracks(frames * 2,
            [new(1, 0, frames, RomCdTrackKind.Mode2Raw), new(2, frames, frames, RomCdTrackKind.Mode2Raw)],
            [new RomCdBinMember("first.bin", bin, bin, [1]), new RomCdBinMember("second.bin", bin, bin, [2]),
                new RomCdStoredMember("disc.cue", cue, RomCdStoredRole.Cue)]);
        var container = new RomContainerFacts.Chd(5, frames * 2 * 2448, 4 * 2448, 2448, 64, [0, 0, 0, 0], false,
            DeliveryFacts.Short('e'), DeliveryFacts.Short('f'),
            [new(0x43485432, 1, "TRACK:1 TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:128 PREGAP:0 PGTYPE:MODE2_RAW PGSUB:NONE POSTGAP:0"u8),
                new(0x43485432, 1, "TRACK:2 TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:128 PREGAP:0 PGTYPE:MODE2_RAW PGSUB:NONE POSTGAP:0"u8)]);
        RomRepresentationOutput[] outputs = [new(91, owned ? 1 : null, new RomOutputSelector.Member(0), identity),
            new(92, owned ? 1 : null, new RomOutputSelector.Member(1), identity)];
        return new(42, 9, DeliveryFacts.Hash('b'), 4096, container, manifest, outputs,
            Measurements(DeliveryFacts.Hash('b'), 4096, container, outputs, decoders), [new(2, 10, cue.Sha256, cue.Size)]);
    }
    private static RomRepresentationVerification[] Measurements(Sha256 artifact, long bytes, RomContainerFacts container,
        IReadOnlyList<RomRepresentationOutput> outputs, IReadOnlyList<RomDecoderIdentity> decoders) => decoders.Select(decoder =>
            new RomRepresentationVerification(Guid.NewGuid(), artifact, bytes, decoder, "reconciliation-fixture/v1", "independent-verifier",
                DeliveryFacts.ObservedAt, outputs.Select(output => new RomMeasuredOutput(output.Selector, output.Identity)).ToArray(), container,
                (container as RomContainerFacts.Chd)?.StoredRawSha1, (container as RomContainerFacts.Chd)?.StoredOverallSha1)).ToArray();
}
