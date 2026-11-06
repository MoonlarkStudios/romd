using System.Text.Json;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Content;

public sealed class RomDeliveryPlannerTests
{
    private static readonly RomLogicalIdentity Identity = DeliveryFacts.Identity(500);
    private static MediaKindResolution Ps2Dvd => MediaKindResolution.Of(TestMediaKinds.Production.Find("ps2-dvd")!);
    private static DeliveryAdmission Admission() => new(DeliveryFacts.ManagedBuild, DeliveryFacts.Decoder());
    private static RomContentDescriptor Content(RomLogicalIdentity identity, params RomContentArtifact[] artifacts) =>
        new(1, artifacts.Single(artifact => artifact.IsPreferred).OutputId, identity, "disc.iso", artifacts, [], []);
    private static RomContentArtifact Rvz(RomLogicalIdentity identity, int id, uint discType, bool preferred) =>
        DeliveryFacts.Rvz(1, id, id, id, identity, DeliveryFacts.Hash(id == 2 ? 'b' : 'c'), 100 - id, discType, preferred, DeliveryFacts.Md5Value('b'), DeliveryFacts.Short('b'));
    private static RomContentDescriptor Chd() => DeliveryFacts.ChdContent(1, 2);

    [Theory]
    [InlineData("gc", 1u)]
    [InlineData("wii", 2u)]
    public void Plan_VerifiedRvz_SeparatesRawAndLogicalMetadata(string platform, uint discType)
    {
        var artifact = DeliveryFacts.Rvz(1, 2, 2, 2, Identity, DeliveryFacts.Hash('b'), 100, discType, true, DeliveryFacts.Md5Value('b'), DeliveryFacts.Short('b'));
        var content = Content(Identity, artifact);
        var kind = MediaKindResolver.Resolve(TestMediaKinds.Production, content, [platform]);
        var options = RomDeliveryPlanner.Plan(content, "folder/disc.rvz", TestDeliveryPolicies.Production, kind, Admission());
        var original = options.Single(option => option.Format == RomDeliveryFormat.Original);
        original.Target!.Filename.ShouldBe("disc.iso");
        original.Target.Sha256.ShouldBe(Identity.Sha256);
        original.Target.SizeBytes.ShouldBe(500);
        original.Target.Md5.ShouldBe(Identity.Md5);
        original.Target.Sha1.ShouldBe(Identity.Sha1);
        original.RuntimeApplicability.ShouldBeEmpty();
        var rvz = options.Single(option => option.Format == RomDeliveryFormat.Rvz);
        rvz.Target!.Filename.ShouldBe("disc.rvz");
        rvz.Target.Sha256.ShouldBe(artifact.ArtifactSha256);
        rvz.Target.SizeBytes.ShouldBe(100);
        rvz.Target.Md5.ShouldBe(artifact.ArtifactMd5);
        rvz.Target.Sha1.ShouldBe(artifact.ArtifactSha1);
        rvz.ProfileVersion.ShouldBe("rvz-import/v1");
        rvz.RuntimeApplicability.ShouldBe(["dolphin"]);
        rvz.Target.ReadBinding.ShouldBe(artifact.BindManaged(DeliveryFacts.ManagedBuild));
        original.Target.ReadBinding.ShouldBe(rvz.Target.ReadBinding);
        options.ShouldAllBe(option => RomDeliveryTargets.IsValid(option.Target!));
    }

