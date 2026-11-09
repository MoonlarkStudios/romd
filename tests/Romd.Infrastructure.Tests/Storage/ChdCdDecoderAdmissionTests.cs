using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Jobs;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.PlayStation;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Synthetic complete serving facts; native and real-disc qualification are separate.</summary>
public sealed class ChdCdDecoderAdmissionTests
{
    private sealed record Fixture(RomRepresentationFacts Facts, ChdNativeIdentity Native,
        RomRepresentationVerification Historical, RomRepresentationVerification Current)
    {
        internal RomContentDescriptor Content => Describe(Facts);
        internal RomReconstruction.CdMember Member => (RomReconstruction.CdMember)Content.Reconstruction;
        internal Fixture WithProofs(params RomRepresentationVerification[] proofs) => this with { Facts = Copy(Facts, proofs) };
    }
    private static Fixture Create(string rid = "linux-x64")
    {
        var native = ChdDeliveryFixture.Create().Native with { Rid = rid, RecipeVersion = 3 };
        var hash = Sha256.Parse(new string('a', 64));
        var sha = Sha1.Parse(new string('b', 40));
        var first = new RomCdFileIdentity(32 * 2352, hash, sha, Md5.Parse(new string('c', 32)), Crc32.FromUInt32(1));
        var second = new RomCdFileIdentity(32 * 2352, Sha256.Parse(new string('d', 64)), Sha1.Parse(new string('e', 40)), Md5.Parse(new string('f', 32)), Crc32.FromUInt32(2));
        var cue = new RomCdFileIdentity(98, Sha256.Parse(new string('1', 64)), sha, first.Md5, Crc32.FromUInt32(3));
        var container = new RomContainerFacts.Chd(5, 64 * 2448, 39168, 2448, 4, [0x63647a73, 0x6364666c, 0, 0], false, sha, sha,
            [new(0x43485432,1,"TRACK:1 TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:32 PREGAP:0 PGTYPE:MODE2_RAW PGSUB:NONE POSTGAP:0"u8),
             new(0x43485432,1,"TRACK:2 TYPE:AUDIO SUBTYPE:NONE FRAMES:32 PREGAP:0 PGTYPE:AUDIO PGSUB:NONE POSTGAP:0"u8)]);
        var manifest = new RomReconstructionManifest.CdTracks(64, [new(1, 0, 32, RomCdTrackKind.Mode2Raw), new(2, 32, 32, RomCdTrackKind.Audio)],
            [new RomCdBinMember("data.bin", first, first, [1]), new RomCdBinMember("audio.bin", second, second, [2]), new RomCdStoredMember("disc.cue", cue, RomCdStoredRole.Cue)]);
        RomLogicalIdentity Identity(RomCdFileIdentity member) => new(member.Size, member.Sha256, member.Sha1, member.Md5, member.Crc32);
        RomMeasuredOutput[] outputs = [new(new RomOutputSelector.Member(0), Identity(first)), new(new RomOutputSelector.Member(1), Identity(second))];
        var current = new RomRepresentationVerification(Guid.NewGuid(), hash, 1024, new RomDecoderIdentity.Native(ChdBuildEvidence.Decoder(native)),
            "test-complete-cd/v1", "test-verifier", DateTimeOffset.UnixEpoch.AddTicks(43), outputs, container, sha, sha);
        var historical = new RomRepresentationVerification(Guid.NewGuid(), hash, 1024,
            new RomDecoderIdentity.Native(new("linux-x64", ChdBuildEvidence.Decoder(native).UpstreamCommit, 2)), current.CheckedProfile, current.VerifierBuild,
            DateTimeOffset.UnixEpoch.AddTicks(17), outputs, container, sha, sha);
        return new(new(42, 9, hash, 1024, container, manifest, [new(101, 1, outputs[0].Selector, outputs[0].Identity), new(102, 2, outputs[1].Selector, outputs[1].Identity)],
            [historical, current], [new(2, 10, cue.Sha256, cue.Size)]), native, historical, current);
    }
    private static RomContentDescriptor Describe(RomRepresentationFacts facts)
    {
        var output = facts.Outputs.Single(row => row.Selector == new RomOutputSelector.Member(0));
        return new(output.RomId!.Value, output.Id, output.Identity, "data.bin", [new(facts, output.Id, true)], [], []);
    }
    private static RomRepresentationFacts Copy(RomRepresentationFacts facts, IReadOnlyList<RomRepresentationVerification> proofs) =>
        new(facts.Id, facts.FileId, facts.ArtifactSha256, facts.ArtifactSize, facts.Container, facts.Manifest, facts.Outputs, proofs, facts.Descriptors);

