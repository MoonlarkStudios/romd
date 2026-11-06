using System.Buffers.Binary;
using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Content;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Content;

public sealed class MediaReconciliationPlanningTests
{
    private static readonly Guid Job = Guid.NewGuid();
    private static readonly RomChdDecoderBuild Resident = DeliveryFacts.Decoder(3);
    private static IMediaHostCapabilities Caps(bool decode, bool encode, bool qualified, RomChdDecoderBuild native, string managed)
    {
        var caps = Substitute.For<IMediaHostCapabilities>();
        caps.CanDecode(Arg.Any<string>()).Returns(decode);
        caps.CanEncode(Arg.Any<string>()).Returns(encode);
        caps.Qualifies(Arg.Any<string>(), Arg.Any<string>()).Returns(qualified);
        caps.Decoder(RomRepresentationEncoding.Chd).Returns(native);
        caps.ManagedDecoderBuild.Returns(managed);
        return caps;
    }
    private static IMediaReconciliationMediaProfile Profile(MediaReconciliationEncoder? encoder)
    {
        var profile = Substitute.For<IMediaReconciliationMediaProfile>();
        profile.OriginalInspectionDecoder.Returns(new RomDecoderIdentity.Managed("original-inspector"));
        profile.LimitsFingerprint.Returns(DeliveryFacts.Hash('d'));
        profile.Encoder(Arg.Any<string>()).Returns(encoder);
        return profile;
    }
    private static MediaReconciliationPlanning Planner(MediaKindCatalog kinds, MediaReconciliationFacts facts, IMediaHostCapabilities caps, IMediaReconciliationMediaProfile profile)
    {
        var snapshots = Substitute.For<IMediaReconciliationSnapshotReader>();
        snapshots.ReadAsync(Arg.Any<MediaReconciliationSubject>(), Arg.Any<CancellationToken>())
            .Returns((ErrorOr<MediaReconciliationFacts>)facts);
        var policies = kinds.Kinds.ToDictionary(kind => kind.Kind, kind => new MediaReconciliationPolicy(kind,
            container => container is RomContainerFacts.Rvz { CompressionLevel: 19 } || container is RomContainerFacts.Chd { HunkBytes: 9792 }, MediaRecipeRebuild.MatchingParameters,
            (_, ct) => { ct.ThrowIfCancellationRequested(); return kind.Kind != "ps2-dvd"; },
            kind.ManifestType == MediaManifestType.CdTracks ? graph => graph.Manifest is RomReconstructionManifest.CdTracks && graph.Verifications.Count > 0 : null,
            kind.ManifestType == MediaManifestType.CdTracks ? kind.ProfileVersions[0] : null));
        return new(snapshots, new(kinds, policies, caps, profile));
    }
    private static MediaReconciliationDemandState Demand(MediaReconciliationSubject subject, bool conversion) => new(subject, 7, conversion, false);
    private static RomRepresentationFacts Withdrawn(RomRepresentationFacts graph) => new(graph.Id, graph.FileId, graph.ArtifactSha256, graph.ArtifactSize, graph.Container, graph.Manifest,
        graph.Outputs.Select(output => new RomRepresentationOutput(output.Id, null, output.Selector, output.Identity)).ToArray(), graph.Verifications, graph.Descriptors, graph.Creations, graph.CdBootInspections);

