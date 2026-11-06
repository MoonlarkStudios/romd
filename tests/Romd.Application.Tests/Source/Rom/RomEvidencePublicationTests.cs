using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Security;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Domain.Storage;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class RomEvidencePublicationTests
{
    private static readonly Sha256 Logical = Sha256.Parse(new string('a', 64));
    private static readonly Sha256 OutputArtifact = Sha256.Parse(new string('b', 64));
    private static readonly Sha256 OtherArtifact = Sha256.Parse(new string('e', 64));
    private static readonly DatHashResult Hashes = new(Sha1.Parse(new string('c', 40)), Md5.Parse(new string('d', 32)), Crc32.FromUInt32(12), 100);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_RetainedMeasurement_RawObservationAndCreationPrecedeSourceRelease(bool attach)
    {
        var fixture = new Fixture(attach);
        var prepared = Prepare(OutputArtifact, 19);
        (await fixture.PublishAsync(prepared)).Value.FileId.ShouldBe(2);
        fixture.Changed.ShouldBeTrue();
        fixture.Events.ShouldBe(["measurement", "observation", "creation", "flush", "prefer", "release", "apply", "flush", "commit"]);
        await fixture.Observations.Received(1).StageAsync(7, Arg.Is<RomRetainedMeasurement>(value => value.OutputId == 102 && value.VerificationId == prepared.Measurement.Id), prepared.Inspection!, Arg.Any<CancellationToken>());
        await fixture.Creations.Received(1).StageAsync(7, Arg.Is<RomRetainedMeasurement>(value => value.OutputId == 102), prepared.SourceEquivalence!, null, Arg.Any<CancellationToken>());
        await fixture.Roms.Received(1).RecordWholeMeasurementStagedAsync(7, attach ? 2 : null, prepared.Container, prepared.Measurement, Arg.Any<CancellationToken>());
        await fixture.Roms.Received(1).ReleaseRepresentationsStagedAsync(7, Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 101 })), Arg.Any<CancellationToken>());
        if (attach)
            await fixture.Files.Received(1).PublishStagedAsync(fixture.Stage, Arg.Any<CancellationToken>());
        else
            await fixture.Files.DidNotReceiveWithAnyArgs().StageAsync(default(ITempFile)!, default);
        await fixture.Transaction.DidNotReceiveWithAnyArgs().RollbackAsync(default);
    }

    [Theory]
    [InlineData(19)]
    [InlineData(12)]
    public async Task PublishAsync_EqualOrWeakerDifferentArtifact_SkipsFactsAndOwnershipChanges(int incomingLevel)
    {
        var fixture = new Fixture(false);
        (await fixture.PublishAsync(Prepare(OtherArtifact, incomingLevel))).Value.FileId.ShouldBe(2);
        fixture.Changed.ShouldBeFalse();
        fixture.Events.ShouldBe(["apply", "flush", "commit"]);
        await fixture.Roms.DidNotReceiveWithAnyArgs().RecordWholeMeasurementStagedAsync(default, default, default!, default!, default);
        fixture.Observations.ReceivedCalls().ShouldBeEmpty();
        fixture.Creations.ReceivedCalls().ShouldBeEmpty();
        await fixture.Roms.DidNotReceiveWithAnyArgs().ReleaseRepresentationsStagedAsync(default, default!, default);
        await fixture.Files.DidNotReceiveWithAnyArgs().StageAsync(default(ITempFile)!, default);
    }

    [Theory]
    [InlineData("measurement")]
    [InlineData("observation")]
    [InlineData("creation")]
    public async Task PublishAsync_RequiredFactRefused_RollsBackBeforeSourceReleaseOrApply(string refused)
    {
        var fixture = new Fixture(false) { Refuse = refused };
        var result = await fixture.PublishAsync(Prepare(OutputArtifact, 19));
        result.IsError.ShouldBeTrue();
        result.FirstError.ShouldBe(refused == "measurement" ? RomEligibilityErrors.MeasurementNotRetained() : Fixture.Refused);
        await fixture.Transaction.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
        await fixture.Transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
        await fixture.Work.DidNotReceiveWithAnyArgs().FlushAsync(default);
        await fixture.Roms.DidNotReceiveWithAnyArgs().ReleaseRepresentationsStagedAsync(default, default!, default);
        fixture.Events.ShouldNotContain("apply");
        if (refused != "creation")
            fixture.Creations.ReceivedCalls().ShouldBeEmpty();
    }

    private static PreparedRom.WholeFile Prepare(Sha256 artifact, int level)
    {
        var source = new RomVerification(Logical, 100, RomRepresentationEncoding.Identity, Logical, 100, Hashes.Sha1, Hashes.Md5, Hashes.Crc32,
            "identity-output/v1", "fixture-hasher", "fixture-verifier", DateTimeOffset.UnixEpoch);
        var output = new RomVerification(artifact, 20, RomRepresentationEncoding.Rvz, Logical, 100, Hashes.Sha1, Hashes.Md5, Hashes.Crc32,
            "complete-rvz-output/v1", "fixture-output-decoder/current", "fixture-verifier", DateTimeOffset.UnixEpoch);
        RomSourceEquivalence.TryCreate(source, output, "fixture-canonical/v1", "fixture-encoder/current", DateTimeOffset.UnixEpoch, out var comparison).ShouldBeTrue();
        var eligibility = new RomIsoEligibility(Logical, 100, "nintendo-iso", "nintendo-iso/v1", "fixture-inspector", RomIsoEligibilityStatus.Supported,
            RomIsoDiscKind.GameCube, 0, 0, 0, DateTimeOffset.UnixEpoch);
        byte[] prefix = new byte[32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(0x1c), 0xc2339f3d);
        var inspection = new RomLogicalInspection(Guid.NewGuid(), output.Identity, "structure/v1", "fixture-inspector", DateTimeOffset.UnixEpoch, prefix, new RomNintendoLayoutFacts(0, 0, 0));
        return new(new Artifact(artifact), "disc.iso", Logical, Hashes, RomRepresentationEncoding.Rvz, output, Container(level),
            eligibility: eligibility, sourceEquivalence: comparison, inspection: inspection);
    }
    private static RomContainerFacts.Rvz Container(int level) => new(0x01000000, 1, 5, level, 131072, false, 100);
    private sealed class Fixture
    {
        internal static readonly Error Refused = Error.Conflict("Fixture.RequiredFactRefused", "Controlled complete-fact refusal.");
        internal readonly IRomRepository Roms = Substitute.For<IRomRepository>();
        internal readonly IFileStorageService Files = Substitute.For<IFileStorageService>();
        internal readonly IUnitOfWork Work = Substitute.For<IUnitOfWork>();
        internal readonly ITransaction Transaction = Substitute.For<ITransaction>();
        internal readonly IRomMediaObservationStore Observations = Substitute.For<IRomMediaObservationStore>();
        internal readonly IRomMediaCreationStore Creations = Substitute.For<IRomMediaCreationStore>();
        internal readonly IStagedFile Stage = Substitute.For<IStagedFile>();
        internal readonly List<string> Events = [];
        internal bool Changed; internal string? Refuse;
        private readonly RomFile existing; private RomFile current;
        private readonly RomRepresentationPublisher publisher;
        internal Fixture(bool attach)
        {
            existing = current = Rom(attach ? 1 : 2);
            var original = new RetainedRepresentation(11, 101, 1, Logical, RomContainerFacts.Identity.Instance, attach, true);
            var encoded = new RetainedRepresentation(12, 102, 2, OutputArtifact, Container(19), true, true);
            Roms.ListRepresentationsAsync(7, Arg.Any<CancellationToken>()).Returns(attach ? [original] : [original, encoded]);
            Roms.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(_ => current);
            Roms.RecordWholeMeasurementStagedAsync(7, Arg.Any<int?>(), Arg.Any<RomContainerFacts>(), Arg.Any<RomRepresentationVerification>(), Arg.Any<CancellationToken>()).Returns(call =>
            { Events.Add("measurement"); return Refuse == "measurement" ? null : new RomRetainedMeasurement(12, 102, 2, call.Arg<RomRepresentationVerification>().Id); });
            Observations.StageAsync(7, Arg.Any<RomRetainedMeasurement>(), Arg.Any<RomLogicalInspection>(), Arg.Any<CancellationToken>()).Returns(_ =>
            { Events.Add("observation"); return Task.FromResult<ErrorOr<Success>>(Refuse == "observation" ? Refused : Result.Success); });
            Creations.StageAsync(7, Arg.Any<RomRetainedMeasurement>(), Arg.Any<RomSourceEquivalence>(), null, Arg.Any<CancellationToken>()).Returns(_ =>
            { Events.Add("creation"); return Task.FromResult<ErrorOr<Success>>(Refuse == "creation" ? Refused : Result.Success); });
            Roms.When(port => port.SetPreferredRepresentationStagedAsync(7, 102, Arg.Any<CancellationToken>())).Do(_ => { Events.Add("prefer"); current = Rom(2); });
            Roms.When(port => port.ReleaseRepresentationsStagedAsync(7, Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())).Do(_ => Events.Add("release"));
            Work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Transaction);
            Work.When(port => port.FlushAsync(Arg.Any<CancellationToken>())).Do(_ => Events.Add("flush"));
            Transaction.When(port => port.CommitAsync(Arg.Any<CancellationToken>())).Do(_ => Events.Add("commit"));
            Files.StageAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>()).Returns(Stage);
            var publication = Substitute.For<IStagedFilePublication>();
            publication.ReadAfterFlush().Returns(new FileStoreResult(FileEntity.Rehydrate(2, OutputArtifact, 20, 20, false, DateTimeOffset.UnixEpoch), true, false));
            Files.PublishStagedAsync(Stage, Arg.Any<CancellationToken>()).Returns(publication);
            var audit = Substitute.For<IAuditContext>();
            audit.ActorId.Returns(Romd.Domain.Identity.SystemActor.UserId);
            audit.IsSystem.Returns(true);
            publisher = new(Files, Work, Roms, TestRomMediaPolicies.Create(), TestRomEncodings.Create(), Substitute.For<IFileMutationLock>(), null,
                Substitute.For<IMediaFactPublisher>(), Observations, Creations, audit, TimeProvider.System, Substitute.For<Romd.Admin.Application.Source.Rom.Reconciliation.IMediaReconciliationRequests>(), Substitute.For<IRomPs2CurrentReferenceAuthority>());
        }
        internal Task<ErrorOr<RomFile>> PublishAsync(PreparedRom.WholeFile prepared) => publisher.PublishAsync(prepared, existing, true,
            (_, changed, _) => { Changed = changed; Events.Add("apply"); return Task.CompletedTask; }, _ => null, default);
        private static RomFile Rom(int file) => RomFile.Rehydrate(7, "disc.iso", file, 100, Hashes.Sha1, Hashes.Md5, Hashes.Crc32, DateTimeOffset.UnixEpoch, Logical,
            file == 1 ? RomRepresentationEncoding.Identity : RomRepresentationEncoding.Rvz);
    }
    private sealed class Artifact(Sha256 hash) : ITempFile
    {
        public string Path => "unused-evidence-fixture"; public Sha256 FileSha256 => hash; public long FileSize => 20;
        public Stream OpenRead() => throw new InvalidOperationException("Publication uses the staging port.");
        public bool TryBeginChildUse() => false; public bool TryEndChildUse() => false; public bool Quarantine() => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