    [Fact]
    public void Decide_RetainedCdWithUnclaimedMetadata_DoesNotSelectTheOnlyRegisteredCdKind()
    {
        var facts = Create().Facts;
        var header = (RomContainerFacts.Chd)facts.Container;
        var unknown = new RomContainerFacts.Chd(header.Version, header.LogicalBytes, header.HunkBytes, header.UnitBytes,
            header.HunkCount, header.Codecs, header.HasParent, header.StoredRawSha1, header.StoredOverallSha1,
            header.Metadata.Select(entry => new RomChdMetadataEntry(0x54455354, entry.Flags, entry.Payload)).ToArray());
        var proofs = facts.Verifications.Select(proof => new RomRepresentationVerification(proof.Id, proof.ArtifactSha256,
            proof.ArtifactSize, proof.Decoder, proof.CheckedProfile, proof.VerifierBuild, proof.ObservedAt, proof.Outputs,
            unknown, proof.ComputedRawSha1, proof.ComputedOverallSha1)).ToArray();
        var retained = new RomRepresentationFacts(facts.Id, facts.FileId, facts.ArtifactSha256, facts.ArtifactSize, unknown,
            facts.Manifest, facts.Outputs.Select(output => new RomRepresentationOutput(output.Id, null, output.Selector, output.Identity)).ToArray(),
            proofs, facts.Descriptors);
        var composition = RomMediaPolicyComposition.Create();
        var capabilities = Substitute.For<IMediaHostCapabilities>();
        var decision = new MediaReconciliationDecision(composition.Kinds,
            composition.Reconciliation, capabilities, Substitute.For<IMediaReconciliationMediaProfile>());

        var result = decision.Decide(Guid.NewGuid(), new(new MediaReconciliationSubject.Representation(retained.Id), 1, false, false),
            new MediaReconciliationFacts.Representation(retained));

        result.Value.ShouldBeOfType<MediaReconciliationDirective.Unavailable>()
            .Code.ShouldBe("cd-kind-unregistered");
        capabilities.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AssessCd_ChangedDecoder_RequiresCompleteCurrentProofAndRetainsPriorBuild(bool currentMeasured)
    {
        var fixture = Create();
        if (!currentMeasured)
            fixture = fixture.WithProofs(fixture.Historical);
        int verified = 0;
        bool ready = true;
        var admission = new ChdDeliveryAdmission(() => ready, () => new("/synthetic/cd-native", fixture.Native), () => ready = false,
            _ => { verified++; return fixture.Native; });
        var option = RomDeliveryPlanner.Plan(fixture.Content, "data.bin", RomMediaPolicyComposition.CreateDeliveryPolicy(), null, admission)[0];
        if (currentMeasured)
        {
            option.State.ShouldBe(RomDeliveryState.Available);
            option.Target!.ReadBinding.ShouldBe(fixture.Member.Bind(ChdBuildEvidence.Decoder(fixture.Native)));
            fixture.Facts.Verifications.Count.ShouldBe(2);
            ((RomDecoderIdentity.Native)fixture.Historical.Decoder).Build.RecipeVersion.ShouldBe(2);
        }
        else
        { option.State.ShouldBe(RomDeliveryState.Unsupported); option.Target.ShouldBeNull(); }
        verified.ShouldBe(currentMeasured ? 1 : 0);
        ready.ShouldBeTrue();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("artifact")]
    [InlineData("size")]
    [InlineData("partial")]
    [InlineData("parameters")]
    [InlineData("raw-hash")]
    [InlineData("descriptor")]
    [InlineData("duplicate-build")]
    public void FreshGraph_MissingOrContradictoryCompleteProof_CannotBecomeServingEvidence(string mutation)
    {
        var fixture = Create();
        var facts = fixture.Facts;
        var header = (RomContainerFacts.Chd)facts.Container;
        var proof = fixture.Current;
        var changed = new RomRepresentationVerification(Guid.NewGuid(), mutation == "artifact" ? Sha256.Parse(new string('2', 64)) : proof.ArtifactSha256,
            mutation == "size" ? 1025 : proof.ArtifactSize, proof.Decoder, proof.CheckedProfile, proof.VerifierBuild, proof.ObservedAt,
            mutation == "partial" ? [proof.Outputs[0]] : proof.Outputs, header,
            mutation == "raw-hash" ? Sha1.Parse(new string('3', 40)) : header.StoredRawSha1, header.StoredOverallSha1);
        Should.Throw<ArgumentException>(() => new RomRepresentationFacts(facts.Id, facts.FileId, facts.ArtifactSha256, facts.ArtifactSize,
            mutation == "parameters" ? new RomContainerFacts.Chd(5, header.LogicalBytes, 19584, 2448, 8, header.Codecs, false, header.StoredRawSha1, header.StoredOverallSha1, header.Metadata) : header,
            facts.Manifest, facts.Outputs, mutation == "missing" ? [] : mutation == "duplicate-build" ? [proof, changed] : [changed],
            mutation == "descriptor" ? [new(2, 10, Sha256.Parse(new string('4', 64)), 98)] : facts.Descriptors));
    }

    [Theory]
    [InlineData("old-grant")]
    [InlineData("missing-proof")]
    [InlineData("new-namespace")]
    public async Task OpenAsync_StaleOrUnretainedBinding_RefusesBeforeCas(string scenario)
    {
        var fixture = Create();
        var content = fixture.Content;
        var admission = new ChdDeliveryAdmission(() => true, () => new("/synthetic/cd-native", fixture.Native), () => { }, _ => fixture.Native);
        var target = RomDeliveryPlanner.Plan(content, "data.bin", RomMediaPolicyComposition.CreateDeliveryPolicy(), null, admission)[0].Target!;
        if (scenario == "old-grant")
            target = target with { ReadBinding = fixture.Member.Bind(((RomDecoderIdentity.Native)fixture.Historical.Decoder).Build) };
        else if (scenario == "missing-proof")
            content = fixture.WithProofs(fixture.Historical).Content;
        else
        {
            var prior = fixture.Facts;
            content = Describe(new(43, prior.FileId, prior.ArtifactSha256, prior.ArtifactSize, prior.Container, prior.Manifest,
                prior.Outputs.Select(row => new RomRepresentationOutput(row.Id + 100, row.RomId, row.Selector, row.Identity)).ToArray(), prior.Verifications, prior.Descriptors));
        }
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdAsync(1, default).Returns(content);
        var cas = Substitute.For<IContentAddressableStore>();
        var decoder = Substitute.For<IRomRepresentationDecoder>();
        decoder.CanDecode(RomRepresentationEncoding.Chd).Returns(true);
        (await new RomContentReader(catalog, cas, decoder, admission).OpenAsync(target)).ShouldBeNull();
        cas.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public void RetainedProof_AnotherRuntime_IsAStructuralFactLeftToHostQualification()
    {
        var fixture = Create("linux-arm64");
        ((RomDecoderIdentity.Native)fixture.Current.Decoder).Build.Rid.ShouldBe("linux-arm64");
        fixture.Member.HasDecoder(ChdBuildEvidence.Decoder(fixture.Native)).ShouldBeTrue();
    }

    [Fact]
    public void AssessCd_UnqualifiedResidentRuntime_RefusesBeforeNativeVerification()
    {
        var fixture = Create("linux-arm64");
        int verified = 0;
        bool ready = true;
        var admission = new ChdDeliveryAdmission(() => ready, () => new("/synthetic/cd-native", fixture.Native), () => ready = false,
            _ => { verified++; return fixture.Native; });
        admission.AssessCd(fixture.Member).ShouldBeNull();
        verified.ShouldBe(0);
        ready.ShouldBeTrue();
    }

    [Theory]
    [InlineData("linux-x64", false)]
    [InlineData("linux-arm64", true)]
    public void AssessCd_RegistrationQualifiesAnotherRuntime_AdmitsOnlyThatRuntime(string rid, bool admitted)
    {
        var fixture = Create(rid);
        int verified = 0;
        var media = RomMediaPolicyComposition.Create(RomMediaPolicyComposition.Registrations
            .Select(kind => kind.ManifestType == MediaManifestType.CdTracks ? kind with { QualifiedRuntime = "linux-arm64" } : kind).ToArray());
        var admission = new ChdDeliveryAdmission(() => true, () => new("/synthetic/cd-native", fixture.Native), () => { },
            _ => { verified++; return fixture.Native; }, new MediaHostCapabilities(() => fixture.Native, () => null, media, encodes: false), media.Kinds);
        var decoder = admission.AssessCd(fixture.Member);
        if (admitted)
            decoder.ShouldBe(ChdBuildEvidence.Decoder(fixture.Native));
        else
            decoder.ShouldBeNull();
        verified.ShouldBe(admitted ? 1 : 0);
    }

    [Fact]
    public void AssessCd_AnotherCdKindIsUnqualified_DoesNotBlockTheMeasuredFamily()
    {
        var fixture = Create();
        var media = RomMediaPolicyComposition.Create(RomMediaPolicyComposition.Registrations.Select(kind =>
            kind.Kind == "saturn-cd-set" ? kind with { QualifiedRuntime = "linux-arm64" } : kind).ToArray());
        var capabilities = new MediaHostCapabilities(() => fixture.Native, () => null, media, encodes: false);
        capabilities.CanDecode("saturn-cd-set").ShouldBeFalse();
        capabilities.CanDecode("ps1-cd-set").ShouldBeTrue();
        var admission = new ChdDeliveryAdmission(() => true, () => new("/synthetic/cd-native", fixture.Native), () => { },
            _ => fixture.Native, capabilities, media.Kinds);

        admission.AssessCd(fixture.Member).ShouldBe(ChdBuildEvidence.Decoder(fixture.Native));
    }

    [Fact]
    public async Task ReadPaths_CdMemberArchiveAndDvd_AskDecodeCapabilityButNeverEncode()
    {
        var fixture = Create();
        var media = RomMediaPolicyComposition.Create();
        var capabilities = new DecodeOnly(new MediaHostCapabilities(() => fixture.Native,
            () => throw new InvalidOperationException("A read path asked for the encoder build."), media, encodes: true));
        var admission = new ChdDeliveryAdmission(() => true, () => new("/synthetic/cd-native", fixture.Native), () => { }, _ => fixture.Native, capabilities, media.Kinds);
        var content = fixture.Content;
        var option = RomDeliveryPlanner.Plan(content, "data.bin", media.DeliveryPolicy, MediaKindResolver.Resolve(media.Kinds, content, ["psx"]), admission)[0];
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdAsync(1, default).Returns(content);
        var cas = Substitute.For<IContentAddressableStore>();
        var decoder = Substitute.For<IRomRepresentationDecoder>();
        cas.OpenSynchronousRaw(Arg.Any<StorageKey>()).Returns((ISynchronousRawContent?)null);
        cas.OpenRandomAccessAsync(Arg.Any<StorageKey>(), Arg.Any<CancellationToken>()).Returns((IRandomAccessContent?)null);
        decoder.CanDecode(RomRepresentationEncoding.Chd).Returns(true);
        var opened = await new RomContentReader(catalog, cas, decoder, admission).OpenAsync(option.Target!);
        var target = new RomCdArchiveDownload(12, Guid.NewGuid(), fixture.Facts, "disc.chd");
        var archives = Substitute.For<IRomCdArchiveDownloadCatalog>();
        archives.FindForLayoutAsync(12, default).Returns(target);
        var archive = await new RomCdArchiveDownloadReader(archives, cas, admission).OpenAsync(target);
        int memberQuestions = capabilities.DecodeQuestions;
        option.State.ShouldBe(RomDeliveryState.Available);
        opened.ShouldBeNull();
        archive.ShouldBeNull();
        cas.Received(1).OpenSynchronousRaw(StorageKey.FromHash(fixture.Facts.ArtifactSha256));
        await cas.Received(1).OpenRandomAccessAsync(StorageKey.FromHash(fixture.Facts.ArtifactSha256), Arg.Any<CancellationToken>());
        memberQuestions.ShouldBeGreaterThan(0);
        var dvd = ChdDeliveryFixture.Create(nativeBuild: fixture.Native);
        admission.Assess(dvd.Evidence).ShouldNotBeNull();
        capabilities.DecodeQuestions.ShouldBeGreaterThan(memberQuestions);
    }

    [Fact]
    public void AssessCd_ResidentIdentityChanges_InvalidatesReadinessAndRefusesEarlierProof()
    {
        var fixture = Create();
        bool ready = true;
        int calls = 0;
        var admission = new ChdDeliveryAdmission(() => ready, () => new("/synthetic/cd-native", fixture.Native), () => ready = false,
            _ => { calls++; return fixture.Native with { RecipeVersion = 4 }; });
        admission.AssessCd(fixture.Member).ShouldBeNull();
        ready.ShouldBeFalse();
        admission.AssessCd(fixture.Member).ShouldBeNull();
        calls.ShouldBe(1);
    }

    private sealed class DecodeOnly(IMediaHostCapabilities inner) : IMediaHostCapabilities
    {
        public int DecodeQuestions { get; private set; }
        public string ManagedDecoderBuild => inner.ManagedDecoderBuild;
        public RomChdDecoderBuild? Decoder(RomRepresentationEncoding encoding) => inner.Decoder(encoding);
        public bool CanDecode(string kind) { DecodeQuestions++; return inner.CanDecode(kind); }
        public bool CanEncode(string kind) => throw new InvalidOperationException("A read path consulted encode capability.");
        public string? QualifiedRuntime(string kind) => inner.QualifiedRuntime(kind);
        public bool Qualifies(string kind, string runtimeIdentifier) => inner.Qualifies(kind, runtimeIdentifier);
    }
}