    [Fact]
    public async Task NextAsync_UnmeasuredEncodedRepresentation_StillCapturesVerifyOnly()
    {
        var graph = DeliveryFacts.WithoutProof(DeliveryFacts.RvzContent(1, 2, 8192, 4096).Preferred).Facts;
        var profile = Profile(null);
        var result = await Planner(TestMediaKinds.Production, new MediaReconciliationFacts.Representation(graph),
            Caps(true, false, true, Resident, DeliveryFacts.ManagedBuild), profile)
            .NextAsync(Job, Demand(new MediaReconciliationSubject.Representation(graph.Id), false));

        var verify = result.Value.ShouldBeOfType<MediaReconciliationDirective.Verify>();
        verify.Source.RepresentationId.ShouldBe(graph.Id);
        verify.Source.Decoder.ShouldBe(new RomDecoderIdentity.Managed(DeliveryFacts.ManagedBuild));
        verify.Capture.Sources.Inputs.ShouldHaveSingleItem().ShouldBeOfType<MediaReconciliationInput.Artifact>()
            .VerificationId.ShouldBeNull();
        graph.Verifications.ShouldBeEmpty();
        profile.DidNotReceive().Encoder(Arg.Any<string>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NextAsync_UnmeasuredIdentitySource_SettlesWithoutInspectionOrRetry(bool inspected, bool conversionRequested)
    {
        var original = inspected ? ObservedOriginal(true, "older-original-inspector") : DeliveryFacts.OriginalContent(1, 1, 8192);
        var artifact = DeliveryFacts.WithoutProof(original.Preferred);
        var content = new RomContentDescriptor(original.RomId, original.OutputId, original.Identity, original.OriginalFilename,
            [artifact], original.Inspections, []);
        var profile = Profile(new("fixture-encoder", RomIsoDiscKind.GameCube));
        var result = await Planner(TestMediaKinds.Production, new MediaReconciliationFacts.Logical(content, ["gc"]),
            Caps(true, true, true, Resident, DeliveryFacts.ManagedBuild), profile)
            .NextAsync(Job, Demand(new MediaReconciliationSubject.LogicalRom(content.RomId), conversionRequested));

        result.IsError.ShouldBeFalse();
        var settled = result.Value.ShouldBeOfType<MediaReconciliationDirective.Settle>();
        settled.Outcome.ShouldBe(MediaReconciliationOutcomeKind.Completed);
        settled.Code.ShouldBe("source-unverified");
        profile.DidNotReceive().Encoder(Arg.Any<string>());
        _ = profile.DidNotReceive().OriginalInspectionDecoder;
        artifact.Facts.Verifications.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextAsync_UnmeasuredEncodedLogicalSource_DefersUntilVerifyOnlyMeasuresIt(bool inspected)
    {
        var original = inspected ? ObservedOriginal(true, "older-original-inspector") : DeliveryFacts.OriginalContent(1, 1, 8192);
        var source = DeliveryFacts.RvzContent(1, 2, 8192, 4096);
        var artifact = DeliveryFacts.WithoutProof(source.Preferred);
        var content = new RomContentDescriptor(source.RomId, source.OutputId, source.Identity, source.OriginalFilename,
            [artifact], original.Inspections, []);
        var profile = Profile(new("fixture-encoder", RomIsoDiscKind.GameCube));
        var result = await Planner(TestMediaKinds.Production, new MediaReconciliationFacts.Logical(content, ["gc"]),
            Caps(true, true, true, Resident, DeliveryFacts.ManagedBuild), profile)
            .NextAsync(Job, Demand(new MediaReconciliationSubject.LogicalRom(content.RomId), true));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeOfType<MediaReconciliationDirective.Unavailable>().Code.ShouldBe("source-unverified");
        profile.DidNotReceive().Encoder(Arg.Any<string>());
        artifact.Facts.Verifications.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(128, false)]
    [InlineData(129, true)]
    public void NeedsInspection_PreservesTheStructuralInspectionAdmissionBoundary(long size, bool expected)
    {
        LogicalMediaInspectionPolicy.NeedsInspection(DeliveryFacts.OriginalContent(1, 1, size)).ShouldBe(expected);
    }

    [Fact]
    public async Task RetainedCd_WithoutLogicalOwners_VerifiesExactResidentBuildWithoutEncoderOrRuntimeClaim()
    {
        var graph = Withdrawn(DeliveryFacts.CdContent().Preferred.Facts);
        var profile = Profile(null);
        var result = await Planner(TestMediaKinds.Production, new MediaReconciliationFacts.Representation(graph), Caps(true, false, true, Resident, "managed"), profile)
            .NextAsync(Job, Demand(new MediaReconciliationSubject.Representation(graph.Id), false));
        var verify = result.Value.ShouldBeOfType<MediaReconciliationDirective.Verify>();
        verify.Source.Decoder.ShouldBe(new RomDecoderIdentity.Native(Resident));
        verify.Source.RepresentationId.ShouldBe(graph.Id);
        verify.Capture.KindId.ShouldBeNull();
        verify.Capture.EncoderBuild.ShouldBeNull();
        verify.Capture.Sources.Inputs.Single().ShouldBeOfType<MediaReconciliationInput.Artifact>().Graph.Outputs.All(output => output.RomId is null).ShouldBeTrue();
        profile.DidNotReceive().Encoder(Arg.Any<string>());
    }

    [Fact]
    public async Task RetainedCd_UnqualifiedDecoder_DefersWithoutCapture()
    {
        var graph = DeliveryFacts.CdContent().Preferred.Facts;
        var result = await Planner(TestMediaKinds.Production, new MediaReconciliationFacts.Representation(graph), Caps(true, true, false, Resident, "managed"), Profile(new("encoder", null)))
            .NextAsync(Job, Demand(new MediaReconciliationSubject.Representation(graph.Id), false));
        result.Value.ShouldBeOfType<MediaReconciliationDirective.Unavailable>();
        graph.Verifications.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task RetainedArtifact_CurrentBuildAlreadyPresent_SettlesWithoutAnotherMeasurement()
    {
        var graph = DeliveryFacts.CdContent().Preferred.Facts;
        var result = await Planner(TestMediaKinds.Production, new MediaReconciliationFacts.Representation(graph), Caps(true, false, true, DeliveryFacts.Decoder(), "managed"), Profile(null))
            .NextAsync(Job, Demand(new MediaReconciliationSubject.Representation(graph.Id), false));
        result.Value.ShouldBeOfType<MediaReconciliationDirective.Settle>().Code.ShouldBe("verified");
    }

    [Fact]
    public async Task UninspectedOriginal_CapturesLogicalSourceAndCurrentReferenceSnapshotBeforeClassification()
    {
        var content = DeliveryFacts.OriginalContent(1, 1, 8192);
        var result = await Planner(TestMediaKinds.Production, new MediaReconciliationFacts.Logical(content, []), Caps(true, true, true, Resident, "managed"), Profile(new("encoder", RomIsoDiscKind.GameCube)))
            .NextAsync(Job, Demand(new MediaReconciliationSubject.LogicalRom(1), false));
        var inspect = result.Value.ShouldBeOfType<MediaReconciliationDirective.Inspect>();
        inspect.Capture.Identity.Operation.ShouldBe(MediaReconciliationOperation.Inspect);
        inspect.Capture.RequestedVersion.ShouldBe(7);
        inspect.Capture.Decoder.ShouldBe(new RomDecoderIdentity.Managed("original-inspector"));
        inspect.Source.Output.Id.ShouldBe(content.OutputId);
        inspect.Source.VerificationId.ShouldBe(content.Preferred.Facts.Verifications.Single().Id);
        inspect.Capture.Sources.ReferenceFingerprint.ShouldBe(MediaReconciliationReferenceFingerprint.Compute([]));
        inspect.Capture.KindId.ShouldBeNull();
    }

    [Fact]
    public async Task ObservedOpaqueOriginal_SettlesWithoutInventingAWholeFileKind()
    {
        var content = ObservedOriginal(false, "older-original-inspector");
        var result = await Planner(TestMediaKinds.Production, new MediaReconciliationFacts.Logical(content, []), Caps(true, true, true, Resident, "managed"), Profile(new("encoder", RomIsoDiscKind.GameCube)))
            .NextAsync(Job, Demand(new MediaReconciliationSubject.LogicalRom(1), true));
        result.Value.ShouldBeOfType<MediaReconciliationDirective.Settle>().Code.ShouldBe("kind-ineligible");
    }

    [Fact]
    public async Task ObservedGameCube_EncodesWithActualCapturedEncoderBuildAndDiscBinding()
    {
        var content = ObservedOriginal(true, "original-inspector");
        var result = await Planner(TestMediaKinds.Production, new MediaReconciliationFacts.Logical(content, ["gc"]), Caps(true, true, true, Resident, "managed-current"), Profile(new("actual-gc-encoder", RomIsoDiscKind.GameCube)))
            .NextAsync(Job, Demand(new MediaReconciliationSubject.LogicalRom(1), false));
        var encode = result.Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
        encode.Capture.EncoderBuild.ShouldBe("actual-gc-encoder");
        encode.Capture.DiscKind.ShouldBe(RomIsoDiscKind.GameCube);
        encode.Capture.KindId.ShouldBe("gamecube-disc");
        encode.Capture.Identity.Recipe!.Value.ShouldBe("rvz-canonical/v1");
        encode.Capture.Sources.Inputs.Single().ShouldBeOfType<MediaReconciliationInput.Logical>().Output.Id.ShouldBe(content.OutputId);
        encode.Capture.Sources.LogicalInspectionIds.ShouldBe(content.Inspections.Select(row => row.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClassifiedOriginal_MissingCurrentInspectionDecoder_InspectsBeforeEncodingAndRetainsExistingObservationSnapshot(bool requested)
    {
        var content = ObservedOriginal(true, "older-original-inspector");
        var result = await Planner(TestMediaKinds.Production, new MediaReconciliationFacts.Logical(content, ["gc"]), Caps(true, true, true, Resident, "managed-current"), Profile(new("actual-gc-encoder", RomIsoDiscKind.GameCube)))
            .NextAsync(Job, Demand(new MediaReconciliationSubject.LogicalRom(1), requested));
        var inspect = result.Value.ShouldBeOfType<MediaReconciliationDirective.Inspect>();
        inspect.Capture.Decoder.ShouldBe(new RomDecoderIdentity.Managed("original-inspector"));
        inspect.Capture.Sources.LogicalInspectionIds.ShouldBe(content.Inspections.Select(row => row.Id));
        inspect.Capture.Sources.ReferenceFingerprint.ShouldBe(MediaReconciliationReferenceFingerprint.Compute(content.References));
        inspect.Source.VerificationId.ShouldBe(content.Preferred.Facts.Verifications.OrderBy(proof => proof.ObservedAt).ThenBy(proof => proof.Id).First().Id);
        content.Preferred.Facts.Verifications.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(18, false)]
    [InlineData(19, true)]
    public async Task OriginalWithMeasuredRvz_ReinspectsOnlyWhenEncodingAndKeepsInspectionCaptureToOnePhysicalGraph(int level, bool canonical)
    {
        var original = ObservedOriginal(true, "older-original-inspector");
        var container = new RomContainerFacts.Rvz(0x01000000, 1, 5, level, 131072, false, original.ContentSize);
        var proof = new RomRepresentationVerification(Guid.NewGuid(), DeliveryFacts.Hash('b'), 4096, new RomDecoderIdentity.Managed("managed-current"),
            "complete-output/v1", "fixture-verifier", DateTimeOffset.UnixEpoch, [new(RomOutputSelector.Whole.Instance, original.Identity)], container);
        var graph = new RomRepresentationFacts(2, 2, DeliveryFacts.Hash('b'), 4096, container, RomReconstructionManifest.WholeFile.Instance,
            [new(2, original.RomId, RomOutputSelector.Whole.Instance, original.Identity)], [proof], []);
        var content = new RomContentDescriptor(original.RomId, original.OutputId, original.Identity, original.OriginalFilename,
            [.. original.Artifacts, new(graph, 2, false, null, null)], original.Inspections, []);
        var result = await Planner(TestMediaKinds.Production, new MediaReconciliationFacts.Logical(content, ["gc"]), Caps(true, true, true, Resident, "managed-current"), Profile(new("actual-gc-encoder", RomIsoDiscKind.GameCube)))
            .NextAsync(Job, Demand(new MediaReconciliationSubject.LogicalRom(1), true));
        if (canonical)
        {
            var release = result.Value.ShouldBeOfType<MediaReconciliationDirective.Release>();
            release.RepresentationId.ShouldBe(graph.Id);
            release.ProofId.ShouldBe(proof.Id);
        }
        else
        {
            var inspect = result.Value.ShouldBeOfType<MediaReconciliationDirective.Inspect>();
            inspect.Capture.Sources.Inputs.ShouldHaveSingleItem().ShouldBeOfType<MediaReconciliationInput.Logical>().Graph.Id.ShouldBe(original.RepresentationId);
            inspect.Capture.Sources.LogicalInspectionIds.ShouldBe(original.Inspections.Select(row => row.Id));
            inspect.Capture.Decoder.ShouldBe(new RomDecoderIdentity.Managed("original-inspector"));
        }
    }

    [Fact]
    public async Task VerifiedNoncanonicalDvd_WithoutCurrentReference_DoesNotAuthorizeEncoding()
    {
        var original = DeliveryFacts.ChdContent(1, 1);
        var inspection = new RomLogicalInspection(Guid.NewGuid(), original.Identity, "logical/v1", "inspector", DateTimeOffset.UnixEpoch, new byte[32]);
        var content = new RomContentDescriptor(original.RomId, original.OutputId, original.Identity, original.OriginalFilename, original.Artifacts, [inspection], []);
        var planner = Planner(TestMediaKinds.Production, new MediaReconciliationFacts.Logical(content, ["ps2"]), Caps(true, true, true, DeliveryFacts.Decoder(), "managed"), Profile(new("dvd-encoder", RomIsoDiscKind.PlayStation2Dvd)));
        var result = await planner.NextAsync(Job, Demand(new MediaReconciliationSubject.LogicalRom(1), true));
        result.Value.ShouldBeOfType<MediaReconciliationDirective.Settle>().Code.ShouldBe(MediaReconciliationNoWork.Ineligible.ToString());
    }

    [Fact]
    public async Task CdMemberLogicalScope_DefersLayoutOwnershipToItsLayoutJob()
    {
        var content = DeliveryFacts.CdContent();
        var result = await Planner(TestMediaKinds.Production, new MediaReconciliationFacts.Logical(content, ["psx"]), Caps(true, true, true, Resident, "managed"), Profile(new("encoder", null)))
            .NextAsync(Job, Demand(new MediaReconciliationSubject.LogicalRom(content.RomId), true));
        result.Value.ShouldBeOfType<MediaReconciliationDirective.Settle>().Code.ShouldBe("layout-managed");
    }

    [Fact]
    public async Task LayoutUsingAnotherArchivesBin_EncodesItsOwnArchiveInsteadOfAdoptingForeignCanonicalOwnership()
    {
        var graph = DeliveryFacts.CdContent().Preferred.Facts;
        var layout = Layout(graph, [], null, false);
        var result = await Planner(TestMediaKinds.Production, layout, Caps(true, true, true, DeliveryFacts.Decoder(), "managed"), Profile(new("actual-cd-encoder", null)))
            .NextAsync(Job, Demand(new MediaReconciliationSubject.OriginalLayout(1), true));
        var encode = result.Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
        encode.Capture.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().Single().Graph.Id.ShouldBe(graph.Id);
        encode.Capture.Sources.Layout.ShouldBeOfType<MediaReconciliationLayout.Original>().LayoutId.ShouldBe(1);
        encode.Capture.EncoderBuild.ShouldBe("actual-cd-encoder");
    }

    [Fact]
    public async Task ForeignArchiveSource_MissingResidentProof_IsVerifiedBeforeEncodingThisLayout()
    {
        var graph = DeliveryFacts.CdContent().Preferred.Facts;
        var result = await Planner(TestMediaKinds.Production, Layout(graph, [], null, false), Caps(true, true, true, Resident, "managed"), Profile(new("encoder", null)))
            .NextAsync(Job, Demand(new MediaReconciliationSubject.OriginalLayout(1), true));
        var verify = result.Value.ShouldBeOfType<MediaReconciliationDirective.Verify>();
        verify.Source.RepresentationId.ShouldBe(graph.Id);
        verify.Source.Decoder.ShouldBe(new RomDecoderIdentity.Native(Resident));
        verify.Capture.Sources.Layout.ShouldBeOfType<MediaReconciliationLayout.Original>().LayoutId.ShouldBe(1);
    }

    [Fact]
    public async Task OwnedCanonicalLayout_NeedingAssociationAlignment_UsesReleaseWithTheExactCanonicalProof()
    {
        var graph = DeliveryFacts.CdContent().Preferred.Facts;
        var result = await Planner(TestMediaKinds.Production, Layout(graph, [graph], graph.Id, true), Caps(true, false, true, DeliveryFacts.Decoder(), "managed"), Profile(null))
            .NextAsync(Job, Demand(new MediaReconciliationSubject.OriginalLayout(1), false));
        var release = result.Value.ShouldBeOfType<MediaReconciliationDirective.Release>();
        release.RepresentationId.ShouldBe(graph.Id);
        release.ProofId.ShouldBe(graph.Verifications.Single().Id);
        release.Capture.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().Single().VerificationId.ShouldBe(release.ProofId);
    }

    [Fact]
    public async Task OwnedCanonicalLayout_WithAnotherWholeOriginalHolding_RequestsReleaseUsingTheExactCurrentProof()
    {
        var graph = DeliveryFacts.CdContent().Preferred.Facts;
        var layout = Layout(graph, [graph], graph.Id, false);
        var raw = DeliveryFacts.OriginalContent(graph.Outputs.Single().RomId!.Value, 99, graph.Outputs.Single().Identity.Size).Preferred.Facts;
        var complete = new MediaReconciliationFacts.Layout(layout.KindId, new([.. layout.Sources.Inputs, new MediaReconciliationInput.Artifact(raw, null, null)],
            layout.Sources.Layout, [], null), layout.Archives, layout.CurrentArchiveId, false, null);
        var profile = Profile(null);
        var result = await Planner(TestMediaKinds.Production, complete, Caps(true, false, true, DeliveryFacts.Decoder(), "managed"), profile)
            .NextAsync(Job, Demand(new MediaReconciliationSubject.OriginalLayout(1), false));
        var release = result.Value.ShouldBeOfType<MediaReconciliationDirective.Release>();
        release.RepresentationId.ShouldBe(graph.Id);
        release.ProofId.ShouldBe(graph.Verifications.Single().Id);
        release.Capture.Sources.Inputs.OfType<MediaReconciliationInput.Artifact>().Single().Graph.ShouldBeSameAs(raw);
        profile.DidNotReceive().Encoder(Arg.Any<string>());
    }

    [Theory]
    [InlineData(ConversionMode.OnRequest)]
    [InlineData(ConversionMode.Automatic)]
    public async Task CdConversionMode_WithoutExplicitDemand_VerifiesMissingSourceProofBeforeItsModeDecision(ConversionMode mode)
    {
        var kinds = new MediaKindCatalog(TestMediaKinds.Descriptors.Select(kind => kind.ManifestType == MediaManifestType.CdTracks
            ? kind with { ConversionMode = mode } : kind));
        var graph = DeliveryFacts.CdContent().Preferred.Facts;
        var layout = Layout(graph, [], null, false);
        var missing = await Planner(kinds, layout, Caps(true, true, true, Resident, "managed"), Profile(new("encoder", null)))
            .NextAsync(Job, Demand(new MediaReconciliationSubject.OriginalLayout(1), false));
        var verify = missing.Value.ShouldBeOfType<MediaReconciliationDirective.Verify>();
        verify.Source.RepresentationId.ShouldBe(graph.Id);
        verify.Source.Decoder.ShouldBe(new RomDecoderIdentity.Native(Resident));

        var current = await Planner(kinds, layout, Caps(true, true, true, DeliveryFacts.Decoder(), "managed"), Profile(new("encoder", null)))
            .NextAsync(Job, Demand(new MediaReconciliationSubject.OriginalLayout(1), false));
        if (mode == ConversionMode.OnRequest)
            current.Value.ShouldBeOfType<MediaReconciliationDirective.Settle>().Code.ShouldBe(MediaReconciliationNoWork.ConversionNotRequested.ToString());
        else
            current.Value.ShouldBeOfType<MediaReconciliationDirective.Encode>().Capture.Identity.Recipe!.Value.ShouldBe("chd-ps1-canonical/v1");
    }

    [Fact]
    public async Task CanonicalCatalogArchive_WithAuthenticOriginalSources_AdoptsWithoutEncoderOrConversionIntent()
    {
        var graph = DeliveryFacts.CdContent().Preferred.Facts;
        var layout = Layout(graph, [], null, false);
        var original = (MediaReconciliationLayout.Original)layout.Sources.Layout!;
        var cue = ((RomReconstructionManifest.CdTracks)graph.Manifest).Members.OfType<RomCdStoredMember>().Single();
        var witness = new MediaReconciliationLayout.Catalog(7, Guid.NewGuid(), original.PlatformId,
            new(13, original.ReleaseId, original.CatalogFingerprint, "cue-requirement", cue.Path, cue.Identity.Size, cue.Identity.Sha1, cue.Identity.Md5, cue.Identity.Crc32), "measured-formatter");
        var candidate = new MediaReconciliationInput.Artifact(graph, null, witness);
        var facts = new MediaReconciliationFacts.Layout(layout.KindId, layout.Sources, [], null, false, candidate);
        var profile = Profile(null);
        var result = await Planner(TestMediaKinds.Production, facts, Caps(true, false, true, DeliveryFacts.Decoder(), "managed"), profile)
            .NextAsync(Job, Demand(new MediaReconciliationSubject.OriginalLayout(1), false));

        var adoption = result.Value.ShouldBeOfType<MediaReconciliationDirective.Adopt>();
        adoption.Capture.Identity.Operation.ShouldBe(MediaReconciliationOperation.Adopt);
        adoption.Capture.EncoderBuild.ShouldBeNull();
        adoption.Capture.Sources.Layout.ShouldBeSameAs(original);
        var captured = adoption.Capture.Sources.Inputs.OfType<MediaReconciliationInput.Artifact>().Single(input => input.CatalogWitness is not null);
        captured.Graph.ShouldBeSameAs(graph);
        captured.CatalogWitness.ShouldBeSameAs(witness);
        profile.DidNotReceive().Encoder(Arg.Any<string>());
    }

    private static MediaReconciliationFacts.Layout Layout(RomRepresentationFacts backing, IReadOnlyList<RomRepresentationFacts> archives, int? owner, bool align)
    {
        var cue = ((RomReconstructionManifest.CdTracks)backing.Manifest).Members.OfType<RomCdStoredMember>().Single();
        var sources = new MediaReconciliationSources([new MediaReconciliationInput.Logical(MediaReconciliationSourceRole.Bin,"data.bin",backing,backing.Outputs.Single().Id,backing.Verifications.Single().Id),
            new MediaReconciliationInput.Stored(MediaReconciliationSourceRole.Cue,"disc.cue",backing.Descriptors.Single().FileId,cue.Identity)], new MediaReconciliationLayout.Original(1, Guid.NewGuid(), 1, 1, "catalog", "disc.cue"), [], null);
        return new("ps1-cd-set", sources, archives, owner, align, null);
    }
    private static RomContentDescriptor ObservedOriginal(bool gamecube, string decoderBuild)
    {
        var content = DeliveryFacts.OriginalContent(1, 1, 8192);
        byte[] prefix = new byte[32];
        if (gamecube)
            BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(28), 0xc2339f3d);
        var inspection = new RomLogicalInspection(Guid.NewGuid(), content.Identity, "logical/v1", "inspector", DateTimeOffset.UnixEpoch, prefix,
            gamecube ? new RomNintendoLayoutFacts(0, 0, 0) : null);
        var graph = content.Preferred.Facts;
        var prior = graph.Verifications.Single();
        var measured = new RomRepresentationVerification(Guid.NewGuid(), graph.ArtifactSha256, graph.ArtifactSize, new RomDecoderIdentity.Managed(decoderBuild),
            prior.CheckedProfile, prior.VerifierBuild, prior.ObservedAt, prior.Outputs, graph.Container, null, null);
        var retained = new RomRepresentationFacts(graph.Id, graph.FileId, graph.ArtifactSha256, graph.ArtifactSize, graph.Container, graph.Manifest,
            graph.Outputs, [prior, measured], graph.Descriptors);
        return new(content.RomId, content.OutputId, content.Identity, content.OriginalFilename, [new(retained, content.OutputId, true, content.Identity.Md5, content.Identity.Sha1)], [inspection], []);
    }
}
