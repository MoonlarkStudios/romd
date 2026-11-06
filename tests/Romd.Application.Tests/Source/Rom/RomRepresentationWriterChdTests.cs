using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Domain.Storage;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class RomRepresentationWriterChdTests
{
    [Theory]
    [InlineData(RomRepresentationEncoding.Chd, false)]
    [InlineData(RomRepresentationEncoding.Chd, true)]
    [InlineData(RomRepresentationEncoding.Rvz, false)]
    [InlineData(RomRepresentationEncoding.Rvz, true)]
    public async Task RecordMeasurementAsync_CompleteFacts_RetainsExactOutputBeforeRawObservation(RomRepresentationEncoding encoding, bool published)
    {
        var fixture = new Fixture(encoding);
        using var cancel = new CancellationTokenSource();
        var prepared = fixture.Facts.Prepared;
        var result = await fixture.Writer.RecordMeasurementAsync(7, prepared.Container, prepared.Measurement, prepared.Inspection, cancel.Token,
            published ? fixture.Published : null);
        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeSameAs(fixture.Receipt);
        fixture.Events.ShouldBe(["measurement", "observation"]);
        await fixture.Roms.Received(1).RecordWholeMeasurementStagedAsync(7, published ? 2 : null, prepared.Container, prepared.Measurement, cancel.Token);
        await fixture.Observations.Received(1).StageAsync(7, fixture.Receipt, prepared.Inspection!, cancel.Token);
        await fixture.NoPromotionAsync();
    }

    [Fact]
    public async Task RecordMeasurementAsync_NoInspection_DoesNotInventRawObservations()
    {
        var fixture = new Fixture();
        var prepared = fixture.Facts.Prepared;
        (await fixture.Writer.RecordMeasurementAsync(7, prepared.Container, prepared.Measurement, null, default)).Value.ShouldBe(fixture.Receipt);
        fixture.Events.ShouldBe(["measurement"]);
        fixture.Observations.ReceivedCalls().ShouldBeEmpty();
        await fixture.NoPromotionAsync();
    }

    [Fact]
    public async Task RecordMeasurementAsync_RefusedCompleteMeasurement_StopsBeforeRawObservation()
    {
        var fixture = new Fixture { Refuse = "measurement" };
        var prepared = fixture.Facts.Prepared;
        var result = await fixture.Writer.RecordMeasurementAsync(7, prepared.Container, prepared.Measurement, prepared.Inspection, default);
        result.FirstError.Code.ShouldBe(RomEligibilityErrors.MeasurementNotRetained().Code);
        fixture.Events.ShouldBe(["measurement"]);
        fixture.Observations.ReceivedCalls().ShouldBeEmpty();
        await fixture.NoPromotionAsync();
    }

    [Fact]
    public async Task RecordMeasurementAsync_ObservationRefused_PreservesThePortErrorWithoutPromotion()
    {
        var fixture = new Fixture { Refuse = "observation" };
        var prepared = fixture.Facts.Prepared;
        (await fixture.Writer.RecordMeasurementAsync(7, prepared.Container, prepared.Measurement, prepared.Inspection, default)).FirstError.ShouldBe(Fixture.Refused);
        fixture.Events.ShouldBe(["measurement", "observation"]);
        await fixture.NoPromotionAsync();
    }

    [Theory]
    [InlineData(RomRepresentationEncoding.Chd, false)]
    [InlineData(RomRepresentationEncoding.Chd, true)]
    [InlineData(RomRepresentationEncoding.Rvz, false)]
    [InlineData(RomRepresentationEncoding.Rvz, true)]
    public async Task RecordCreatedEvidenceAsync_OnlyCreatedArtifacts_CarryExactSourceComparisonAndOptionalNativeProvenance(
        RomRepresentationEncoding encoding, bool created)
    {
        var fixture = new Fixture(encoding, created);
        (await fixture.Writer.RecordCreatedEvidenceAsync(7, fixture.Receipt, fixture.Facts.Prepared, default)).IsError.ShouldBeFalse();
        if (created)
        {
            fixture.Events.ShouldBe(["creation"]);
            await fixture.Creations.Received(1).StageAsync(7, fixture.Receipt, fixture.Facts.Comparison!, fixture.Facts.EncodingEvidence, default);
        }
        else
        { fixture.Events.ShouldBeEmpty(); fixture.Creations.ReceivedCalls().ShouldBeEmpty(); }
        await fixture.NoPromotionAsync();
    }

    [Fact]
    public async Task RecordCreatedEvidenceAsync_ParentOrSourceRefused_PreservesTheErrorWithoutPromotion()
    {
        var fixture = new Fixture(created: true) { Refuse = "creation" };
        (await fixture.Writer.RecordCreatedEvidenceAsync(7, fixture.Receipt, fixture.Facts.Prepared, default)).FirstError.ShouldBe(Fixture.Refused);
        fixture.Events.ShouldBe(["creation"]);
        await fixture.NoPromotionAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreferAndReleaseAsync_SelectsOutputIdentityThenReleasesOnlyTheOtherLockedOwners(bool keepExisting)
    {
        var fixture = new Fixture();
        var prepared = fixture.Facts.Prepared;
        var prior = PublisherChdFacts.Retained(3, PublisherChdFacts.OtherArtifact, prepared.Container, true, true);
        var decision = new RepresentationRetentionDecision(RepresentationRetention.EvidenceOnly, keepExisting ? prior.ArtifactSha256 : prepared.Artifact.FileSha256,
            keepExisting ? [] : [prior]);
        int file = await fixture.Writer.PreferAndReleaseAsync(7, decision, [prior], fixture.Receipt, prepared, default);
        file.ShouldBe(keepExisting ? prior.FileId : fixture.Receipt.FileId);
        fixture.Events.ShouldBe(["flush", "prefer", "release"]);
        await fixture.Roms.Received(1).SetPreferredRepresentationStagedAsync(7, keepExisting ? prior.OutputId : fixture.Receipt.OutputId, default);
        await fixture.Roms.Received(1).ReleaseRepresentationsStagedAsync(7,
            Arg.Is<IReadOnlyList<int>>(ids => keepExisting ? ids.Count == 0 : ids.Count == 1 && ids[0] == prior.OutputId), default);
    }

    [Theory]
    [InlineData("discard")]
    [InlineData("selected-owner")]
    [InlineData("foreign-owner")]
    public async Task PreferAndReleaseAsync_InvalidOwnershipDecision_RejectsBeforeFlushOrMutation(string change)
    {
        var fixture = new Fixture();
        var prepared = fixture.Facts.Prepared;
        var prior = PublisherChdFacts.Retained(2, prepared.Artifact.FileSha256, prepared.Container, true, true);
        var foreign = PublisherChdFacts.Retained(4, PublisherChdFacts.OtherArtifact, prepared.Container, false, true);
        var decision = new RepresentationRetentionDecision(change == "discard" ? RepresentationRetention.Discard : RepresentationRetention.EvidenceOnly,
            prior.ArtifactSha256, change == "foreign-owner" ? [foreign] : [prior]);
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.Writer.PreferAndReleaseAsync(7, decision, [prior], fixture.Receipt, prepared, default));
        fixture.Events.ShouldBeEmpty();
        await fixture.NoPromotionAsync();
    }

    [Theory]
    [InlineData("measurement")]
    [InlineData("observation")]
    [InlineData("creation")]
    public async Task RecordFacts_CancelledPort_PropagatesTheOriginalTokenAndDoesNotPromote(string boundary)
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var fixture = new Fixture(created: true);
        var prepared = fixture.Facts.Prepared;
        if (boundary == "measurement")
            fixture.Roms.RecordWholeMeasurementStagedAsync(7, Arg.Any<int?>(), prepared.Container, prepared.Measurement, cancel.Token)
            .Returns(Task.FromCanceled<RomRetainedMeasurement?>(cancel.Token));
        else if (boundary == "observation")
            fixture.Observations.StageAsync(7, fixture.Receipt, prepared.Inspection!, cancel.Token)
            .Returns(Task.FromCanceled<ErrorOr<Success>>(cancel.Token));
        else
            fixture.Creations.StageAsync(7, fixture.Receipt, fixture.Facts.Comparison!, fixture.Facts.EncodingEvidence, cancel.Token)
            .Returns(Task.FromCanceled<ErrorOr<Success>>(cancel.Token));
        var error = await Should.ThrowAsync<TaskCanceledException>(() => boundary == "creation"
            ? fixture.Writer.RecordCreatedEvidenceAsync(7, fixture.Receipt, prepared, cancel.Token)
            : MeasureAsync());
        error.CancellationToken.ShouldBe(cancel.Token);
        await fixture.NoPromotionAsync();
        async Task<ErrorOr<Success>> MeasureAsync()
        { var result = await fixture.Writer.RecordMeasurementAsync(7, prepared.Container, prepared.Measurement, prepared.Inspection, cancel.Token); return result.IsError ? result.Errors : Result.Success; }
    }

    [Theory]
    [InlineData("measurement")]
    [InlineData("observation")]
    [InlineData("creation")]
    [InlineData("flush")]
    [InlineData("prefer")]
    [InlineData("release")]
    public async Task CurrentPorts_Fault_PreservesTheOriginalExceptionAndStopsFollowingWrites(string boundary)
    {
        var fixture = new Fixture(created: true) { ThrowAt = boundary };
        var prepared = fixture.Facts.Prepared;
        var error = await Should.ThrowAsync<IOException>(async () =>
        {
            var measured = await fixture.Writer.RecordMeasurementAsync(7, prepared.Container, prepared.Measurement, prepared.Inspection, default, fixture.Published);
            (await fixture.Writer.RecordCreatedEvidenceAsync(7, measured.Value, prepared, default)).IsError.ShouldBeFalse();
            await fixture.Writer.PreferAndReleaseAsync(7, new(RepresentationRetention.Retain, prepared.Artifact.FileSha256, []), [], measured.Value, prepared, default);
        });
        error.ShouldBeSameAs(fixture.Failure);
        fixture.Events.Last().ShouldBe(boundary);
        if (boundary is "measurement" or "observation" or "creation" or "flush")
            await fixture.Roms.DidNotReceiveWithAnyArgs().SetPreferredRepresentationStagedAsync(default, default, default);
        if (boundary != "release")
            await fixture.Roms.DidNotReceiveWithAnyArgs().ReleaseRepresentationsStagedAsync(default, default!, default);
    }

    [Theory]
    [InlineData("verification-id")]
    [InlineData("artifact-hash")]
    [InlineData("artifact-size")]
    [InlineData("content-hash")]
    [InlineData("content-size")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc32")]
    [InlineData("decoder")]
    [InlineData("measurement-profile")]
    [InlineData("verifier-build")]
    [InlineData("measurement-time")]
    [InlineData("captured-profile")]
    public void Preparation_ContradictoryIntegrity_IsRefusedBeforeAWriterReceivesIt(string change)
    {
        var facts = new PublisherChdFacts(false);
        var prior = facts.Integrity!;
        var sha1 = change == "sha1" ? Sha1.Parse(new string('e', 40)) : prior.Sha1;
        var parameters = change == "captured-profile" ? PublisherChdFacts.Parameters(PublisherChdFacts.OtherProfile) : prior.Parameters;
        if (change == "content-size")
            parameters = new(parameters.Version, parameters.LogicalBytes + 2048, parameters.HunkBytes, parameters.UnitBytes,
            checked((uint)((parameters.LogicalBytes + 2048 + parameters.HunkBytes - 1) / parameters.HunkBytes)),
            parameters.Codec0, parameters.Codec1, parameters.Codec2, parameters.Codec3, parameters.HasParent, parameters.Metadata, parameters.CapturedEncodingProfile);
        var changed = RomChdIntegrity.Rehydrate(prior.Id, change == "verification-id" ? Guid.NewGuid() : prior.VerificationId,
            change == "artifact-hash" ? PublisherChdFacts.OtherArtifact : prior.ArtifactSha256,
            change == "artifact-size" ? prior.ArtifactSize + 1 : prior.ArtifactSize,
            change == "content-hash" ? PublisherChdFacts.OtherArtifact : prior.ContentSha256,
            change == "content-size" ? prior.ContentSize + 2048 : prior.ContentSize, sha1,
            change == "md5" ? Md5.Parse(new string('e', 32)) : prior.Md5, change == "crc32" ? Crc32.FromUInt32(124) : prior.Crc32,
            parameters,
            sha1, sha1, prior.StoredOverallSha1, prior.ComputedOverallSha1,
            change == "decoder" ? new(prior.Decoder.Rid, prior.Decoder.UpstreamCommit, prior.Decoder.RecipeVersion + 1) : prior.Decoder,
            change == "measurement-profile" ? "foreign-measurement/v1" : prior.MeasurementProfile,
            change == "verifier-build" ? "foreign-verifier" : prior.VerifierBuild,
            change == "measurement-time" ? prior.MeasurementVerifiedAt.AddTicks(1) : prior.MeasurementVerifiedAt, prior.Profile, prior.ObservedAt);
        Should.Throw<ArgumentException>(() => new PreparedRom.WholeFile(facts.Artifact, "disc.iso", facts.Output.ContentSha256,
            facts.Prepared.Hashes, RomRepresentationEncoding.Chd, facts.Output, facts.Container, encodingProfile: facts.Prepared.EncodingProfile,
            chdIntegrity: changed, chdPs2Boot: facts.Prepared.ChdPs2Boot));
    }

    [Fact]
    public async Task RecordMeasurementAsync_ZeroCrc_IsRetainedAsMeasuredData()
    {
        var fixture = new Fixture();
        var prior = fixture.Facts.Prepared.Measurement;
        var identity = prior.Outputs.Single().Identity;
        var zero = new RomLogicalIdentity(identity.Size, identity.Sha256, identity.Sha1, identity.Md5, Crc32.FromUInt32(0));
        var proof = new RomRepresentationVerification(Guid.NewGuid(), prior.ArtifactSha256, prior.ArtifactSize, prior.Decoder, prior.CheckedProfile, prior.VerifierBuild,
            prior.ObservedAt, [new(RomOutputSelector.Whole.Instance, zero)], fixture.Facts.Container, prior.ComputedRawSha1, prior.ComputedOverallSha1);
        var receipt = fixture.Receipt with { VerificationId = proof.Id };
        fixture.Roms.RecordWholeMeasurementStagedAsync(7, null, fixture.Facts.Container, proof, default).Returns(receipt);
        (await fixture.Writer.RecordMeasurementAsync(7, fixture.Facts.Container, proof, null, default)).Value.ShouldBe(receipt);
        proof.Outputs.Single().Identity.Crc32.IsEmpty.ShouldBeFalse();
        proof.Outputs.Single().Identity.Crc32.ShouldBe(Crc32.FromUInt32(0));
        await fixture.Roms.Received(1).RecordWholeMeasurementStagedAsync(7, null, fixture.Facts.Container, proof, default);
    }

    private sealed class Fixture
    {
        internal static readonly Error Refused = Error.Conflict("Fixture.RawFactRefused", "Controlled required-fact refusal.");
        public readonly IRomRepository Roms = Substitute.For<IRomRepository>();
        public readonly IUnitOfWork Work = Substitute.For<IUnitOfWork>();
        public readonly IRomMediaObservationStore Observations = Substitute.For<IRomMediaObservationStore>();
        public readonly IRomMediaCreationStore Creations = Substitute.For<IRomMediaCreationStore>();
        public readonly PublisherChdFacts Facts;
        public readonly RomRetainedMeasurement Receipt;
        public readonly FileStoreResult Published;
        public readonly RomRepresentationWriter Writer;
        public readonly List<string> Events = [];
        public string? Refuse, ThrowAt;
        public readonly IOException Failure = new("original current-port fault");
        public Fixture(RomRepresentationEncoding encoding = RomRepresentationEncoding.Chd, bool created = false)
        {
            Facts = new(created, encoding);
            Receipt = new(12, 102, 2, Facts.Prepared.Measurement.Id);
            Published = new(PublisherChdFacts.File(proof: Facts.Output), true, false);
            Writer = new(Roms, Work, Observations, Creations);
            Roms.RecordWholeMeasurementStagedAsync(7, Arg.Any<int?>(), Facts.Container, Facts.Prepared.Measurement, Arg.Any<CancellationToken>()).Returns(_ =>
            { Event("measurement"); return Refuse == "measurement" ? null : Receipt; });
            Observations.StageAsync(7, Receipt, Facts.Prepared.Inspection!, Arg.Any<CancellationToken>()).Returns(_ =>
            { Event("observation"); return Task.FromResult<ErrorOr<Success>>(Refuse == "observation" ? Refused : Result.Success); });
            Creations.StageAsync(7, Receipt, Arg.Any<RomSourceEquivalence>(), Arg.Any<RomChdEncodingEvidence?>(), Arg.Any<CancellationToken>()).Returns(_ =>
            { Event("creation"); return Task.FromResult<ErrorOr<Success>>(Refuse == "creation" ? Refused : Result.Success); });
            Work.When(port => port.FlushAsync(Arg.Any<CancellationToken>())).Do(_ => Event("flush"));
            Roms.When(port => port.SetPreferredRepresentationStagedAsync(7, Arg.Any<int>(), Arg.Any<CancellationToken>())).Do(_ => Event("prefer"));
            Roms.When(port => port.ReleaseRepresentationsStagedAsync(7, Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())).Do(_ => Event("release"));
        }
        private void Event(string value) { Events.Add(value); if (ThrowAt == value) throw Failure; }
        public async Task NoPromotionAsync()
        {
            await Work.DidNotReceiveWithAnyArgs().FlushAsync(default);
            await Roms.DidNotReceiveWithAnyArgs().SetPreferredRepresentationStagedAsync(default, default, default);
            await Roms.DidNotReceiveWithAnyArgs().ReleaseRepresentationsStagedAsync(default, default!, default);
        }
    }
}