    [Fact]
    public void Plan_OriginalPreferredWithVerifiedAlternates_SelectsByPreferenceThenArtifactHash()
    {
        var original = DeliveryFacts.Original(1, 1, 1, 1, Identity, true);
        var low = Rvz(Identity, 2, 1, false);
        var high = Rvz(Identity, 3, 1, false);
        var first = Content(Identity, original, high, low);
        RomDeliveryPlanner.Plan(first, "disc.iso", TestDeliveryPolicies.WithoutRuntimes, chdAdmission: Admission())
            .Single(option => option.Format == RomDeliveryFormat.Rvz).Target!.FileId.ShouldBe(2);
        var preferred = Content(Identity, DeliveryFacts.Select(original, false, original.ArtifactMd5, original.ArtifactSha1), low,
            DeliveryFacts.Select(high, true, high.ArtifactMd5, high.ArtifactSha1));
        var selected = RomDeliveryPlanner.Plan(preferred, "disc.iso", TestDeliveryPolicies.WithoutRuntimes, chdAdmission: Admission())
            .Single(option => option.Format == RomDeliveryFormat.Rvz);
        selected.Target!.FileId.ShouldBe(3);
        selected.RuntimeApplicability.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("gc")]
    [InlineData("wii")]
    [InlineData("psx")]
    public void Plan_UnmeasuredAlternate_DoesNotPromisePreparationOrStoredBytes(string platform)
    {
        var original = DeliveryFacts.Original(1, 1, 1, 1, Identity, true);
        var content = Content(Identity, original, DeliveryFacts.WithoutProof(Rvz(Identity, 2, 1, false)));
        var kind = MediaKindResolver.Resolve(TestMediaKinds.Production, content, [platform]);
        var options = RomDeliveryPlanner.Plan(content, "disc.iso", TestDeliveryPolicies.WithoutRuntimes, chdAdmission: Admission());
        options[0].State.ShouldBe(RomDeliveryState.Available);
        var rvz = options.Single(option => option.Format == RomDeliveryFormat.Rvz);
        rvz.State.ShouldBe(RomDeliveryState.Unsupported);
        rvz.Target.ShouldBeNull();
        rvz.ProfileVersion.ShouldBe("unknown");
        kind.Outcome.ShouldBe(MediaKindOutcome.Fallback);
        var resolvedOptions = RomDeliveryPlanner.Plan(content, "disc.iso", TestDeliveryPolicies.WithoutRuntimes, kind, Admission());
        resolvedOptions.Select(option => option.Format).ShouldBe([RomDeliveryFormat.Original, RomDeliveryFormat.Rvz]);
        resolvedOptions[1].State.ShouldBe(RomDeliveryState.Unsupported);
        resolvedOptions[1].Target.ShouldBeNull();
    }

