using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Application.Common.Security;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Domain.Storage;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class RomPublicationRecipeTests
{
    [Theory]
    [InlineData(RomRepresentationEncoding.Identity, false, false)]
    [InlineData(RomRepresentationEncoding.Identity, true, false)]
    [InlineData(RomRepresentationEncoding.Rvz, false, false)]
    [InlineData(RomRepresentationEncoding.Identity, false, true)]
    [InlineData(RomRepresentationEncoding.Identity, true, true)]
    [InlineData(RomRepresentationEncoding.Rvz, false, true)]
    public async Task PublishAsync_RetainedMedia_StagesAuditedDemandWithoutCapturingSourceOrPolicy(RomRepresentationEncoding encoding, bool eligible, bool system)
    {
        var fixture = new Fixture(Prepared(encoding, eligible), SharedRecipes());
        fixture.Audit.IsSystem.Returns(system);
        (await fixture.PublishAsync()).IsError.ShouldBeFalse();
        var demand = fixture.Captured.ShouldNotBeNull();
        demand.Subject.ShouldBe(new MediaReconciliationSubject.LogicalRom(7));
        demand.ConversionRequested.ShouldBeFalse();
        demand.Cause.ShouldBe(MediaReconciliationCause.Import);
        demand.ActorId.ShouldBe(system ? null : fixture.Actor);
        demand.SelectionJobId.ShouldBeNull();
        fixture.Events.IndexOf("flush").ShouldBeLessThan(fixture.Events.IndexOf("request"));
        fixture.Events.IndexOf("request").ShouldBeLessThan(fixture.Events.IndexOf("commit"));
    }

    [Fact]
    public async Task PublishAsync_EncodedPolicyCannotResolve_RefusesBeforeStorageOrTransaction()
    {
        var fixture = new Fixture(Prepared(RomRepresentationEncoding.Rvz, false), ConflictingRecipes());
        (await fixture.PublishAsync()).FirstError.Code.ShouldBe(RomPublicationErrors.AmbiguousProfile.Code);
        fixture.Events.ShouldBeEmpty();
        fixture.Facts.ReceivedCalls().ShouldBeEmpty();
        fixture.Intents.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_InspectedCanonicalKind_UsesItsPhysicalRecipeForPromotion(bool existing)
    {
        var fixture = new Fixture(Prepared(RomRepresentationEncoding.Rvz, true, canonical: true), ConflictingRecipes(), existing: existing, retainedLevel: 12);
        (await fixture.PublishAsync()).Value.FileId.ShouldBe(3);
        await fixture.Roms.Received(1).SetPreferredRepresentationStagedAsync(7, 103, Arg.Any<CancellationToken>());
        await fixture.Roms.Received(1).ReleaseRepresentationsStagedAsync(7,
            Arg.Is<IReadOnlyList<int>>(ids => existing ? ids.Count == 1 && ids[0] == 102 : ids.Count == 0), Arg.Any<CancellationToken>());
        fixture.Captured.ShouldNotBeNull().Cause.ShouldBe(MediaReconciliationCause.Import);
        fixture.Events.IndexOf("measurement").ShouldBeLessThan(fixture.Events.IndexOf("prefer"));
    }

    [Theory]
    [InlineData("missing-kind-policy")]
    [InlineData("ambiguous")]
    [InlineData("unsupported-eligibility")]
    public async Task PublishAsync_UnboundPolicy_RejectsBeforeMutation(string change)
    {
        var prepared = Prepared(RomRepresentationEncoding.Rvz, change != "ambiguous", supported: change != "unsupported-eligibility", canonical: true);
        var recipes = ConflictingRecipes();
        if (change == "missing-kind-policy")
            recipes = new([recipes.Policies.Single(policy => policy.DiscKind == RomIsoDiscKind.Wii)]);
        var fixture = new Fixture(prepared, recipes);
        (await fixture.PublishAsync()).FirstError.Code.ShouldBe(RomPublicationErrors.AmbiguousProfile.Code);
        fixture.Events.ShouldBeEmpty();
        fixture.Facts.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_InspectedKindPreservesCanonical_RefusesEqualOrWeakerDifferentArtifact(bool incomingCanonical)
    {
        var fixture = new Fixture(Prepared(RomRepresentationEncoding.Rvz, true, canonical: incomingCanonical), ConflictingRecipes(), existing: true, retainedLevel: 19);
        (await fixture.PublishAsync()).Value.FileId.ShouldBe(2);
        fixture.Events.ShouldNotContain("publish");
        fixture.Events.ShouldNotContain("measurement");
        fixture.Events.ShouldNotContain("prefer");
        fixture.Events.ShouldNotContain("release");
        fixture.Facts.ReceivedCalls().ShouldBeEmpty();
        fixture.Captured.ShouldNotBeNull().Cause.ShouldBe(MediaReconciliationCause.Import);
        fixture.Events.ShouldContain("commit");
    }

    [Fact]
    public async Task PublishAsync_NewLogicalIdentity_UsesAuditActorAndCompleteMeasuredOutputBeforePreference()
    {
        var prepared = Prepared(RomRepresentationEncoding.Identity, false);
        var fixture = new Fixture(prepared, SharedRecipes());
        (await fixture.PublishAsync()).Value.Id.ShouldBe(7);
        await fixture.Facts.Received(1).CreateLogicalAsync(prepared.LogicalFilename, prepared.Measurement.Outputs.Single().Identity,
            fixture.Actor, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        fixture.Events.IndexOf("publish").ShouldBeLessThan(fixture.Events.IndexOf("logical"));
        fixture.Events.IndexOf("logical").ShouldBeLessThan(fixture.Events.IndexOf("measurement"));
        fixture.Events.IndexOf("measurement").ShouldBeLessThan(fixture.Events.IndexOf("prefer"));
        fixture.Events.IndexOf("prefer").ShouldBeLessThan(fixture.Events.IndexOf("commit"));
    }

    private static PreparedRom.WholeFile Prepared(RomRepresentationEncoding encoding, bool eligible, bool supported = true, bool canonical = false)
    {
        var logical = Sha256.Parse(new string('e', 64));
        var artifactHash = encoding == RomRepresentationEncoding.Identity ? logical : Sha256.Parse(new string('a', 64));
        long physical = encoding == RomRepresentationEncoding.Identity ? 1024 : 256;
        var sha1 = Sha1.Parse(new string('b', 40));
        var md5 = Md5.Parse(new string('c', 32));
        var crc = Crc32.FromUInt32(1);
        var artifact = Substitute.For<ITempFile>();
        artifact.FileSha256.Returns(artifactHash);
        artifact.FileSize.Returns(physical);
        var proof = new RomVerification(artifactHash, physical, encoding, logical, 1024, sha1, md5, crc,
            "complete-output/v1", "fixture-managed-decoder", "fixture-verifier", DateTimeOffset.UnixEpoch);
        var eligibility = eligible ? new RomIsoEligibility(logical, 1024, "fixture", "fixture/v1", "fixture-inspector",
            supported ? RomIsoEligibilityStatus.Supported : RomIsoEligibilityStatus.Unsupported, supported ? RomIsoDiscKind.GameCube : null, 0, 0, 0, DateTimeOffset.UnixEpoch) : null;
        RomContainerFacts container = encoding == RomRepresentationEncoding.Identity ? RomContainerFacts.Identity.Instance : Rvz(canonical ? 19 : 12);
        return new(artifact, "disc.iso", logical, new(sha1, md5, crc, 1024), encoding, proof, container, eligibility: eligibility);
    }
    private static RomContainerFacts.Rvz Rvz(int level) => new(0x01000000, 1, 5, level, 131072, false, 1024);
    private static IRomMediaTypePolicy Policy(RomIsoDiscKind kind, string recipe, bool inverted = false)
    {
        var policy = Substitute.For<IRomMediaTypePolicy>();
        policy.DiscKind.Returns(kind);
        policy.PlatformKey.Returns(kind == RomIsoDiscKind.GameCube ? "gc" : "wii");
        policy.EncodingProfile.StoredEncoding.Returns(RomRepresentationEncoding.Rvz);
        policy.EncodingProfile.RecipeVersion.Returns(recipe);
        policy.EncodingProfile.Classify(Arg.Any<RomContainerFacts>()).Returns(call => call.Arg<RomContainerFacts>() is RomContainerFacts.Rvz rvz
            ? (rvz.CompressionLevel == (inverted ? 12 : 19) ? RomRepresentationProfileClass.Canonical : RomRepresentationProfileClass.NonCanonical)
            : RomRepresentationProfileClass.Unknown);
        policy.MatchesFacts(Arg.Any<RomContentDescriptor>()).Returns(call => call.Arg<RomContentDescriptor>().Artifacts.Any(artifact =>
            artifact.HasMeasuredEvidence && artifact.Container is RomContainerFacts.Rvz rvz && rvz.DiscType == (kind == RomIsoDiscKind.GameCube ? 1 : 2)));
        return policy;
    }
    private static RomMediaPolicyRegistry SharedRecipes() =>
        new([Policy(RomIsoDiscKind.GameCube, "shared-current/v1"), Policy(RomIsoDiscKind.Wii, "shared-current/v1")]);
    private static RomMediaPolicyRegistry ConflictingRecipes() => new([Policy(RomIsoDiscKind.GameCube, "gamecube-current/v1"), Policy(RomIsoDiscKind.Wii, "unrelated-wii/v1", true)]);

    private sealed class Fixture
    {
        public readonly IRomRepository Roms = Substitute.For<IRomRepository>();
        public readonly IMediaFactPublisher Facts = Substitute.For<IMediaFactPublisher>();
        public readonly IMediaReconciliationRequests Intents = Substitute.For<IMediaReconciliationRequests>();
        public readonly IAuditContext Audit = Substitute.For<IAuditContext>();
        public readonly Guid Actor = Guid.Parse("12345678-1111-2222-3333-123456789012");
        public readonly List<string> Events = [];
        public MediaReconciliationDemand? Captured;
        private readonly RomRepresentationPublisher publisher;
        private readonly PreparedRom.WholeFile prepared;
        private readonly RomFile? existing;
        public Fixture(PreparedRom.WholeFile prepared, RomMediaPolicyRegistry recipes, bool existing = false, int retainedLevel = 12)
        {
            this.prepared = prepared;
            var logical = prepared.Measurement.Outputs.Single().Identity;
            RomFile Rom(int file) => RomFile.Rehydrate(7, prepared.LogicalFilename, file, logical.Size, logical.Sha1, logical.Md5, logical.Crc32, DateTimeOffset.UnixEpoch,
                logical.Sha256, file == 2 ? RomRepresentationEncoding.Rvz : prepared.Encoding);
            this.existing = existing ? Rom(2) : null;
            var current = this.existing ?? Rom(3);
            var files = Substitute.For<IFileStorageService>();
            var work = Substitute.For<IUnitOfWork>();
            var transaction = Substitute.For<ITransaction>();
            var stage = Substitute.For<IStagedFile>();
            Audit.ActorId.Returns(Actor);
            Audit.IsSystem.Returns(false);
            files.StageAsync(prepared.Artifact, Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("stage"); return stage; });
            var publication = Substitute.For<IStagedFilePublication>();
            var fileResult = new FileStoreResult(FileEntity.Rehydrate(3, prepared.Artifact.FileSha256,
                prepared.Artifact.FileSize, prepared.Artifact.FileSize, false, DateTimeOffset.UnixEpoch), true, false);
            publication.ReadAfterFlush().Returns(fileResult);
            files.PublishStagedAsync(stage, Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("publish"); return publication; });
            work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("begin"); return transaction; });
            Facts.CreateLogicalAsync(prepared.LogicalFilename, logical, Actor, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("logical"); return 7; });
            Roms.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(_ => current);
            Roms.ListRepresentationsAsync(7, Arg.Any<CancellationToken>()).Returns(existing
                ? new[] { new RetainedRepresentation(12, 102, 2, Sha256.Parse(new string('d', 64)), Rvz(retainedLevel), true, true) } : []);
            Roms.RecordWholeMeasurementStagedAsync(7, 3, prepared.Container, prepared.Measurement, Arg.Any<CancellationToken>()).Returns(_ =>
            { Events.Add("measurement"); return new RomRetainedMeasurement(13, 103, 3, prepared.Measurement.Id); });
            work.When(port => port.FlushAsync(Arg.Any<CancellationToken>())).Do(_ => Events.Add("flush"));
            Roms.When(port => port.SetPreferredRepresentationStagedAsync(7, 103, Arg.Any<CancellationToken>())).Do(_ => { Events.Add("prefer"); current = Rom(3); });
            Roms.When(port => port.ReleaseRepresentationsStagedAsync(7, Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())).Do(_ => Events.Add("release"));
            transaction.When(port => port.CommitAsync(Arg.Any<CancellationToken>())).Do(_ => Events.Add("commit"));
            Intents.StageSubjectAsync(Arg.Any<MediaReconciliationDemand>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Events.Add("request");
                Captured = call.Arg<MediaReconciliationDemand>();
                var job = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SubjectWork(Captured.Subject), "subject", null, Captured.ActorId, DateTimeOffset.UtcNow);
                return Task.FromResult<ErrorOr<MediaReconciliationAcceptance>>(new MediaReconciliationAcceptance(job, true, false));
            });
            publisher = new(files, work, Roms, recipes, TestRomEncodings.Create(), Substitute.For<IFileMutationLock>(), null, Facts,
                Substitute.For<IRomMediaObservationStore>(), Substitute.For<IRomMediaCreationStore>(), Audit, TimeProvider.System, Intents, Substitute.For<IRomPs2CurrentReferenceAuthority>());
        }
        public Task<ErrorOr<RomFile>> PublishAsync() => publisher.PublishAsync(prepared, existing, true, (_, _, _) => Task.CompletedTask, _ => null, default);
    }
}
