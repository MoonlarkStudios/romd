using System.Buffers.Binary;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Content;

public sealed class MediaKindResolverTests
{
    private static MediaKindCatalog Kinds => TestMediaKinds.Production;
    [Theory]
    [InlineData("gc")]
    [InlineData("wii")]
    [InlineData("ps2")]
    [InlineData("snes")]
    [InlineData("psx")]
    [InlineData(null)]
    public void Resolve_OriginalWithoutStructuralObservation_UsesOpaqueFallbackRegardlessOfPlatform(string? platform)
    {
        var content = DeliveryFacts.OriginalContent(1, 1, 8192);
        MediaKindResolver.Candidates(Kinds, content).ShouldBeEmpty();
        var result = MediaKindResolver.Resolve(Kinds, content, [platform]);
        result.Outcome.ShouldBe(MediaKindOutcome.Fallback);
        result.Kind.ShouldBe(Kinds.Fallback);
        result.Unique.ShouldBeNull();
        MediaKindResolver.Resolve(Kinds, content, []).ShouldBe(result);
    }
    [Theory]
    [InlineData(1u, "gc", "gamecube-disc")]
    [InlineData(2u, "wii", "wii-disc")]
    public void Resolve_ObservedOriginal_MatchesRawPrefixAndLayoutWithPlatformAsFilter(uint discType, string platform, string kind)
    {
        var content = ObservedOriginal(discType);
        MediaKindResolver.Resolve(Kinds, content, [platform]).Unique.ShouldNotBeNull().Kind.ShouldBe(kind);
        MediaKindResolver.Resolve(Kinds, content, []).Unique.ShouldNotBeNull().Kind.ShouldBe(kind);
        MediaKindResolver.Resolve(Kinds, content, ["snes"]).Outcome.ShouldBe(MediaKindOutcome.Contradicted);
        MediaKindResolver.Resolve(Kinds, content, [platform, "snes"]).Outcome.ShouldBe(MediaKindOutcome.Ambiguous);
    }
    [Theory]
    [InlineData(1u, "gc", "gamecube-disc")]
    [InlineData(2u, "wii", "wii-disc")]
    public void Resolve_VerifiedRvz_UsesCapturedDiscFamilyRatherThanPlatformAlone(uint discType, string platform, string kind)
    {
        var identity = DeliveryFacts.Identity(8192);
        var artifact = DeliveryFacts.Rvz(1, 2, 2, 2, identity, DeliveryFacts.Hash('b'), 4096, discType, true, null, null);
        var content = new RomContentDescriptor(1, 2, identity, "disc.iso", [artifact], [], []);
        MediaKindResolver.Candidates(Kinds, content).Select(candidate => candidate.Kind).ShouldBe([kind]);
        MediaKindResolver.Resolve(Kinds, content, []).Unique!.Kind.ShouldBe(kind);
        MediaKindResolver.Resolve(Kinds, content, [platform]).Unique!.Kind.ShouldBe(kind);
        MediaKindResolver.Resolve(Kinds, content, [discType == 1 ? "wii" : "gc"]).Outcome.ShouldBe(MediaKindOutcome.Contradicted);
    }
    [Fact]
    public void Resolve_ContradictoryCapturedNintendoFamilies_ClaimsNeitherKind()
    {
        var identity = DeliveryFacts.Identity(8192);
        var gc = DeliveryFacts.Rvz(1, 2, 2, 2, identity, DeliveryFacts.Hash('b'), 4096, 1, true, null, null);
        var wii = DeliveryFacts.Rvz(1, 3, 3, 3, identity, DeliveryFacts.Hash('c'), 4096, 2, false, null, null);
        var content = new RomContentDescriptor(1, 2, identity, "disc.iso", [gc, wii], [], []);
        MediaKindResolver.Candidates(Kinds, content).ShouldBeEmpty();
        MediaKindResolver.Resolve(Kinds, content, ["gc"]).Outcome.ShouldBe(MediaKindOutcome.Fallback);
    }
    [Fact]
    public void Resolve_RvzHeaderContradictedByRawInspection_ClaimsNeitherKind()
    {
        var raw = ObservedOriginal(2);
        var artifact = DeliveryFacts.Rvz(1, 2, 2, 2, raw.Identity, DeliveryFacts.Hash('b'), 4096, 1, true, null, null);
        var content = new RomContentDescriptor(1, 2, raw.Identity, "disc.iso", [artifact], raw.Inspections, []);
        MediaKindResolver.Candidates(Kinds, content).ShouldBeEmpty();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_UnmeasuredEncodedGraph_CannotClaimKindFromHeaderOrPresentationLabel(bool chd)
    {
        var measured = chd ? DeliveryFacts.ChdContent(1, 2) : DeliveryFacts.RvzContent(1, 2, 8192, 4096);
        var artifact = DeliveryFacts.WithoutProof(measured.Preferred);
        var content = new RomContentDescriptor(1, artifact.OutputId, measured.Identity, "disc.iso", [artifact], [], []);
        MediaKindResolver.Candidates(Kinds, content).ShouldBeEmpty();
        MediaKindResolver.Resolve(Kinds, content, [chd ? "ps2" : "gc"]).Outcome.ShouldBe(MediaKindOutcome.Fallback);
        artifact.EncodingProfile.ShouldStartWith(chd ? "chd-observed/v1" : "rvz-import/v1");
    }
    [Theory]
    [InlineData]
    [InlineData("ps2")]
    [InlineData("ps2", "ps2")]
    public void Resolve_VerifiedDvdChd_WithNoPlatformOrMatchingPlatform_IsUnique(params string[] platforms)
    {
        var content = DeliveryFacts.ChdContent(1, 2);
        MediaKindResolver.Candidates(Kinds, content).Select(kind => kind.Kind).ShouldBe(["ps2-dvd"]);
        MediaKindResolver.Resolve(Kinds, content, platforms).Unique!.Kind.ShouldBe("ps2-dvd");
    }
    [Theory]
    [InlineData("ps2", "psx")]
    [InlineData("psx", "ps2")]
    [InlineData("ps2", null)]
    [InlineData("ps2", "snes")]
    [InlineData("psx", "gc")]
    public void Resolve_VerifiedChdMatchedOnDisagreeingPlatforms_IsAmbiguous(string first, string? second)
    {
        var resolution = MediaKindResolver.Resolve(Kinds, DeliveryFacts.ChdContent(1, 2), [first, second]);
        resolution.Outcome.ShouldBe(MediaKindOutcome.Ambiguous);
        resolution.NativeRuntimes(RomDeliveryFormat.Chd).ShouldBeEmpty();
    }
    [Theory]
    [InlineData("psx")]
    [InlineData("gc")]
    [InlineData("snes")]
    [InlineData(null)]
    public void Resolve_VerifiedChdOnContradictingPlatform_IsContradicted(string? platform)
    {
        var resolution = MediaKindResolver.Resolve(Kinds, DeliveryFacts.ChdContent(1, 2), [platform]);
        resolution.Outcome.ShouldBe(MediaKindOutcome.Contradicted);
        resolution.Kind.ShouldBeNull();
        resolution.NativeRuntimes(RomDeliveryFormat.Chd).ShouldBeEmpty();
    }
    [Theory]
    [InlineData]
    [InlineData("psx")]
    public void Resolve_CdMember_UsesTypedMemberLayout(params string[] platforms)
    {
        var content = DeliveryFacts.CdContent();
        MediaKindResolver.Candidates(Kinds, content).Select(kind => kind.Kind).ShouldBe(["ps1-cd-set"]);
        var result = MediaKindResolver.Resolve(Kinds, content, platforms);
        result.Unique!.Kind.ShouldBe("ps1-cd-set");
        result.NativeRuntimes(RomDeliveryFormat.Chd).ShouldBeEmpty();
        MediaKindResolver.Resolve(Kinds, content, ["ps2"]).Outcome.ShouldBe(MediaKindOutcome.Contradicted);
    }
    [Fact]
    public void Resolve_TwoObservedLayoutsClaimPlatform_FailsClosedAsAmbiguous()
    {
        var kinds = new MediaKindCatalog([.. TestMediaKinds.Descriptors, TestMediaKinds.CdTracks("ps2-cd", "ps2", "chd-cd-test/v1", TestMediaKinds.CdFacts)]);
        var cd = DeliveryFacts.CdContent();
        var dvd = DeliveryFacts.Chd(1, 3, 7, 11, cd.Identity, DeliveryFacts.Hash('c'), 4096, DeliveryFacts.Decoder(), 4096, RomChdCodecs.Zlib, false, null, null);
        var content = new RomContentDescriptor(1, cd.OutputId, cd.Identity, "data.bin", [cd.Preferred, dvd], [], []);
        MediaKindResolver.Candidates(kinds, content).Select(kind => kind.Kind).ShouldBe(["ps2-dvd", "ps1-cd-set", "ps2-cd"]);
        MediaKindResolver.Resolve(kinds, content, ["ps2"]).Outcome.ShouldBe(MediaKindOutcome.Ambiguous);
        MediaKindResolver.Resolve(kinds, content, ["psx"]).Unique!.Kind.ShouldBe("ps1-cd-set");
    }
    [Fact]
    public void Resolve_TwoRegisteredMatchersAgreeOnFacts_PlatformFiltersOtherwiseAmbiguousCandidates()
    {
        var kinds = new MediaKindCatalog([..TestMediaKinds.Descriptors,TestMediaKinds.Disc("test-dvd","test",RomRepresentationEncoding.Chd,
            "chd-dvd-test/v1",["test-runtime"],["chd-observed/v1"],TestMediaKinds.DvdFacts)]);
        var content = DeliveryFacts.ChdContent(1, 2);
        MediaKindResolver.Resolve(kinds, content, []).Outcome.ShouldBe(MediaKindOutcome.Ambiguous);
        MediaKindResolver.Resolve(kinds, content, ["ps2", "test"]).Outcome.ShouldBe(MediaKindOutcome.Ambiguous);
        MediaKindResolver.Resolve(kinds, content, ["test"]).Unique!.Kind.ShouldBe("test-dvd");
    }
    [Fact]
    public void Resolve_RetainedBootAndCurrentReferences_RecomputesWithoutPersistingPositiveOrNegativeKind()
    {
        var identity = DeliveryFacts.Identity(131072);
        var artifact = DeliveryFacts.Original(1, 1, 1, 1, identity, true);
        var boot = new RomPs2BootLayoutFacts(131072, 64, 16, 24, 200, 25, 123, 28, 896, @"cdrom0:\slus_203.12;1", @"cdrom0:\SLUS_203.12;1",
            "SLUS_203.12", 0x100000, 2, RomPs2BootLayoutFacts.ProfileVersion, "fixture-inspector");
        var observation = new RomLogicalInspection(Guid.NewGuid(), identity, "structure/v1", "fixture-inspector", DeliveryFacts.ObservedAt, new byte[32], layout: boot);
        RomContentDescriptor Content(params RomDiscMediaReferenceRecord[] references) => new(1, 1, identity, "disc.iso", [artifact], [observation], references);
        RomDiscMediaReferenceRecord Reference(RomDiscReferenceMedia media) => new(Guid.NewGuid(), RomDiscReferenceSystem.PlayStation2, media, 1, identity.Size,
            identity.Sha1, identity.Md5, identity.Crc32, 1, DeliveryFacts.Hash('d'), "synthetic-source", "parser/v1", "synthetic-local", DeliveryFacts.ObservedAt);
        MediaKindResolver.Resolve(Kinds, Content(), ["ps2"]).Outcome.ShouldBe(MediaKindOutcome.Fallback);
        var reference = Reference(RomDiscReferenceMedia.Dvd5);
        MediaKindResolver.Resolve(Kinds, Content(reference), ["ps2"]).Unique!.Kind.ShouldBe("ps2-dvd");
        MediaKindResolver.Resolve(Kinds, Content(reference, Reference(RomDiscReferenceMedia.Dvd9)), ["ps2"]).Outcome.ShouldBe(MediaKindOutcome.Fallback);
        Content().Inspections.Single().ShouldBeSameAs(observation);
    }
    [Theory]
    [InlineData("codec")]
    [InlineData("metadata-tag")]
    [InlineData("metadata-flags")]
    [InlineData("metadata-payload")]
    public void Resolve_DvdLikeContainerWithoutMatchingCapturedScalars_DoesNotBorrowThePlatformsKind(string mismatch)
    {
        var source = DeliveryFacts.ChdContent(1, 2);
        var identity = source.Identity;
        var container = new RomContainerFacts.Chd(5, 8192, 4096, 2048, 2, [mismatch == "codec" ? 0xdeadbeef : RomChdCodecs.Zlib, 0, 0, 0], false,
            identity.Sha1, DeliveryFacts.Short('c'), [new(mismatch=="metadata-tag"?0x44564421u:0x44564420u,
                mismatch=="metadata-flags"?(byte)0:(byte)1,mismatch=="metadata-payload"?new byte[]{1}:new byte[]{0})]);
        var proof = new RomRepresentationVerification(Guid.NewGuid(), DeliveryFacts.Hash('b'), 4096, new RomDecoderIdentity.Native(DeliveryFacts.Decoder()),
            "complete-output/v1", "fixture-verifier", DeliveryFacts.ObservedAt, [new(RomOutputSelector.Whole.Instance, identity)], container,
            container.StoredRawSha1, container.StoredOverallSha1);
        var facts = new RomRepresentationFacts(2, 2, DeliveryFacts.Hash('b'), 4096, container, RomReconstructionManifest.WholeFile.Instance,
            [new(2, 1, RomOutputSelector.Whole.Instance, identity)], [proof], []);
        var content = new RomContentDescriptor(1, 2, identity, "disc.iso", [new(facts, 2, true)], [], []);
        MediaKindResolver.Candidates(Kinds, content).ShouldBeEmpty();
        MediaKindResolver.Resolve(Kinds, content, ["ps2"]).Outcome.ShouldBe(MediaKindOutcome.Fallback);
    }

    [Fact]
    public void NativeRuntimes_UniqueKind_ClaimsOnlyItsOwnStoredFormat()
    {
        var ps2 = MediaKindResolver.Resolve(Kinds, DeliveryFacts.ChdContent(1, 2), ["ps2"]);
        var gc = MediaKindResolver.Resolve(Kinds, DeliveryFacts.RvzContent(1, 2, 8192, 4096), ["gc"]);
        ps2.NativeRuntimes(RomDeliveryFormat.Chd).ShouldBe(["pcsx2"]);
        ps2.NativeRuntimes(RomDeliveryFormat.Rvz).ShouldBeEmpty();
        ps2.NativeRuntimes(RomDeliveryFormat.Original).ShouldBeEmpty();
        gc.NativeRuntimes(RomDeliveryFormat.Rvz).ShouldBe(["dolphin"]);
        gc.NativeRuntimes(RomDeliveryFormat.Chd).ShouldBeEmpty();
    }
    private static RomContentDescriptor ObservedOriginal(uint discType)
    {
        var content = DeliveryFacts.OriginalContent(1, 1, 8192);
        var prefix = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(discType == 1 ? 0x1c : 0x18), discType == 1 ? 0xc2339f3dU : 0x5d1c9ea3U);
        int partitions = discType == 1 ? 0 : 1;
        var observation = new RomLogicalInspection(Guid.NewGuid(), content.Identity, "structure/v1", "fixture-inspector", DeliveryFacts.ObservedAt, prefix,
            new RomNintendoLayoutFacts(partitions, partitions, 0));
        return new(1, content.OutputId, content.Identity, "disc.iso", content.Artifacts, [observation], []);
    }
}