    [Theory]
    [InlineData("snes", "game.sfc")]
    [InlineData("gc", "disc.iso")]
    [InlineData("ps2", "disc.iso")]
    [InlineData(null, "game.bin")]
    public void Plan_NoEncodedArtifact_OffersOnlyMeasuredOriginal(string? platform, string filename)
    {
        var content = DeliveryFacts.OriginalContent(1, 1, 500);
        var options = RomDeliveryPlanner.Plan(content, filename, TestDeliveryPolicies.Production,
            MediaKindResolver.Resolve(TestMediaKinds.Production, content, [platform]), Admission());
        options.ShouldHaveSingleItem().Format.ShouldBe(RomDeliveryFormat.Original);
        options[0].State.ShouldBe(RomDeliveryState.Available);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plan_RvzWithoutCurrentManagedDecoder_RefusesBothDecodedAndStoredBytes(bool missing)
    {
        var content = DeliveryFacts.RvzContent(1, 2, 500, 100);
        IRomChdDeliveryAdmission? admission = missing ? null : new DeliveryAdmission("another-managed-build", null);
        var options = RomDeliveryPlanner.Plan(content, "disc.rvz", TestDeliveryPolicies.Production, chdAdmission: admission);
        options.ShouldAllBe(option => option.State == RomDeliveryState.Unsupported && option.Target == null && option.RuntimeApplicability.Count == 0);
    }

    [Fact]
    public void Plan_VerifiedRvz_TakesRuntimeApplicabilityFromItsResolvedKind()
    {
        var policy = new RomDeliveryPolicy(TestDeliveryPolicies.Formats, TestMediaKinds.WithRuntimes(kind => kind.Kind == "gamecube-disc" ? ["test-only-runtime"] : []));
        var content = DeliveryFacts.RvzContent(1, 2, 500, 100);
        RomDeliveryOption RvzOption(string platform) => RomDeliveryPlanner.Plan(content, "disc.rvz", policy,
            MediaKindResolver.Resolve(policy.Kinds, content, [platform]), Admission()).Single(option => option.Format == RomDeliveryFormat.Rvz);
        RvzOption("gc").RuntimeApplicability.ShouldBe(["test-only-runtime"]);
        RvzOption("wii").RuntimeApplicability.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(".gcm", "disc.gcm")]
    [InlineData(null, "disc.rvz")]
    public void Plan_StoredRvz_NamesOriginalByRegisteredExtension(string? extension, string expected)
    {
        var kinds = extension is null ? TestMediaKinds.Descriptors.Where(kind => kind.StoredEncoding != RomRepresentationEncoding.Rvz) :
            TestMediaKinds.Descriptors.Select(kind => kind.StoredEncoding == RomRepresentationEncoding.Rvz ? kind with { OriginalExtension = extension } : kind);
        var policy = new RomDeliveryPolicy(TestDeliveryPolicies.Formats, new(kinds));
        var content = DeliveryFacts.RvzContent(1, 2, 500, 100);
        RomDeliveryPlanner.Plan(content, "disc.rvz", policy, chdAdmission: Admission())[0].Target!.Filename.ShouldBe(expected);
    }

    [Fact]
    public void ToContract_CompleteLogicalIdentityAndUnknownPhysicalHashes_ReportsOnlyEstablishedMetadata()
    {
        var original = DeliveryFacts.Original(1, 1, 1, 1, Identity, true);
        var rvz = Rvz(Identity, 2, 1, false);
        var unavailable = RomDeliveryPlanner.Plan(Content(Identity, original, DeliveryFacts.WithoutProof(rvz)), "disc.iso", TestDeliveryPolicies.WithoutRuntimes,
            chdAdmission: Admission());
        using var originalJson = Json(unavailable[0]);
        originalJson.RootElement.GetProperty("md5").GetString().ShouldBe(Identity.Md5.ToString());
        originalJson.RootElement.GetProperty("sha1").GetString().ShouldBe(Identity.Sha1.ToString());
        using var missing = Json(unavailable[1]);
        foreach (string field in new[] { "sha256", "sizeBytes", "filename", "md5", "sha1" })
            missing.RootElement.TryGetProperty(field, out _).ShouldBeFalse();
        missing.RootElement.GetProperty("label").GetString().ShouldBe("RVZ");
        missing.RootElement.GetProperty("fileExtension").GetString().ShouldBe(".rvz");
        var known = RomDeliveryPlanner.Plan(Content(Identity, original, DeliveryFacts.Select(rvz, false, null, null)), "disc.iso", TestDeliveryPolicies.WithoutRuntimes,
            chdAdmission: Admission()).Single(option => option.Format == RomDeliveryFormat.Rvz);
        using var raw = Json(known);
        raw.RootElement.GetProperty("state").GetString().ShouldBe("available");
        raw.RootElement.GetProperty("sha256").GetString().ShouldBe(rvz.ArtifactSha256.ToString());
        raw.RootElement.TryGetProperty("md5", out _).ShouldBeFalse();
        raw.RootElement.TryGetProperty("sha1", out _).ShouldBeFalse();
    }

    [Fact]
    public void Plan_ChdWithExactProof_SeparatesArtifactAndOriginalIdentity()
    {
        var content = Chd();
        var artifact = content.Preferred;
        var evidence = artifact.ChdEvidence.Single();
        var options = RomDeliveryPlanner.Plan(content, "folder/disc.chd", TestDeliveryPolicies.Production, Ps2Dvd, Admission());
        var original = options[0];
        original.Target!.Filename.ShouldBe("disc.iso");
        original.Target.Sha256.ShouldBe(content.ContentSha256);
        original.Target.SizeBytes.ShouldBe(8192);
        original.Target.Md5.ShouldBe(content.ContentMd5);
        original.Target.Sha1.ShouldBe(content.ContentSha1);
        original.RuntimeApplicability.ShouldBeEmpty();
        var chd = options.Single(option => option.Format == RomDeliveryFormat.Chd);
        chd.Target!.Filename.ShouldBe("disc.chd");
        chd.Target.Sha256.ShouldBe(artifact.ArtifactSha256);
        chd.Target.SizeBytes.ShouldBe(4096);
        chd.Target.Md5.ShouldBe(artifact.ArtifactMd5);
        chd.Target.Sha1.ShouldBe(artifact.ArtifactSha1);
        chd.Target.ContentSha256.ShouldBe(content.ContentSha256);
        chd.Target.ContentSizeBytes.ShouldBe(8192);
        chd.Target.ReadBinding.ShouldBe(evidence.Bind());
        original.Target.ReadBinding.ShouldBe(evidence.Bind());
        chd.ProfileVersion.ShouldBe("chd-observed/v1");
        chd.RuntimeApplicability.ShouldBe(["pcsx2"]);
        foreach (var option in options)
            evidence.AdmitsTarget(option.Target!, content, artifact, Admission()).ShouldBeTrue();
        using var contract = Json(chd);
        contract.RootElement.GetProperty("format").GetString().ShouldBe("chd");
        contract.RootElement.GetProperty("sha1").GetString().ShouldBe(artifact.ArtifactSha1.ToString());
        contract.RootElement.TryGetProperty("readBinding", out _).ShouldBeFalse();
        contract.RootElement.TryGetProperty("decoder", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("ps2", "psx")]
    [InlineData("psx")]
    [InlineData(null)]
    [InlineData(null, "ps2")]
    public void Plan_VerifiedChdWithoutUniqueKind_OffersVerifiedBytesWithoutRuntimeClaim(params string?[] platforms)
    {
        var content = Chd();
        var kind = MediaKindResolver.Resolve(TestMediaKinds.Production, content, platforms ?? [null]);
        kind.Unique.ShouldBeNull();
        var options = RomDeliveryPlanner.Plan(content, "disc.chd", TestDeliveryPolicies.Production, kind, Admission());
        options.Select(option => (option.Format, option.State, option.RuntimeApplicability.Count)).ShouldBe(
            [(RomDeliveryFormat.Original, RomDeliveryState.Available, 0), (RomDeliveryFormat.Chd, RomDeliveryState.Available, 0)]);
        options.ShouldAllBe(option => option.Target!.ReadBinding == content.Preferred.ChdEvidence.Single().Bind());
    }

    [Fact]
    public void Plan_NoncanonicalCapturedParameters_RemainReadableThroughAdmittedDecoder()
    {
        var identity = DeliveryFacts.Identity(8192);
        var artifact = DeliveryFacts.Chd(1, 2, 7, 11, identity, DeliveryFacts.Hash('b'), 4096, DeliveryFacts.Decoder(), 16384, RomChdCodecs.Zlib, true,
            DeliveryFacts.Md5Value('b'), DeliveryFacts.Short('b'));
        var options = RomDeliveryPlanner.Plan(Content(identity, artifact), "disc.chd", TestDeliveryPolicies.Production, Ps2Dvd, Admission());
        options.ShouldAllBe(option => option.State == RomDeliveryState.Available);
        options[1].Target!.ReadBinding.ShouldBe(artifact.ChdEvidence.Single().Bind());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("refused")]
    [InlineData("different-native-build")]
    [InlineData("no-retained-proof")]
    public void Plan_ChdWithoutExactRetainedNativeAuthority_FailsClosed(string rejection)
    {
        var content = Chd();
        if (rejection == "no-retained-proof")
            content = Content(content.Identity, DeliveryFacts.WithoutProof(content.Preferred));
        IRomChdDeliveryAdmission? admission = rejection switch
        { "missing" => null, "refused" => new DeliveryAdmission(DeliveryFacts.ManagedBuild, null), "different-native-build" => new DeliveryAdmission(DeliveryFacts.ManagedBuild, DeliveryFacts.Decoder(2)), _ => Admission() };
        var options = RomDeliveryPlanner.Plan(content, "disc.chd", TestDeliveryPolicies.Production, Ps2Dvd, admission);
        options.ShouldAllBe(option => option.State == RomDeliveryState.Unsupported && option.Target == null && option.RuntimeApplicability.Count == 0);
    }

    [Theory]
    [InlineData("measurement")]
    [InlineData("output")]
    [InlineData("representation")]
    [InlineData("decoder")]
    [InlineData("rom")]
    [InlineData("file")]
    [InlineData("artifact-hash")]
    [InlineData("content-hash")]
    [InlineData("logical-hashes")]
    [InlineData("revoked")]
    public void AdmitsTarget_RechecksExactRetainedIdsScalarsAndCurrentAuthority(string mismatch)
    {
        var content = Chd();
        var artifact = content.Preferred;
        var evidence = artifact.ChdEvidence.Single();
        var admission = Admission();
        var target = RomDeliveryPlanner.Plan(content, "disc.chd", TestDeliveryPolicies.Production, Ps2Dvd, admission)[1].Target!;
        evidence.AdmitsTarget(target, content, artifact, admission).ShouldBeTrue();
        var binding = (RomChdDeliveryBinding)target.ReadBinding!;
        target = mismatch switch
        {
            "measurement" => target with { ReadBinding = binding with { MeasurementId = Guid.NewGuid() } },
            "output" => target with { ReadBinding = binding with { OutputId = 99 } },
            "representation" => target with { ReadBinding = binding with { RepresentationId = 99 } },
            "decoder" => target with { ReadBinding = binding with { Decoder = DeliveryFacts.Decoder(2) } },
            "rom" => target with { RomId = 99 },
            "file" => target with { FileId = 99 },
            "artifact-hash" => target with { ArtifactSha256 = DeliveryFacts.Hash('f') },
            "content-hash" => target with { ContentSha256 = DeliveryFacts.Hash('f') },
            "logical-hashes" => target with { Md5 = content.ContentMd5, Sha1 = content.ContentSha1 },
            _ => target
        };
        if (mismatch == "revoked")
            admission.NativeAssessment = null;
        evidence.AdmitsTarget(target, content, artifact, admission).ShouldBeFalse();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("logical-sha")]
    [InlineData("logical-md5")]
    [InlineData("preferred-output")]
    [InlineData("artifact-sha")]
    public void ContentGraph_ContradictoryIdentity_RefusesAtConstruction(string mismatch)
    {
        var content = Chd();
        var artifact = content.Preferred;
        var facts = artifact.Facts;
        Should.Throw<ArgumentException>(() =>
        {
            if (mismatch == "artifact-sha")
                _ = new RomRepresentationFacts(facts.Id, facts.FileId, DeliveryFacts.Hash('c'), facts.ArtifactSize, facts.Container, facts.Manifest,
                    facts.Outputs, facts.Verifications, facts.Descriptors);
            else
                _ = new RomContentDescriptor(mismatch == "owner" ? 99 : 1, mismatch == "preferred-output" ? 99 : content.OutputId,
                    mismatch == "logical-sha" ? new(8192, DeliveryFacts.Hash('c'), content.ContentSha1, content.ContentMd5, content.ContentCrc32) :
                    mismatch == "logical-md5" ? new(8192, content.ContentSha256, content.ContentSha1, DeliveryFacts.Md5Value('c'), content.ContentCrc32) : content.Identity,
                    "disc.iso", [artifact], [], []);
        });
    }

    [Theory]
    [InlineData(RomDeliveryFormat.Original)]
    [InlineData(RomDeliveryFormat.Chd)]
    public void IsValid_ChdTargetWithoutBindingOrWithConfusedDeliveredIdentity_Refuses(RomDeliveryFormat format)
    {
        var content = Chd();
        var target = RomDeliveryPlanner.Plan(content, "disc.chd", TestDeliveryPolicies.Production, Ps2Dvd, Admission())
            .Single(option => option.Format == format).Target!;
        RomDeliveryTargets.IsValid(target).ShouldBeTrue();
        RomDeliveryTargets.IsValid(target with { ReadBinding = null }).ShouldBeFalse();
        RomDeliveryTargets.IsValid(target with { Sha256 = format == RomDeliveryFormat.Original ? content.ArtifactSha256 : content.ContentSha256 }).ShouldBeFalse();
        RomDeliveryTargets.IsValid(target with { SizeBytes = format == RomDeliveryFormat.Original ? 4096 : 8192 }).ShouldBeFalse();
    }

    [Theory]
    [InlineData("null-md5")]
    [InlineData("empty-md5")]
    [InlineData("null-sha1")]
    [InlineData("empty-sha1")]
    public void Plan_ChdWithoutPhysicalHashes_PreservesLogicalOriginalButRefusesRawTarget(string missing)
    {
        var content = Chd();
        var artifact = content.Preferred;
        var incomplete = DeliveryFacts.Select(artifact, true, missing.EndsWith("md5", StringComparison.Ordinal) ? missing.StartsWith("null", StringComparison.Ordinal) ? null : default(Md5) : artifact.ArtifactMd5,
            missing.EndsWith("sha1", StringComparison.Ordinal) ? missing.StartsWith("null", StringComparison.Ordinal) ? null : default(Sha1) : artifact.ArtifactSha1);
        var options = RomDeliveryPlanner.Plan(Content(content.Identity, incomplete), "disc.chd", TestDeliveryPolicies.Production, Ps2Dvd, Admission());
        options[0].State.ShouldBe(RomDeliveryState.Available);
        var originalTarget = options[0].Target.ShouldNotBeNull();
        originalTarget.Md5.ShouldBe(content.ContentMd5);
        originalTarget.Sha1.ShouldBe(content.ContentSha1);
        options[1].State.ShouldBe(RomDeliveryState.Unsupported);
        options[1].Target.ShouldBeNull();
        var raw = RomDeliveryPlanner.Plan(content, "disc.chd", TestDeliveryPolicies.Production, Ps2Dvd, Admission())[1].Target! with
        { Md5 = incomplete.ArtifactMd5, Sha1 = incomplete.ArtifactSha1 };
        RomDeliveryTargets.IsValid(raw).ShouldBeFalse();
        artifact.ChdEvidence.Single().AdmitsTarget(raw, content, artifact, Admission()).ShouldBeFalse();
    }

    [Fact]
    public void Plan_ChdAlternates_SelectByPreferenceThenHashAndCompletePhysicalMetadata()
    {
        var content = Chd();
        var identity = content.Identity;
        var low = DeliveryFacts.Select(content.Preferred, false, content.Preferred.ArtifactMd5, content.Preferred.ArtifactSha1);
        var high = DeliveryFacts.Chd(1, 3, 8, 12, identity, DeliveryFacts.Hash('c'), 4096, DeliveryFacts.Decoder(), 4096, RomChdCodecs.Zlib, false, DeliveryFacts.Md5Value('b'), DeliveryFacts.Short('b'));
        var original = DeliveryFacts.Original(1, 1, 1, 1, identity, true);
        RomDeliveryOption Stored(RomContentDescriptor descriptor) => RomDeliveryPlanner.Plan(descriptor, "disc.iso", TestDeliveryPolicies.Production, Ps2Dvd, Admission())[1];
        Stored(Content(identity, original, high, low)).Target!.FileId.ShouldBe(2);
        Stored(Content(identity, original, low, high)).Target!.FileId.ShouldBe(2);
        var preferredHigh = Content(identity, DeliveryFacts.Select(original, false, original.ArtifactMd5, original.ArtifactSha1), low,
            DeliveryFacts.Select(high, true, high.ArtifactMd5, high.ArtifactSha1));
        Stored(preferredHigh).Target!.FileId.ShouldBe(3);
        var missingPreferred = Content(identity, DeliveryFacts.Select(low, true, null, low.ArtifactSha1), high);
        var options = RomDeliveryPlanner.Plan(missingPreferred, "disc.chd", TestDeliveryPolicies.Production, Ps2Dvd, Admission());
        options[0].Target!.FileId.ShouldBe(2);
        options[1].Target!.FileId.ShouldBe(3);
        var retainedOriginal = RomDeliveryPlanner.Plan(Content(identity, original, low), "disc.iso", TestDeliveryPolicies.Production, Ps2Dvd, Admission());
        var retainedTarget = retainedOriginal[0].Target.ShouldNotBeNull();
        retainedTarget.Encoding.ShouldBe(RomRepresentationEncoding.Identity);
        retainedTarget.ReadBinding.ShouldBeNull();
    }

    [Theory]
    [InlineData(RomDeliveryFormat.Original, false)]
    [InlineData(RomDeliveryFormat.Original, true)]
    [InlineData(RomDeliveryFormat.Rvz, false)]
    [InlineData(RomDeliveryFormat.Rvz, true)]
    public void IsValid_OriginalOrRvzWithOptionalDeliveredHashes_PreservesExactDecoderBinding(RomDeliveryFormat format, bool empty)
    {
        var content = DeliveryFacts.RvzContent(1, 2, 500, 100);
        var target = RomDeliveryPlanner.Plan(content, "disc.rvz", TestDeliveryPolicies.Production, chdAdmission: Admission()).Single(option => option.Format == format).Target!;
        target = target with { Md5 = empty ? default(Md5) : null, Sha1 = empty ? default(Sha1) : null };
        RomDeliveryTargets.IsValid(target).ShouldBeTrue();
        target.ReadBinding.ShouldBeOfType<RomManagedReadBinding>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RVZ")]
    [InlineData("iso")]
    [InlineData("original ")]
    [InlineData(" rvz")]
    public void Parse_InvalidWireId_ReturnsValidationError(string? wire) => RomDeliveryFormats.Parse(wire).IsError.ShouldBeTrue();
    [Theory]
    [InlineData("original", RomDeliveryFormat.Original)]
    [InlineData("rvz", RomDeliveryFormat.Rvz)]
    [InlineData("chd", RomDeliveryFormat.Chd)]
    public void Parse_ExactWireId_RoundTrips(string wire, RomDeliveryFormat format)
    { RomDeliveryFormats.Parse(wire).Value.ShouldBe(format); RomDeliveryFormats.ToWire(format).ShouldBe(wire); }
    [Fact]
    public void Parse_FormatValuesRemainStable()
    {
        ((int)RomDeliveryFormat.Original).ShouldBe(0);
        ((int)RomDeliveryFormat.Rvz).ShouldBe(1);
        ((int)RomDeliveryFormat.Chd).ShouldBe(2);
        RomDeliveryFormats.Parse("CHD").IsError.ShouldBeTrue();
        RomDeliveryFormats.Parse("chd ").IsError.ShouldBeTrue();
    }
    private static JsonDocument Json(RomDeliveryOption option) => JsonDocument.Parse(JsonSerializer.Serialize(option.ToContract(TestDeliveryPolicies.Formats), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}
