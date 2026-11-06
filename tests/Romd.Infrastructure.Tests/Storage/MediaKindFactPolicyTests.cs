using NSubstitute;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Jobs;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class MediaKindFactPolicyTests
{
    [Fact]
    public void Decide_SecondWholeFileChdKind_UsesItsEligibilityWithoutPs2Facts()
    {
        var adapter = new OtherChdPolicy();
        var registration = RomMediaPolicyComposition.Registrations.Single(kind => kind.Kind == "ps2-dvd") with
        {
            Kind = "other-chd-disc",
            PlatformKeys = ["other"],
            NativeRuntimes = [],
            WholeFilePolicy = _ => adapter
        };
        var composition = RomMediaPolicyComposition.Create([RomMediaPolicyComposition.Registrations.Single(kind => kind.Kind == "ps2-dvd"), registration]);
        var content = Content(Dvd(), [new(Guid.NewGuid(), Identity, "structural/v1", "inspector", Observed, new byte[32])]);
        content.References.ShouldBeEmpty();
        content.Inspections.ShouldAllBe(row => !(row.Layout is RomPs2BootLayoutFacts));
        var capabilities = Substitute.For<IMediaHostCapabilities>();
        capabilities.CanDecode("other-chd-disc").Returns(true);
        capabilities.CanEncode("other-chd-disc").Returns(true);
        var decoder = ((RomDecoderIdentity.Native)content.Preferred.Facts.Verifications.Single().Decoder).Build;
        capabilities.Decoder(RomRepresentationEncoding.Chd).Returns(decoder);
        capabilities.Qualifies("other-chd-disc", decoder.Rid).Returns(true);
        capabilities.ManagedDecoderBuild.Returns("decoder");
        var profile = Substitute.For<IMediaReconciliationMediaProfile>();
        profile.LimitsFingerprint.Returns(Hash('e'));
        profile.Encoder("other-chd-disc").Returns(new MediaReconciliationEncoder("other-encoder", RomIsoDiscKind.GameCube));
        var decision = new MediaReconciliationDecision(composition.Kinds, composition.Reconciliation, capabilities, profile);

        var result = decision.Decide(Guid.NewGuid(), new(new MediaReconciliationSubject.LogicalRom(1), 1, true, false),
            new MediaReconciliationFacts.Logical(content, ["other"]));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeOfType<MediaReconciliationDirective.Encode>().Capture.KindId.ShouldBe("other-chd-disc");
        adapter.EligibilityCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData("gamecube-disc", "rvz", "nintendo", "psp")]
    [InlineData("gamecube-disc", "rvz", "nintendo", "ps2")]
    [InlineData("gamecube-disc", "original", "nintendo", "psp")]
    [InlineData("ps2-dvd", "chd", null, "nintendo")]
    [InlineData("ps2-dvd", "chd", null, "psp")]
    [InlineData("psp-umd", "chd", "psp", "ps2")]
    [InlineData("psp-umd", "original", "psp", "nintendo")]
    public void MatchesFacts_ForeignLayoutObservation_ExcludesTheKindWithoutNamingIt(string kind, string container, string? own, string foreign)
    {
        var registration = RomMediaPolicyComposition.Registrations.Single(candidate => candidate.Kind == kind);
        var policy = registration.WholeFilePolicy!(registration.PlatformKeys[0]);
        RomContainerFacts? facts = container switch { "rvz" => Rvz(), "chd" => Dvd(), _ => null };
        var claimed = own is null ? Inspect("structural/v1", new byte[32], null) : Inspect("structural/v1", own);
        var contradicted = Inspect("foreign/v1", claimed.Prefix.ToArray(), Layout(foreign));

        policy.MatchesFacts(Content(facts, [claimed])).ShouldBeTrue();
        policy.MatchesFacts(Content(facts, [claimed, contradicted])).ShouldBeFalse();
    }

    [Fact]
    public void RvzDiscFamily_EachFamily_RecognizesOnlyItsOwnHeaderLayoutAndContainer()
    {
        (RvzDiscFamily Family, RomIsoDiscKind Kind, bool Wii, uint ContainerType)[] families =
            [(RvzDiscFamily.GameCube, RomIsoDiscKind.GameCube, false, 1), (RvzDiscFamily.Wii, RomIsoDiscKind.Wii, true, 2)];
        foreach (var (family, kind, wii, containerType) in families)
        {
            family.DiscKind.ShouldBe(kind);
            family.ContainerDiscType.ShouldBe(containerType);
            foreach (bool observedWii in new[] { false, true })
            {
                var observed = Nintendo(observedWii);
                family.MatchesHeader(observed.Prefix).ShouldBe(observedWii == wii, $"{kind} header for wii={observedWii}");
                family.MatchesLayout((RomNintendoLayoutFacts)observed.Layout!).ShouldBe(observedWii == wii, $"{kind} layout for wii={observedWii}");
            }
            new RvzMediaTypePolicy(family, "platform").DiscKind.ShouldBe(kind);
        }
    }

    [Fact]
    public void WholeEncoding_EveryKind_ObservesOnlyItsOwnSourceLayoutFamily()
    {
        var composition = RomMediaPolicyComposition.Create();
        var families = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gamecube-disc"] = "nintendo",
            ["wii-disc"] = "nintendo",
            ["ps2-dvd"] = "ps2",
            ["psp-umd"] = "psp"
        };
        // A newly registered whole-file kind must declare its family here, so its encoder rule is never inferred.
        composition.WholeEncoding.Keys.ShouldBe(families.Keys, ignoreOrder: true);
        foreach (var (kind, family) in families)
        {
            var profile = composition.WholeEncoding[kind];
            profile.ObservesEncodingLayout(Inspect("unstructured/v1", new byte[32], null)).ShouldBeFalse(kind);
            foreach (string observed in new[] { "nintendo", "ps2", "psp" })
                profile.ObservesEncodingLayout(Inspect(observed + "/v1", observed)).ShouldBe(observed == family, $"{kind} observing {observed}");
        }
    }

    [Fact]
    public void Plan_ContradictoryNintendoInspection_PreservesAdmittedRvzWithoutRuntimeClaim()
    {
        var content = Content(Rvz(), [Nintendo(true)]);
        var composition = RomMediaPolicyComposition.Create();
        var resolution = MediaKindResolver.Resolve(composition.Kinds, content, ["gc"]);
        resolution.Outcome.ShouldBe(MediaKindOutcome.Fallback);
        var admission = Substitute.For<IRomChdDeliveryAdmission>();
        admission.AssessManaged(content.Preferred).Returns(content.Preferred.BindManaged("decoder"));

        var options = RomDeliveryPlanner.Plan(content, "disc.rvz", composition.DeliveryPolicy, resolution, admission);

        options.Select(option => option.Format).ShouldBe([RomDeliveryFormat.Original, RomDeliveryFormat.Rvz]);
        options.ShouldAllBe(option => option.State == RomDeliveryState.Available && option.RuntimeApplicability.Count == 0);
        options[1].Target!.ReadBinding.ShouldBe(content.Preferred.BindManaged("decoder"));
        options[1].Target!.Sha256.ShouldBe(content.Preferred.ArtifactSha256);
    }

    [Theory]
    [InlineData("accepted", true)]
    [InlineData("missing-reference", false)]
    [InlineData("conflicting-references", false)]
    [InlineData("missing-boot", false)]
    [InlineData("cd-reference", false)]
    public void IsEncodingEligible_RegisteredPs2Policy_RequiresCurrentCompatibleBootAndReferenceFacts(string facts, bool expected)
    {
        RomDiscMediaReferenceRecord[] references = facts switch
        {
            "missing-reference" => [],
            "conflicting-references" => [Reference(), Reference(RomDiscReferenceMedia.Dvd9)],
            "cd-reference" => [Reference(RomDiscReferenceMedia.Cd)],
            _ => [Reference()]
        };
        var content = Content(observations: facts == "missing-boot" ? [] : [Boot()], references: references);
        var policy = RomMediaPolicyComposition.Create().Reconciliation["ps2-dvd"];

        policy.IsEncodingEligible(content, CancellationToken.None).ShouldBe(expected);
    }

    private sealed class OtherChdPolicy : IRomMediaTypePolicy, IRomMediaEncodingProfile
    {
        public int EligibilityCalls { get; private set; }
        public IRomMediaEncodingProfile EncodingProfile => this;
        public bool ObservesEncodingLayout(RomLogicalInspection inspection) => false;
        public RomIsoDiscKind DiscKind => RomIsoDiscKind.GameCube;
        public string PlatformKey => "other";
        public RomRepresentationEncoding StoredEncoding => RomRepresentationEncoding.Chd;
        public string RecipeVersion => RomMediaPolicyComposition.Registrations.Single(kind => kind.Kind == "ps2-dvd").Recipe.Id;
        public RomRepresentationProfileClass Classify(RomContainerFacts container) => RomRepresentationProfileClass.NonCanonical;
        public bool MatchesFacts(RomContentDescriptor content) => content.Preferred.Container is RomContainerFacts.Chd;
        public bool IsEncodingEligible(RomContentDescriptor content, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            EligibilityCalls++;
            return true;
        }
        public long GetMaximumOutputBytes(long logicalBytes, long maxLogicalBytes, CancellationToken ct = default) => maxLogicalBytes;
    }

    private static Sha256 Hash(char value) => Sha256.Parse(new string(value, 64));
    private static Sha1 Short(char value) => Sha1.Parse(new string(value, 40));
    private static readonly RomLogicalIdentity Identity = new(131072, Hash('a'), Short('b'), Md5.Parse(new string('c', 32)), Crc32.FromUInt32(0));
    private static readonly DateTimeOffset Observed = DateTimeOffset.UnixEpoch.AddTicks(123);
    private static readonly IRomMediaTypePolicy[] Policies = [new RvzMediaTypePolicy(RvzDiscFamily.GameCube,"gc"),
        new RvzMediaTypePolicy(RvzDiscFamily.Wii,"wii"),new ChdDvdMediaTypePolicy("ps2")];
    private static MediaKindCatalog Kinds => new(Policies.Select(policy => new MediaKindDescriptor(policy.DiscKind.ToString().ToLowerInvariant(),
        [policy.PlatformKey], policy.EncodingProfile.StoredEncoding, policy.EncodingProfile.RecipeVersion, MediaManifestType.WholeFile, ConversionMode.Automatic, ["runtime"], ".iso", [policy.EncodingProfile.StoredEncoding.ToString().ToLowerInvariant() + "/observed/v1"], policy.MatchesFacts))
        .Append(new("opaque", [], RomRepresentationEncoding.Identity, null, MediaManifestType.WholeFile, ConversionMode.OnRequest, [], null, ["identity/v1"])));
    private static RomLogicalInspection Nintendo(bool wii) => new(Guid.NewGuid(), Identity, "structural/v1", "inspector", Observed,
        Convert.FromHexString(wii ? new string('0', 48) + "5d1c9ea300000000" : new string('0', 56) + "c2339f3d"), new RomNintendoLayoutFacts(wii ? 1 : 0, wii ? 1 : 0, 0));
    private static RomLogicalInspection Boot() => new(Guid.NewGuid(), Identity, "root/v1", "inspector", Observed, new byte[32],
        new RomPs2BootLayoutFacts(131072, 64, 16, 24, 864, 25, 80, 26, 256, @"cdrom0:\SLUS_123.45;1", @"cdrom0:\SLUS_123.45;1", "SLUS_123.45", 0x100008, 1,
            RomPs2BootLayoutFacts.ProfileVersion, "inspector"));
    private static RomLayoutObservation Layout(string family) => family switch
    {
        "nintendo" => new RomNintendoLayoutFacts(0, 0, 0),
        "ps2" => Boot().Layout!,
        "psp" => new RomPspLayoutFacts(Identity.Size, 64, "ULUS12345", "1.00"),
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };
    /// <summary>A GameCube header prefix satisfies the Nintendo family; other families accept any prefix.</summary>
    private static RomLogicalInspection Inspect(string profile, string family) =>
        Inspect(profile, family == "nintendo" ? Nintendo(false).Prefix.ToArray() : new byte[32], Layout(family));
    private static RomLogicalInspection Inspect(string profile, byte[] prefix, RomLayoutObservation? layout) =>
        new(Guid.NewGuid(), Identity, profile, "inspector", Observed, prefix, layout);
    private static RomDiscMediaReferenceRecord Reference(RomDiscReferenceMedia media = RomDiscReferenceMedia.Dvd5,
        RomDiscReferenceSystem system = RomDiscReferenceSystem.PlayStation2, int files = 1) =>
        new(Guid.NewGuid(), system, media, files, Identity.Size, Identity.Sha1, Identity.Md5, Identity.Crc32, 1, Hash('e'), "trusted-source", "parser/v1", "accepted-import", Observed);
    private static RomContentDescriptor Content(RomContainerFacts? container = null, RomLogicalInspection[]? observations = null,
        RomDiscMediaReferenceRecord[]? references = null)
    {
        container ??= RomContainerFacts.Identity.Instance;
        bool original = container is RomContainerFacts.Identity;
        var artifact = original ? Identity.Sha256 : Hash('d');
        long bytes = original ? Identity.Size : 512;
        RomDecoderIdentity decoder = container is RomContainerFacts.Chd ? new RomDecoderIdentity.Native(new("linux-arm64", Short('f'), 2)) : new RomDecoderIdentity.Managed("decoder");
        var proof = new RomRepresentationVerification(Guid.NewGuid(), artifact, bytes, decoder, "checked/v1", "verifier", Observed,
            [new(RomOutputSelector.Whole.Instance, Identity)], container, container is RomContainerFacts.Chd chd ? chd.StoredRawSha1 : null,
            container is RomContainerFacts.Chd native ? native.StoredOverallSha1 : null);
        var facts = new RomRepresentationFacts(10, 20, artifact, bytes, container, RomReconstructionManifest.WholeFile.Instance,
            [new(1, 1, RomOutputSelector.Whole.Instance, Identity)], [proof], []);
        return new(1, 1, Identity, "disc.iso", [new(facts, 1, true, Md5.Parse(new string('d', 32)), Short('e'))], observations ?? [], references ?? []);
    }
    private static RomContainerFacts.Rvz Rvz(uint discType = 1) => new(0x1000000, discType, 5, 19, 131072, discType == 2, Identity.Size);
    private static RomContainerFacts.Chd Dvd(uint unit = 2048) => new(5, Identity.Size, 16384, unit, 8, [0, 0, 0, 0], false, Identity.Sha1, Short('c'),
        [new(0x44564420, 1, new byte[] { 0 })]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_ObservedNintendoLayout_UsesRawFactsWithoutStoredKind(bool wii)
    {
        var content = Content(observations: [Nintendo(wii)]);
        MediaKindResolver.Resolve(Kinds, content, []).Unique!.PlatformKeys.ShouldBe([wii ? "wii" : "gc"]);
        new RomMediaPolicyRegistry(Policies).ResolveObservedKind(content).ShouldBe(wii ? RomIsoDiscKind.Wii : RomIsoDiscKind.GameCube);
    }

    [Fact]
    public void Resolve_UnknownOriginalInKnownPlatform_UsesOpaqueFallback() =>
        MediaKindResolver.Resolve(Kinds, Content(), ["gc"]).Outcome.ShouldBe(MediaKindOutcome.Fallback);

    [Fact]
    public void Resolve_MeasuredRvzWithoutEncodableLogicalLayout_UsesObservedDiscType() =>
        MediaKindResolver.Resolve(Kinds, Content(Rvz()), ["gc"]).Unique!.PlatformKeys.ShouldBe(["gc"]);

    [Fact]
    public void Resolve_RvzHeaderContradictsObservedNintendoBytes_UsesFallback() =>
        MediaKindResolver.Resolve(Kinds, Content(Rvz(2), [Nintendo(false)]), []).Outcome.ShouldBe(MediaKindOutcome.Fallback);

    [Fact]
    public void Resolve_PlatformContradictsExactObservedKind_ReportsContradiction() =>
        MediaKindResolver.Resolve(Kinds, Content(Rvz(), [Nintendo(false)]), ["wii"]).Outcome.ShouldBe(MediaKindOutcome.Contradicted);

    [Fact]
    public void Resolve_MatchedDatEntriesDisagree_ReportsAmbiguity() =>
        MediaKindResolver.Resolve(Kinds, Content(Rvz(), [Nintendo(false)]), ["gc", "wii"]).Outcome.ShouldBe(MediaKindOutcome.Ambiguous);

    [Fact]
    public void Resolve_CompleteDvdChd_UsesMeasuredGeometryAndMetadataWithoutReferenceCache() =>
        MediaKindResolver.Resolve(Kinds, Content(Dvd()), ["ps2"]).Unique!.PlatformKeys.ShouldBe(["ps2"]);

    [Fact]
    public void Resolve_BootWithoutTrustedDvdReference_UsesFallback() =>
        MediaKindResolver.Resolve(Kinds, Content(observations: [Boot()]), ["ps2"]).Outcome.ShouldBe(MediaKindOutcome.Fallback);

    [Fact]
    public void Resolve_CurrentDvdReferenceAfterBoot_ChangesKindWithoutReinspection()
    {
        var observation = Boot();
        MediaKindResolver.Resolve(Kinds, Content(observations: [observation]), ["ps2"]).Outcome.ShouldBe(MediaKindOutcome.Fallback);
        MediaKindResolver.Resolve(Kinds, Content(observations: [observation], references: [Reference()]), ["ps2"]).Unique!.PlatformKeys.ShouldBe(["ps2"]);
        MediaKindResolver.Resolve(Kinds, Content(observations: [observation], references: [Reference(), Reference(RomDiscReferenceMedia.Dvd9)]), ["ps2"]).Outcome.ShouldBe(MediaKindOutcome.Fallback);
    }

    [Theory]
    [InlineData(RomDiscReferenceSystem.PlayStation1, RomDiscReferenceMedia.Dvd5, 1)]
    [InlineData(RomDiscReferenceSystem.PlayStation2, RomDiscReferenceMedia.Cd, 1)]
    [InlineData(RomDiscReferenceSystem.PlayStation2, RomDiscReferenceMedia.Dvd5, 2)]
    public void Resolve_NonSingletonOrNonDvdReference_CannotCreateDvdAuthority(RomDiscReferenceSystem system, RomDiscReferenceMedia media, int files) =>
        MediaKindResolver.Resolve(Kinds, Content(observations: [Boot()], references: [Reference(media, system, files)]), ["ps2"]).Outcome.ShouldBe(MediaKindOutcome.Fallback);

    [Fact]
    public void Plan_OpaqueDecodedArtifact_OffersAdmittedHoldingsWithoutRuntimeClaims()
    {
        var content = Content(Rvz(7));
        var kinds = Kinds;
        var kind = MediaKindResolver.Resolve(kinds, content, []);
        var formats = new RomDeliveryFormatCatalog([new(RomDeliveryFormat.Original, "Original", null), new(RomDeliveryFormat.Rvz, "RVZ", ".rvz"), new(RomDeliveryFormat.Chd, "CHD", ".chd")]);
        var options = RomDeliveryPlanner.Plan(content, "disc.rvz", new(formats, kinds), kind, new Admission());
        options.Select(option => option.Format).ShouldBe([RomDeliveryFormat.Original, RomDeliveryFormat.Rvz]);
        options.ShouldAllBe(option => option.State == RomDeliveryState.Available && option.RuntimeApplicability.Count == 0);
        options[1].Target!.Sha256.ShouldBe(content.Preferred.ArtifactSha256);
    }

    [Fact]
    public void Plan_ContradictedDvd_OffersVerifiedBytesWithoutRuntimeClaim()
    {
        var content = Content(Dvd());
        var kinds = Kinds;
        var formats = new RomDeliveryFormatCatalog([new(RomDeliveryFormat.Original, "Original", null), new(RomDeliveryFormat.Rvz, "RVZ", ".rvz"), new(RomDeliveryFormat.Chd, "CHD", ".chd")]);
        var options = RomDeliveryPlanner.Plan(content, "disc.chd", new(formats, kinds), MediaKindResolver.Resolve(kinds, content, ["psx"]), new Admission());
        options.Select(option => option.Format).ShouldBe([RomDeliveryFormat.Original, RomDeliveryFormat.Chd]);
        options.ShouldAllBe(option => option.State == RomDeliveryState.Available && option.RuntimeApplicability.Count == 0);
    }

    [Theory]
    [InlineData("gamecube-disc", 1u, 19, true)]
    [InlineData("gamecube-disc", 1u, 18, false)]
    [InlineData("wii-disc", 2u, 19, true)]
    [InlineData("wii-disc", 2u, 18, false)]
    public void Reconciliation_NintendoRecipe_UsesCapturedParameters(string kind, uint discType, int level, bool canonical)
    {
        var facts = Content(new RomContainerFacts.Rvz(0x1000000, discType, 5, level, 131072, discType == 2, Identity.Size)).Preferred.Facts;
        var policy = RomMediaPolicyComposition.Create().Reconciliation[kind];
        policy.IsCanonical(facts).ShouldBe(canonical);
        facts.Creations.ShouldBeEmpty("matching imported parameters need no invented encoder provenance");
    }

    [Theory]
    [InlineData(RomChdCodecs.Zstd, true)]
    [InlineData(RomChdCodecs.None, false)]
    [InlineData(RomChdCodecs.Zlib, false)]
    public void Reconciliation_DvdRecipe_UsesTheRegisteredCanonicalMatcher(uint codec, bool canonical)
    {
        var facts = Content(new RomContainerFacts.Chd(5, Identity.Size, 16384, 2048, 8, [codec, 0, 0, 0], false,
            Identity.Sha1, Short('c'), [new(0x44564420, 1, new byte[] { 0 })])).Preferred.Facts;
        RomMediaPolicyComposition.Create().Reconciliation["ps2-dvd"].IsCanonical(facts).ShouldBe(canonical);
    }

    [Fact]
    public void Reconciliation_NewRecipeExplicitlyRequiresRebuild_RejectsMatchingImportedParameters()
    {
        var facts = Content(Rvz()).Preferred.Facts;
        var registration = RomMediaPolicyComposition.Registrations.Single(kind => kind.Kind == "gamecube-disc");
        var adapter = Substitute.For<IRomMediaTypePolicy>();
        adapter.DiscKind.Returns(RomIsoDiscKind.GameCube);
        adapter.PlatformKey.Returns(registration.PlatformKeys.Single());
        adapter.EncodingProfile.StoredEncoding.Returns(registration.Recipe.Encoding);
        adapter.EncodingProfile.RecipeVersion.Returns("rvz-rebuild-test/v2");
        adapter.EncodingProfile.Classify(Arg.Any<RomContainerFacts>()).Returns(RomRepresentationProfileClass.Canonical);
        var changed = registration with
        {
            Recipe = registration.Recipe with
            {
                Id = adapter.EncodingProfile.RecipeVersion,
                Rebuild = MediaRecipeRebuild.Required("Replace output from the earlier recipe.")
            },
            WholeFilePolicy = _ => adapter
        };
        RomMediaPolicyComposition.Create().Reconciliation[registration.Kind].IsCanonical(facts).ShouldBeTrue();
        RomMediaPolicyComposition.Create([changed]).Reconciliation[registration.Kind].IsCanonical(facts).ShouldBeFalse();
        facts.Creations.ShouldBeEmpty();
    }

    private sealed class Admission : IRomChdDeliveryAdmission
    {
        public RomChdDeliveryAssessment? Assess(RomChdDeliveryEvidence evidence) => new(evidence.Decoder);
        public RomManagedReadBinding? AssessManaged(RomContentArtifact artifact) => artifact.BindManaged("decoder");
        public bool AdmitsCd(RomReconstruction.CdMember member) => false;
        public RomChdDecoderBuild? AssessCd(RomReconstruction.CdMember member) => null;
    }
}
