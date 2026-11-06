using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Application.Common.Security;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Domain.Storage;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class RomRepresentationPublisherChdTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("conflicting")]
    [InlineData("unavailable")]
    [InlineData("provenance")]
    [InlineData("earlier-agreeing")]
    public async Task PublishAsync_CurrentAuthorityChanges_RefusesBeforePublishingFacts(string change)
    {
        var fixture = new Fixture(true, false) { ReferenceChange = change };
        var result = await fixture.PublishAsync();
        result.IsError.ShouldBeTrue();
        fixture.Events.ShouldContain("authority");
        fixture.Events.ShouldContain("rollback");
        fixture.Events.LastIndexOf("file-lock").ShouldBeLessThan(fixture.Events.IndexOf("authority"));
        foreach (string forbidden in new[] { "publish", "measurement", "observation", "creation", "flush", "prefer", "release", "apply", "commit" })
            fixture.Events.ShouldNotContain(forbidden);
        fixture.Events.Count(value => value == "dispose-stage").ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_PendingBoot_RetainsRawLogicalObservationWithoutCurrentDvdAuthority(bool chd)
    {
        var fixture = new Fixture(chd, false, pending: true, originalPs2: !chd,
            encoding: chd ? RomRepresentationEncoding.Chd : RomRepresentationEncoding.Identity);
        var result = await fixture.PublishAsync();
        result.IsError.ShouldBeFalse();
        fixture.Facts.Prepared.Eligibility.ShouldBeNull();
        fixture.Facts.Prepared.Inspection.ShouldNotBeNull().Layout.ShouldBe(PublisherChdFacts.Boot());
        await fixture.Observations.Received(1).StageAsync(7, fixture.Receipt, fixture.Facts.Prepared.Inspection!, Arg.Any<CancellationToken>());
        fixture.Events.ShouldNotContain("authority");
        fixture.Events.ShouldNotContain("creation");
        fixture.Events.ShouldContain("commit");
        fixture.Events.IndexOf("measurement").ShouldBeLessThan(fixture.Events.IndexOf("observation"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PublishAsync_CompleteChdFactsAndCreation_PrecedePreferenceAndOwnerRelease(bool evidenceOnly, bool created)
    {
        var fixture = new Fixture(evidenceOnly, created);
        var result = await fixture.PublishAsync();
        result.IsError.ShouldBeFalse();
        result.Value.FileId.ShouldBe(2);
        foreach (string required in new[] { "stage-raw", "begin", "rom-lock", "file-lock", "authority", "publish", "measurement", "observation", "flush", "prefer", "release", "apply", "commit" })
            fixture.Events.ShouldContain(required);
        fixture.Events.IndexOf("stage-raw").ShouldBeLessThan(fixture.Events.IndexOf("begin"));
        fixture.Events.LastIndexOf("file-lock").ShouldBeLessThan(fixture.Events.IndexOf("authority"));
        fixture.Events.IndexOf("authority").ShouldBeLessThan(fixture.Events.IndexOf("publish"));
        fixture.Events.IndexOf("publish").ShouldBeLessThan(fixture.Events.IndexOf("measurement"));
        fixture.Events.IndexOf("measurement").ShouldBeLessThan(fixture.Events.IndexOf("observation"));
        fixture.Events.IndexOf("observation").ShouldBeLessThan(fixture.Events.IndexOf("flush"));
        fixture.Events.IndexOf("flush").ShouldBeLessThan(fixture.Events.IndexOf("prefer"));
        fixture.Events.IndexOf("prefer").ShouldBeLessThan(fixture.Events.IndexOf("release"));
        fixture.Events.IndexOf("release").ShouldBeLessThan(fixture.Events.IndexOf("apply"));
        fixture.Events.IndexOf("apply").ShouldBeLessThan(fixture.Events.IndexOf("commit"));
        await fixture.Roms.Received(1).RecordWholeMeasurementStagedAsync(7, evidenceOnly ? null : 2, fixture.Facts.Container,
            fixture.Facts.Prepared.Measurement, Arg.Any<CancellationToken>());
        fixture.Facts.Prepared.Measurement.Decoder.ShouldBeOfType<RomDecoderIdentity.Native>().Build.ShouldBe(fixture.Facts.Integrity!.Decoder);
        fixture.Facts.Prepared.Measurement.ObservedParametersSha256.ShouldBe(fixture.Facts.Container.Fingerprint);
        fixture.Facts.Prepared.Measurement.ComputedRawSha1.ShouldBe(fixture.Facts.Integrity.ComputedRawSha1);
        if (created)
        {
            fixture.Events.IndexOf("observation").ShouldBeLessThan(fixture.Events.IndexOf("creation"));
            fixture.Events.IndexOf("creation").ShouldBeLessThan(fixture.Events.IndexOf("flush"));
            await fixture.Creations.Received(1).StageAsync(7, fixture.Receipt, fixture.Facts.Comparison!, fixture.Facts.EncodingEvidence, Arg.Any<CancellationToken>());
        }
        else
            fixture.Events.ShouldNotContain("creation");
        fixture.Events.Count(value => value == "dispose-stage").ShouldBe(1);
        fixture.Facts.Artifact.ReadCalls.ShouldBe(0);
        fixture.Facts.Artifact.DisposeCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false, "measurement")]
    [InlineData(true, "measurement")]
    [InlineData(false, "observation")]
    [InlineData(true, "observation")]
    [InlineData(false, "creation")]
    [InlineData(true, "creation")]
    public async Task PublishAsync_RequiredFactRefused_RollsBackBeforePromotion(bool evidenceOnly, string refused)
    {
        var fixture = new Fixture(evidenceOnly, true) { Refuse = refused };
        var result = await fixture.PublishAsync();
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(refused == "measurement" ? RomEligibilityErrors.MeasurementNotRetained().Code : Fixture.Refused.Code);
        fixture.Events.ShouldContain("rollback");
        fixture.Events.Count(value => value == "dispose-stage").ShouldBe(1);
        foreach (string forbidden in new[] { "flush", "prefer", "release", "apply", "commit" })
            fixture.Events.ShouldNotContain(forbidden);
        if (refused == "measurement")
            fixture.Events.ShouldNotContain("observation");
        if (refused != "creation")
            fixture.Events.ShouldNotContain("creation");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PublishAsync_CompressedStorageRefusal_PreservesBorrowedArtifactAndCleansStages(bool evidenceOnly, bool atPublish)
    {
        var fixture = new Fixture(evidenceOnly, false) { CompressedAt = atPublish ? "publish" : "stage" };
        await Should.ThrowAsync<NotSupportedException>(() => fixture.PublishAsync());
        fixture.Events.ShouldContain("stage-raw");
        foreach (string forbidden in new[] { "measurement", "observation", "creation", "flush", "prefer", "release", "apply", "commit" })
            fixture.Events.ShouldNotContain(forbidden);
        if (atPublish)
        { fixture.Events.ShouldContain("rollback"); fixture.Events.Count(value => value == "dispose-stage").ShouldBe(1); }
        else
            fixture.Events.ShouldNotContain("begin");
        fixture.Facts.Artifact.DisposeCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(true, "file-id")]
    [InlineData(true, "hash")]
    [InlineData(true, "size")]
    [InlineData(true, "compressed")]
    [InlineData(true, "disk-size")]
    [InlineData(false, "hash")]
    [InlineData(false, "size")]
    [InlineData(false, "compressed")]
    [InlineData(false, "disk-size")]
    public async Task PublishAsync_PhysicalReplyMismatch_RefusesBeforeAnyFacts(bool evidenceOnly, string changed)
    {
        var fixture = new Fixture(evidenceOnly, true) { ReplyChanged = changed };
        (await fixture.PublishAsync()).FirstError.Code.ShouldBe(RomPublicationErrors.RetryRequiredCode);
        fixture.Events.ShouldContain("rollback");
        foreach (string forbidden in new[] { "measurement", "observation", "creation", "flush", "prefer", "release", "apply", "commit" })
            fixture.Events.ShouldNotContain(forbidden);
        fixture.Events.Count(value => value == "dispose-stage").ShouldBe(1);
        fixture.Facts.Artifact.ReadCalls.ShouldBe(0);
        fixture.Facts.Artifact.DisposeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task PublishAsync_ExactLowerRankArtifact_RetainsNewProofAndPreservesStrongerOutputAndRawParameters()
    {
        var fixture = new Fixture(true, false, canonical: false, stronger: true);
        var fingerprint = fixture.Rows.Single(row => row.FileId == 2).Container.Fingerprint;
        (await fixture.PublishAsync()).Value.FileId.ShouldBe(3);
        await fixture.Roms.Received(1).SetPreferredRepresentationStagedAsync(7, 103, Arg.Any<CancellationToken>());
        await fixture.Roms.Received(1).ReleaseRepresentationsStagedAsync(7, Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 0), Arg.Any<CancellationToken>());
        fixture.Rows.Single(row => row.FileId == 2).Container.Fingerprint.ShouldBe(fingerprint);
        fixture.Events.ShouldContain("measurement");
        fixture.Events.ShouldContain("observation");
        fixture.Events.ShouldContain("commit");
    }

    [Fact]
    public async Task PublishAsync_LockedDecisionNeedsAnUnstagedRawArtifact_RetriesWithoutWritingFacts()
    {
        var fixture = new Fixture(true, false);
        var preferred = PublisherChdFacts.Retained(3, PublisherChdFacts.OtherArtifact, fixture.Facts.Container, true, true);
        int reads = 0;
        fixture.Roms.ListRepresentationsAsync(7, Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromResult<IReadOnlyList<RetainedRepresentation>>(++reads == 1 ? [preferred] : fixture.Rows));
        (await fixture.PublishAsync()).FirstError.Code.ShouldBe(RomPublicationErrors.RetryRequiredCode);
        foreach (string forbidden in new[] { "stage-raw", "publish", "measurement", "flush", "prefer", "release", "apply", "commit" })
            fixture.Events.ShouldNotContain(forbidden);
        fixture.Events.ShouldContain("rollback");
    }

    [Fact]
    public async Task PublishAsync_DiscardedIncoming_LeavesFactsAndOwnershipAlone()
    {
        var fixture = new Fixture(true, true);
        (await fixture.PublishAsync(false)).IsError.ShouldBeFalse();
        foreach (string forbidden in new[] { "stage-raw", "stage-default", "publish", "measurement", "observation", "creation", "prefer", "release" })
            fixture.Events.ShouldNotContain(forbidden);
        fixture.Events.ShouldContain("apply");
        fixture.Events.ShouldContain("commit");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_Rvz_UsesManagedMeasurementAndOptionalCreationWithoutNativeProvenance(bool created)
    {
        var fixture = new Fixture(false, created, encoding: RomRepresentationEncoding.Rvz);
        (await fixture.PublishAsync()).Value.StorageEncoding.ShouldBe(RomRepresentationEncoding.Rvz);
        fixture.Events.ShouldContain("stage-default");
        fixture.Events.ShouldNotContain("stage-raw");
        fixture.Facts.Prepared.Measurement.Decoder.ShouldBeOfType<RomDecoderIdentity.Managed>();
        if (created)
            await fixture.Creations.Received(1).StageAsync(7, fixture.Receipt, fixture.Facts.Comparison!, null, Arg.Any<CancellationToken>());
        else
            fixture.Events.ShouldNotContain("creation");
        fixture.Events.ShouldContain("commit");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PublishAsync_RawRequirement_ComesFromTheEncodingDescriptor(bool raw, bool compressed)
    {
        var fixture = new Fixture(false, false, encoding: RomRepresentationEncoding.Rvz,
            encodings: raw ? TestRomEncodings.RequiringRaw(RomRepresentationEncoding.Rvz) : TestRomEncodings.Create())
        { ReplyChanged = compressed ? "compressed" : null };
        var result = await fixture.PublishAsync();
        fixture.Events.ShouldContain(raw ? "stage-raw" : "stage-default");
        fixture.Events.ShouldNotContain(raw ? "stage-default" : "stage-raw");
        if (raw && compressed)
        { result.FirstError.Code.ShouldBe(RomPublicationErrors.RetryRequiredCode); fixture.Events.ShouldNotContain("measurement"); }
        else
        { result.IsError.ShouldBeFalse(); fixture.Events.ShouldContain("commit"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_FileIdentity_FlushesOnlyPendingPublicationBeforeMeasurement(bool pendingFile)
    {
        var fixture = new Fixture(false, true, pendingFile: pendingFile);

        (await fixture.PublishAsync()).IsError.ShouldBeFalse();

        int measurement = fixture.Events.IndexOf("measurement");
        int firstFlush = fixture.Events.IndexOf("flush");
        measurement.ShouldBeGreaterThan(fixture.Events.IndexOf("publish"));
        (firstFlush < measurement).ShouldBe(pendingFile);
        fixture.Events.Count(value => value == "flush").ShouldBe(pendingFile ? 3 : 2);
    }

    [Fact]
    public async Task PublishAsync_FileIdentityFlushFails_RollsBackBeforeMeasurementAndCreation()
    {
        var fixture = new Fixture(false, true, pendingFile: true)
        { OnFlush = () => throw new IOException("File identity flush failed.") };

        await Should.ThrowAsync<IOException>(() => fixture.PublishAsync());

        fixture.Events.ShouldContain("rollback");
        fixture.Events.ShouldNotContain("measurement");
        fixture.Events.ShouldNotContain("creation");
        fixture.Events.ShouldNotContain("commit");
        fixture.Events.ShouldContain("dispose-stage");
    }

    private sealed class Fixture
    {
        internal static readonly Error Refused = Error.Validation("Fixture.RawFactRefused", "Controlled current-fact refusal.");
        public readonly IRomRepository Roms = Substitute.For<IRomRepository>();
        public readonly IRomMediaObservationStore Observations = Substitute.For<IRomMediaObservationStore>();
        public readonly IRomMediaCreationStore Creations = Substitute.For<IRomMediaCreationStore>();
        public readonly List<string> Events = [];
        public readonly PublisherChdFacts Facts;
        public readonly IReadOnlyList<RetainedRepresentation> Rows;
        public readonly RomRetainedMeasurement Receipt;
        private readonly RomRepresentationPublisher publisher;
        private RomFile current;
        public string? Refuse, CompressedAt, ReplyChanged, ReferenceChange;
        public Action? OnFlush;
        public Fixture(bool evidenceOnly, bool created, bool canonical = true, bool stronger = false,
            RomRepresentationEncoding encoding = RomRepresentationEncoding.Chd, bool pending = false,
            bool originalPs2 = false, RomEncodingRegistry? encodings = null, bool pendingFile = false)
        {
            Facts = new(created, encoding, canonical ? PublisherChdFacts.CanonicalProfile : PublisherChdFacts.OtherProfile, pendingReference: pending, originalPs2: originalPs2);
            var original = PublisherChdFacts.Retained(1, PublisherChdFacts.Content, RomContainerFacts.Identity.Instance, !stronger, true);
            var incoming = PublisherChdFacts.Retained(2, Facts.Output.ArtifactSha256, Facts.Container, false, true);
            Rows = originalPs2 ? [original] : stronger
                ? [PublisherChdFacts.Retained(3,PublisherChdFacts.OtherArtifact,PublisherChdFacts.ChdContainer(PublisherChdFacts.MakeIntegrity(
                    PublisherChdFacts.Verification(RomRepresentationEncoding.Chd,PublisherChdFacts.OtherArtifact,4096,PublisherChdFacts.CanonicalProfile))),true,true),incoming]
                : evidenceOnly ? [original, incoming] : [original];
            int file = originalPs2 ? 1 : 2;
            Receipt = new(file + 10, file + 100, file, Facts.Prepared.Measurement.Id);
            current = PublisherChdFacts.Rom(stronger ? 3 : 1, encoding);
            var files = Substitute.For<IFileStorageService>();
            var work = Substitute.For<IUnitOfWork>();
            var transaction = Substitute.For<ITransaction>();
            var stage = Substitute.For<IStagedFile>();
            var locks = Substitute.For<IFileMutationLock>();
            Roms.ListRepresentationsAsync(7, Arg.Any<CancellationToken>()).Returns(Rows);
            Roms.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(_ => current);
            Roms.When(port => port.LockRepresentationSetAsync(7, Arg.Any<CancellationToken>())).Do(_ => Events.Add("rom-lock"));
            files.StageRawAsync(Facts.Artifact, Arg.Any<CancellationToken>()).Returns(_ =>
            { Events.Add("stage-raw"); Events.ShouldNotContain("begin"); if (CompressedAt == "stage") throw new NotSupportedException("Compressed existing raw object."); return stage; });
            files.StageAsync(Facts.Artifact, Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("stage-default"); return stage; });
            work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("begin"); return transaction; });
            locks.When(port => port.AcquireAsync(Arg.Any<Sha256>(), Arg.Any<CancellationToken>())).Do(_ => Events.Add("file-lock"));
            var publication = Substitute.For<IStagedFilePublication>();
            publication.RequiresFlush.Returns(pendingFile);
            publication.ReadAfterFlush().Returns(_ => new FileStoreResult(PublisherChdFacts.File(ReplyChanged,
                proof: originalPs2 ? Facts.Output : null), !evidenceOnly, evidenceOnly));
            files.PublishStagedAsync(stage, Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Events.Add("publish");
                Events.ShouldContain("file-lock");
                if (CompressedAt == "publish")
                    throw new NotSupportedException("Compressed publication winner.");
                return publication;
            });
            Roms.RecordWholeMeasurementStagedAsync(7, Arg.Any<int?>(), Facts.Container, Facts.Prepared.Measurement, Arg.Any<CancellationToken>()).Returns(_ =>
            { Events.Add("measurement"); return Refuse == "measurement" ? null : Receipt; });
            Observations.StageAsync(7, Receipt, Facts.Prepared.Inspection!, Arg.Any<CancellationToken>()).Returns(_ =>
            { Events.Add("observation"); return Task.FromResult<ErrorOr<Success>>(Refuse == "observation" ? Refused : Result.Success); });
            Creations.StageAsync(7, Receipt, Arg.Any<RomSourceEquivalence>(), Arg.Any<RomChdEncodingEvidence?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Events.Add("creation");
                call.Arg<RomSourceEquivalence>().ShouldBeSameAs(Facts.Comparison);
                call.Arg<RomChdEncodingEvidence?>().ShouldBeSameAs(Facts.EncodingEvidence);
                return Task.FromResult<ErrorOr<Success>>(Refuse == "creation" ? Refused : Result.Success);
            });
            work.When(port => port.FlushAsync(Arg.Any<CancellationToken>())).Do(_ => { Events.Add("flush"); OnFlush?.Invoke(); });
            Roms.When(port => port.SetPreferredRepresentationStagedAsync(7, Arg.Any<int>(), Arg.Any<CancellationToken>())).Do(call =>
            { Events.Add("prefer"); current = PublisherChdFacts.Rom(call.ArgAt<int>(1) - 100, encoding); });
            Roms.When(port => port.ReleaseRepresentationsStagedAsync(7, Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())).Do(_ => Events.Add("release"));
            transaction.When(port => port.CommitAsync(Arg.Any<CancellationToken>())).Do(_ => Events.Add("commit"));
            transaction.When(port => port.RollbackAsync(Arg.Any<CancellationToken>())).Do(_ => Events.Add("rollback"));
            stage.When(port => port.DisposeAsync()).Do(_ => Events.Add("dispose-stage"));
            var authority = Substitute.For<IRomPs2CurrentReferenceAuthority>();
            authority.ResolveAsync(Facts.Output.Identity, Arg.Any<RomPs2BootLayoutFacts>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                Events.Add("authority");
                call.Arg<RomPs2BootLayoutFacts>().ShouldBe(Facts.Prepared.Ps2BootEvidence!.Boot);
                return await PublisherChdFacts.CurrentResolutionAsync(Facts.Output, call.Arg<RomPs2BootLayoutFacts>(), call.Arg<DateTimeOffset>(),
                    Facts.Prepared.Eligibility, ReferenceChange, call.Arg<CancellationToken>());
            });
            var audit = Substitute.For<IAuditContext>();
            audit.ActorId.Returns(Romd.Domain.Identity.SystemActor.UserId);
            audit.IsSystem.Returns(true);
            publisher = new(files, work, Roms, PublisherChdFacts.Policies(encoding == RomRepresentationEncoding.Identity ? RomRepresentationEncoding.Rvz : encoding),
                encodings ?? TestRomEncodings.Create(), locks, null, Substitute.For<IMediaFactPublisher>(), Observations, Creations,
                audit, TimeProvider.System, requests: Substitute.For<Romd.Admin.Application.Source.Rom.Reconciliation.IMediaReconciliationRequests>(), ps2Authority: authority);
        }
        public Task<ErrorOr<RomFile>> PublishAsync(bool retain = true) => publisher.PublishAsync(Facts.Prepared, current, retain,
            (_, _, _) => { Events.Add("apply"); return Task.CompletedTask; }, _ => null, default);
    }
}

// Synthetic complete inputs isolate application publication ordering from persistence and native qualification.
internal sealed class PublisherChdFacts
{
    internal const string CanonicalProfile = "dvd;unit=2048;hunk=16384;codec=zlib;processors=1";
    internal const string OtherProfile = "dvd;unit=2048;hunk=16384;codec=lzma;processors=1";
    internal const string Recipe = "chd-dvd-experiment/v1";
    internal const long LogicalBytes = 64 * 2048;
    internal static readonly Sha256 Content = Sha256.Parse(new string('a', 64));
    internal static readonly Sha256 OutputArtifact = Sha256.Parse(new string('b', 64));
    internal static readonly Sha256 OtherArtifact = Sha256.Parse(new string('e', 64));
    internal static readonly Sha1 LogicalSha1 = Sha1.Parse(new string('c', 40));
    internal static readonly Md5 LogicalMd5 = Md5.Parse(new string('d', 32));
    internal static readonly Crc32 LogicalCrc = Crc32.FromUInt32(123);
    internal static readonly DateTimeOffset MeasuredAt = DateTimeOffset.UnixEpoch.AddTicks(1234);
    internal readonly RomVerification Source;
    internal readonly RomVerification Output;
    internal readonly RomChdIntegrity? Integrity;
    internal readonly RomSourceEquivalence? Comparison;
    internal readonly RomChdEncodingEvidence? EncodingEvidence;
    internal readonly TestArtifact Artifact;
    internal readonly PreparedRom.WholeFile Prepared;
    internal RomContainerFacts Container => Prepared.Container;

    internal PublisherChdFacts(bool created, RomRepresentationEncoding encoding = RomRepresentationEncoding.Chd,
        string profile = CanonicalProfile, RomVerification? source = null, bool pendingReference = false, bool originalPs2 = false)
    {
        if (encoding == RomRepresentationEncoding.Identity)
            profile = RomEncodingProfiles.Identity;
        Source = source ?? Verification(RomRepresentationEncoding.Identity, Content, LogicalBytes, RomEncodingProfiles.Identity, -1);
        Output = originalPs2 ? OriginalPs2(0) : Verification(encoding, OutputArtifact, 4096, profile);
        Artifact = new(Output);
        var boot = encoding == RomRepresentationEncoding.Chd || originalPs2 ? Boot() : null;
        Integrity = encoding == RomRepresentationEncoding.Chd ? MakeIntegrity(Output) : null;
        if (created)
        {
            RomSourceEquivalence.TryCreate(Source, Output, Recipe,
                encoding == RomRepresentationEncoding.Chd ? Encoder().BuildLabel : "legacy-encoder",
                MeasuredAt.AddTicks(20), out var comparison).ShouldBeTrue();
            Comparison = comparison.ShouldNotBeNull();
            if (encoding == RomRepresentationEncoding.Chd)
            {
                RomChdEncodingEvidence.TryCreate(Comparison, Output, Integrity, RecipeId.Parse(Recipe), Encoder(),
                    MeasuredAt.AddTicks(30), out var evidence).ShouldBeTrue();
                EncodingEvidence = evidence.ShouldNotBeNull();
            }
        }
        var container = encoding switch
        {
            RomRepresentationEncoding.Identity => (RomContainerFacts)RomContainerFacts.Identity.Instance,
            RomRepresentationEncoding.Rvz => RvzContainer(profile == CanonicalProfile ? 19 : 12),
            _ => ChdContainer(Integrity!)
        };
        byte[] prefix = new byte[32];
        if (encoding == RomRepresentationEncoding.Rvz)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(0x1c), 0xc2339f3d);
        var inspection = new RomLogicalInspection(Guid.NewGuid(), Output.Identity, "fixture-structure/v1", "fixture-structure-inspector", MeasuredAt, prefix,
            encoding == RomRepresentationEncoding.Rvz ? new RomNintendoLayoutFacts(0, 0, 0) : boot);
        Prepared = new(Artifact, "disc.iso", Content, new(LogicalSha1, LogicalMd5, LogicalCrc, LogicalBytes), encoding,
            Output, container, encodingProfile: profile, eligibility: pendingReference ? null : Eligibility(Output, encoding), sourceEquivalence: Comparison,
            ps2Observation: originalPs2 ? new(Output, Boot()) : null,
            chdIntegrity: Integrity, chdEncodingEvidence: EncodingEvidence, chdPs2Boot: encoding == RomRepresentationEncoding.Chd ? boot : null, inspection: inspection);
    }

    internal static RomVerification Verification(RomRepresentationEncoding encoding, Sha256 artifact, long bytes,
        string profile, long ticks = 0) => new(artifact, bytes, encoding, Content, LogicalBytes,
        LogicalSha1, LogicalMd5, LogicalCrc, encoding == RomRepresentationEncoding.Chd ? "chd-import/v1" : "legacy/v1",
        encoding == RomRepresentationEncoding.Chd ? Decoder().BuildLabel : "legacy-decoder", "verifier/mvid:synthetic",
        MeasuredAt.AddTicks(ticks), profile);

    internal static RomVerification OriginalPs2(long ticks = -1) => new(Content, LogicalBytes,
        RomRepresentationEncoding.Identity, Content, LogicalBytes, LogicalSha1, LogicalMd5, LogicalCrc,
        RomPs2BootObservation.MeasurementProfile, Boot().InspectorBuild, "verifier/mvid:synthetic",
        MeasuredAt.AddTicks(ticks), RomEncodingProfiles.Identity);

    internal static RomChdIntegrity MakeIntegrity(RomVerification proof)
    {
        RomChdIntegrity.TryCreate(proof, Parameters(proof.CapturedEncodingProfile!), proof.Sha1, proof.Sha1,
            Sha1.Parse(new string('f', 40)), Sha1.Parse(new string('f', 40)), Decoder(), RomChdIntegrity.ProfileVersion,
            MeasuredAt.AddTicks(10), out var fact).ShouldBeTrue();
        return fact.ShouldNotBeNull();
    }

    internal static RomChdIntegrity ChangeIntegrity(RomChdIntegrity fact, string changed) => RomChdIntegrity.Rehydrate(
        changed == "fact-id" ? Guid.NewGuid() : fact.Id,
        changed == "verification-id" ? Guid.NewGuid() : fact.VerificationId, fact.ArtifactSha256, fact.ArtifactSize,
        fact.ContentSha256, fact.ContentSize, changed == "sha1" ? Sha1.Parse(new string('e', 40)) : fact.Sha1,
        changed == "md5" ? Md5.Parse(new string('e', 32)) : fact.Md5,
        changed == "crc32" ? Crc32.FromUInt32(124) : fact.Crc32,
        changed == "container-profile" ? Parameters(fact.Parameters.CapturedEncodingProfile == OtherProfile
            ? CanonicalProfile : OtherProfile) : changed == "container-codec"
            ? new RomChdContainerParameters(5, LogicalBytes, 16384, 2048, 8, 0x6c7a6d61, 0, 0, 0, false,
                fact.Parameters.Metadata, fact.Parameters.CapturedEncodingProfile) : fact.Parameters,
        changed == "sha1" ? Sha1.Parse(new string('e', 40)) : fact.StoredRawSha1,
        changed == "sha1" ? Sha1.Parse(new string('e', 40)) : fact.ComputedRawSha1,
        changed == "overall" ? Sha1.Parse(new string('e', 40)) : fact.StoredOverallSha1,
        changed == "overall" ? Sha1.Parse(new string('e', 40)) : fact.ComputedOverallSha1,
        fact.Decoder, fact.MeasurementProfile, fact.VerifierBuild,
        changed == "ticks" ? fact.MeasurementVerifiedAt.AddTicks(1) : fact.MeasurementVerifiedAt, fact.Profile,
        changed == "observed-at" ? fact.ObservedAt.AddTicks(1) : fact.ObservedAt);

    internal static RomChdContainerParameters Parameters(string profile) => new(5, LogicalBytes, 16384, 2048, 8,
        profile == OtherProfile ? 0x6c7a6d61U : 0x7a6c6962U, 0, 0, 0, false,
        new(1, 0x44564420, 1, 1, 0, RomChdDvdMetadataFacts.ProfileVersion, RomChdDvdMetadataFacts.SingletonFingerprint), profile);

    private static RomChdDecoderBuild Decoder() => new("linux-arm64", Sha1.Parse(new string('e', 40)), 1);
    private static RomChdEncoderBuild Encoder() => new("linux-arm64", "mame0289", Content, 1);

    internal static RomPs2BootLayoutFacts Boot() => new(LogicalBytes, 64, 16, 24, 2048, 25, 60, 26, 512,
        "cdrom0:\\SLUS_203.12;1", "cdrom0:\\SLUS_203.12;1", "SLUS_203.12", 0x00100000, 1,
        RomPs2BootLayoutFacts.ProfileVersion, "ps2-inspector/mvid:synthetic");

    internal static RomIsoEligibility Eligibility(RomVerification proof, RomRepresentationEncoding encoding)
    {
        if (encoding != RomRepresentationEncoding.Chd)
            return new(Content, LogicalBytes, "nintendo-iso", "nintendo-iso/v1", "inspector",
                RomIsoEligibilityStatus.Supported, RomIsoDiscKind.GameCube, 0, 0, 0, MeasuredAt);
        var boot = Boot();
        var reference = new RomDvdMediaReference(Guid.Parse("11111111-2222-3333-4444-555555555555"),
            proof.ContentSha256, proof.ContentSize, proof.Sha1, proof.Md5, proof.Crc32, RomDvdMediaKind.Dvd5,
            "dvd-reference/v1", OtherArtifact, "synthetic-independent-reference");
        var details = new RomPs2DvdEligibilityFacts(proof.ContentSha256, proof.ContentSize, proof.Sha1, proof.Md5,
            proof.Crc32, boot, reference);
        return new(Content, LogicalBytes, "ps2-iso", RomPs2DvdEligibilityFacts.ProfileVersion, boot.InspectorBuild,
            RomIsoEligibilityStatus.Supported, RomIsoDiscKind.PlayStation2Dvd, 0, 0, 0, MeasuredAt, details);
    }

    internal static async Task<ErrorOr<RomPs2DvdEligibilityResolution>> CurrentResolutionAsync(RomVerification measurement,
        RomPs2BootLayoutFacts boot, DateTimeOffset inspectedAt, RomIsoEligibility? supplied, string? change, CancellationToken ct)
    {
        var provenance = (supplied ?? Eligibility(measurement, RomRepresentationEncoding.Chd)).Ps2DvdFacts!.DvdReference;
        var original = new RomDiscMediaReferenceRecord(provenance.ReferenceRecordId, RomDiscReferenceSystem.PlayStation2,
            provenance.MediaKind == RomDvdMediaKind.Dvd5 ? RomDiscReferenceMedia.Dvd5 : RomDiscReferenceMedia.Dvd9,
            1, measurement.ContentSize, measurement.Sha1, measurement.Md5, measurement.Crc32, 32,
            provenance.ReferenceSha256, provenance.ReferenceSource, provenance.Profile, "accepted-synthetic", MeasuredAt);
        var earlier = new RomDiscMediaReferenceRecord(Guid.Parse("00000000-1111-2222-3333-444444444444"),
            RomDiscReferenceSystem.PlayStation2, change == "conflicting" ? RomDiscReferenceMedia.Dvd9 : RomDiscReferenceMedia.Dvd5,
            1, measurement.ContentSize, measurement.Sha1, measurement.Md5, measurement.Crc32, 32,
            Sha256.Parse(new string('0', 63) + "1"), "earlier-independent-reference", "earlier-reference/v1", "accepted-synthetic", MeasuredAt.AddDays(1));
        IReadOnlyList<RomDiscMediaReferenceRecord> rows = change switch
        {
            "missing" => [],
            "conflicting" or "earlier-agreeing" => [earlier, original],
            "provenance" => [earlier],
            _ => [original]
        };
        var store = Substitute.For<IRomDiscMediaReferenceStore>();
        store.FindAsync(Arg.Any<RomDiscMediaReferenceQuery>(), RomPs2DvdEligibilityResolver.MaximumReferenceRecords + 1,
            Arg.Any<CancellationToken>()).Returns(Task.FromResult<ErrorOr<IReadOnlyList<RomDiscMediaReferenceRecord>>>(
                change == "unavailable" ? RomDiscMediaReferenceErrors.Unavailable() : ErrorOrFactory.From(rows)));
        return await new RomPs2DvdEligibilityResolver(store).ResolveAsync(measurement.Identity, boot, inspectedAt.AddHours(1), ct);
    }

    internal static FileEntity File(string? changed = null, int id = 2, RomVerification? proof = null)
    {
        var hash = proof?.ArtifactSha256 ?? OutputArtifact;
        long bytes = proof?.ArtifactSize ?? 4096;
        return FileEntity.Rehydrate(changed == "file-id" ? 999 : id, changed == "hash" ? OtherArtifact : hash,
            changed == "size" ? bytes + 1 : bytes, changed == "disk-size" ? bytes - 1 : bytes,
            changed == "compressed", MeasuredAt);
    }

    internal static RomContainerFacts.Rvz RvzContainer(int level) => new(0x01000000, 1, 5, level, 131072, false, LogicalBytes);
    internal static RomContainerFacts.Chd ChdContainer(RomChdIntegrity integrity)
    {
        var parameters = integrity.Parameters;
        return new(parameters.Version, parameters.LogicalBytes, parameters.HunkBytes, parameters.UnitBytes, parameters.HunkCount,
            [parameters.Codec0, parameters.Codec1, parameters.Codec2, parameters.Codec3], parameters.HasParent, integrity.StoredRawSha1, integrity.StoredOverallSha1,
            [new(0x44564420, 1, new byte[] { 0 })]);
    }
    internal static RetainedRepresentation Retained(int file, Sha256 hash, RomContainerFacts container, bool preferred, bool measured) =>
        new(file + 10, file + 100, file, hash, container, preferred, measured);

    internal static RomFile Rom(int preferredFile, RomRepresentationEncoding encoded = RomRepresentationEncoding.Chd) => RomFile.Rehydrate(7, "disc.iso", preferredFile, LogicalBytes,
        LogicalSha1, LogicalMd5, LogicalCrc, MeasuredAt, Content,
        preferredFile == 1 ? RomRepresentationEncoding.Identity : encoded);

    internal static RomMediaPolicyRegistry Policies(RomRepresentationEncoding encoding) => new([new Policy(encoding)]);
    private sealed class Policy(RomRepresentationEncoding encoding) : IRomMediaTypePolicy, IRomMediaEncodingProfile
    {
        public IRomMediaEncodingProfile EncodingProfile => this;
        public bool ObservesEncodingLayout(RomLogicalInspection inspection) => false;
        public RomIsoDiscKind DiscKind => encoding == RomRepresentationEncoding.Chd ? RomIsoDiscKind.PlayStation2Dvd : RomIsoDiscKind.GameCube;
        public string PlatformKey => encoding == RomRepresentationEncoding.Chd ? "ps2" : "gc";
        public RomRepresentationEncoding StoredEncoding => encoding;
        public string RecipeVersion => Recipe;
        public RomRepresentationProfileClass Classify(RomContainerFacts container) => container switch
        {
            RomContainerFacts.Chd chd => chd.Codecs[0] == 0x7a6c6962 ? RomRepresentationProfileClass.Canonical : RomRepresentationProfileClass.NonCanonical,
            RomContainerFacts.Rvz rvz => rvz.CompressionLevel == 19 ? RomRepresentationProfileClass.Canonical : RomRepresentationProfileClass.NonCanonical,
            _ => RomRepresentationProfileClass.Unknown
        };
        public bool MatchesFacts(RomContentDescriptor content) => content.Artifacts.Any(artifact => artifact.HasMeasuredEvidence && artifact.Encoding == encoding);
        public bool IsEncodingEligible(RomContentDescriptor content, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return MatchesFacts(content);
        }
        public long GetMaximumOutputBytes(long logicalBytes, long maxLogicalBytes, CancellationToken ct = default) => logicalBytes;
    }

    internal sealed class TestArtifact(RomVerification proof) : ITempFile
    {
        internal int ReadCalls;
        internal int DisposeCalls;
        public string Path => "unused-publisher-fixture";
        public Sha256 FileSha256 => proof.ArtifactSha256;
        public long FileSize => proof.ArtifactSize;
        public Stream OpenRead() { ReadCalls++; throw new InvalidOperationException("Publication must use staging port."); }
        public bool TryBeginChildUse() => throw new InvalidOperationException();
        public bool TryEndChildUse() => throw new InvalidOperationException();
        public bool Quarantine() => throw new InvalidOperationException();
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }
}
