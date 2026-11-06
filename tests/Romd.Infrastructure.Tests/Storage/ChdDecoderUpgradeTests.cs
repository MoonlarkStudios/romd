using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Application.Common.Security;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Domain.Storage;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Media.PlayStation;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>A new decoder tuple requalifies a stored CHD only through an exact re-upload of the retained artifact.</summary>
/// <remarks>The publisher, planner, read admission and serving authority are real; repositories, CAS, libchdr and the
/// resident builds are synthetic. After the re-upload the catalog returns the earlier evidence plus the publisher's staged
/// rows, which the database keeps immutable.</remarks>
public sealed class ChdDecoderUpgradeTests : IAsyncLifetime
{
    private readonly ChdDeliveryFixture _disc = ChdDeliveryFixture.Create();
    private readonly IRomContentCatalog _catalog = Substitute.For<IRomContentCatalog>();
    private readonly IContentAddressableStore _cas = Substitute.For<IContentAddressableStore>();
    private readonly DecodingAdapter _libchdr;
    private readonly List<RomRepresentationDecoder> _decoders = [];
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() { foreach (var decoder in _decoders) await decoder.DisposeAsync(); }

    public ChdDecoderUpgradeTests() => _libchdr = new(_disc.LogicalBytes);

    [Theory]
    [InlineData("rid")]
    [InlineData("pin")]
    [InlineData("recipe")]
    public async Task ExactReupload_AnotherDecoderTuple_AppendsEvidenceAndBothTuplesServe(string changed)
    {
        var upgraded = Change(_disc.Native, changed);
        Serve(_disc.Descriptor);
        var issuedBeforeUpgrade = Targets(_disc.Descriptor, _disc.Native);

        // Only T1 evidence: the T2 host advertises neither format, although the generic measured flag is set, and it
        // refuses the targets a T1 host issued. The CAS holds the bytes, so a bypass would serve them.
        _disc.Artifact.HasMeasuredEvidence.ShouldBeTrue();
        RomDeliveryPlanner.Plan(_disc.Descriptor, "disc.iso", RomMediaPolicyComposition.CreateDeliveryPolicy(), null, Admission(upgraded))
            .Where(option => option.Format is RomDeliveryFormat.Original or RomDeliveryFormat.Chd)
            .ShouldAllBe(option => option.State == RomDeliveryState.Unsupported && option.Target == null);
        var t2Reader = Reader(upgraded);
        (await t2Reader.OpenAsync(issuedBeforeUpgrade[RomDeliveryFormat.Original])).ShouldBeNull();
        (await t2Reader.OpenAsync(issuedBeforeUpgrade[RomDeliveryFormat.Chd])).ShouldBeNull();
        _cas.ReceivedCalls().ShouldBeEmpty();

        var reuploaded = await ReuploadAsync(upgraded);
        ((RomDecoderIdentity.Native)reuploaded.Decoder).Build.ShouldBe(ChdBuildEvidence.Decoder(upgraded));
        reuploaded.Id.ShouldNotBe(_disc.Evidence.Measurement.Id);
        var prior = _disc.Artifact.Facts;
        var facts = new RomRepresentationFacts(prior.Id, prior.FileId, prior.ArtifactSha256, prior.ArtifactSize, prior.Container, prior.Manifest,
            prior.Outputs, [.. prior.Verifications, reuploaded], prior.Descriptors);
        var artifact = new RomContentArtifact(facts, _disc.Artifact.OutputId, true, _disc.Artifact.ArtifactMd5, _disc.Artifact.ArtifactSha1);
        var retained = new RomContentDescriptor(_disc.Descriptor.RomId, artifact.OutputId, _disc.Descriptor.Identity, "disc.iso", [artifact], [], []);
        var appended = artifact.ChdEvidence.Single(evidence => evidence.Measurement.Id == reuploaded.Id);
        var historical = artifact.ChdEvidence.Single(evidence => evidence.Measurement.Id == _disc.Evidence.Measurement.Id);
        Serve(retained);
        await ShouldServeBothFormatsAsync(retained, upgraded, appended);
        await ShouldServeBothFormatsAsync(retained, _disc.Native, historical);
        // Every issued target binds its exact proof. A new host must issue a new target after re-verification.
        foreach (var target in issuedBeforeUpgrade.Values)
            (await t2Reader.OpenAsync(target)).ShouldBeNull();
        facts.Verifications.Count.ShouldBe(2);
    }

    private async Task ShouldServeBothFormatsAsync(RomContentDescriptor retained, ChdNativeIdentity resident,
        RomChdDeliveryEvidence expected)
    {
        var reader = Reader(resident);
        foreach (var (format, target) in Targets(retained, resident))
        {
            target.ReadBinding.ShouldBe(expected.Bind());
            await using var stream = (await reader.OpenAsync(target)).ShouldNotBeNull();
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            output.ToArray().ShouldBe(format == RomDeliveryFormat.Original ? _disc.LogicalBytes : _disc.RawBytes);
        }
        _libchdr.Admitted[^1].Bind().ShouldBe(expected.Bind());
    }

    private static Dictionary<RomDeliveryFormat, RomDeliveryTarget> Targets(RomContentDescriptor content,
        ChdNativeIdentity resident) => RomDeliveryPlanner.Plan(content, "disc.iso", RomMediaPolicyComposition.CreateDeliveryPolicy(), null, Admission(resident))
        .Where(option => option.Format is RomDeliveryFormat.Original or RomDeliveryFormat.Chd)
        .ToDictionary(option => option.Format, option =>
        {
            option.State.ShouldBe(RomDeliveryState.Available);
            return option.Target.ShouldNotBeNull();
        });

    /// <summary>A worker whose verified libchdr has the given tuple re-uploads the exact retained CHD.</summary>
    private async Task<RomRepresentationVerification> ReuploadAsync(ChdNativeIdentity worker)
    {
        var decoder = ChdBuildEvidence.Decoder(worker);
        var old = _disc.Evidence.Measurement;
        var identity = _disc.Descriptor.Identity;
        var container = (RomContainerFacts.Chd)_disc.Artifact.Container;
        string label = _disc.Artifact.EncodingProfile;
        var measurement = new RomVerification(old.ArtifactSha256, old.ArtifactSize, RomRepresentationEncoding.Chd,
            identity.Sha256, identity.Size, identity.Sha1, identity.Md5, identity.Crc32, old.CheckedProfile, decoder.BuildLabel,
            old.VerifierBuild, old.ObservedAt.AddDays(1), label);
        var entry = container.Metadata.Single();
        var metadata = new RomChdDvdMetadataFacts(1, entry.Tag, entry.Flags, (uint)entry.Payload.Length, entry.Payload[0],
            RomChdDvdMetadataFacts.ProfileVersion, container.MetadataSha256);
        var parameters = new RomChdContainerParameters(container.Version, container.LogicalBytes, container.HunkBytes, container.UnitBytes,
            container.HunkCount, container.Codecs[0], container.Codecs[1], container.Codecs[2], container.Codecs[3], container.HasParent, metadata, label);
        RomChdIntegrity.TryCreate(measurement, parameters, container.StoredRawSha1, container.StoredRawSha1, container.StoredOverallSha1,
            container.StoredOverallSha1, decoder, RomChdIntegrity.ProfileVersion, measurement.VerifiedAt.AddTicks(1), out var integrity).ShouldBeTrue();
        await using var prepared = new PreparedRom.WholeFile(new Upload(measurement), "disc.iso", identity.Sha256,
            new(identity.Sha1, identity.Md5, identity.Crc32, identity.Size), RomRepresentationEncoding.Chd, measurement, container,
            encodingProfile: label, chdIntegrity: integrity);
        var artifact = _disc.Artifact;
        var rom = RomFile.Rehydrate(_disc.Descriptor.RomId, "disc.iso", artifact.FileId, identity.Size, identity.Sha1, identity.Md5,
            identity.Crc32, old.ObservedAt, identity.Sha256, RomRepresentationEncoding.Chd);
        var roms = Substitute.For<IRomRepository>();
        roms.GetByIdAsync(rom.Id, Arg.Any<CancellationToken>()).Returns(rom);
        roms.ListRepresentationsAsync(rom.Id, Arg.Any<CancellationToken>()).Returns(new[]
            { new RetainedRepresentation(artifact.RepresentationId,artifact.OutputId,artifact.FileId,artifact.ArtifactSha256,container,true,true) });
        RomRepresentationVerification? stagedMeasurement = null;
        roms.RecordWholeMeasurementStagedAsync(rom.Id, null, Arg.Any<RomContainerFacts>(), Arg.Do<RomRepresentationVerification>(value => stagedMeasurement = value),
            Arg.Any<CancellationToken>()).Returns(_ => new RomRetainedMeasurement(artifact.RepresentationId, artifact.OutputId, artifact.FileId, prepared.Measurement.Id));
        var files = Substitute.For<IFileStorageService>();
        var staged = Substitute.For<IStagedFile>();
        files.StageRawAsync(prepared.Artifact, Arg.Any<CancellationToken>()).Returns(staged);
        var publication = Substitute.For<IStagedFilePublication>();
        publication.ReadAfterFlush().Returns(new FileStoreResult(FileEntity.Rehydrate(
            artifact.FileId, artifact.ArtifactSha256, artifact.ArtifactSize, artifact.ArtifactSize, false, old.ObservedAt), false, true));
        files.PublishStagedAsync(staged, Arg.Any<CancellationToken>()).Returns(publication);
        var work = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        var facts = Substitute.For<IMediaFactPublisher>();
        var observations = Substitute.For<IRomMediaObservationStore>();
        var creations = Substitute.For<IRomMediaCreationStore>();
        var audit = Substitute.For<IAuditContext>();
        audit.ActorId.Returns(Romd.Domain.Identity.SystemActor.UserId);
        audit.IsSystem.Returns(true);
        var publisher = new RomRepresentationPublisher(files, work, roms, new RomMediaPolicyRegistry([new ChdDvdMediaTypePolicy("ps2")]),
            RomEncodingComposition.CreateRegistry(), Substitute.For<IFileMutationLock>(), null, facts, observations, creations, audit, TimeProvider.System, Substitute.For<Romd.Admin.Application.Source.Rom.Reconciliation.IMediaReconciliationRequests>(), Substitute.For<IRomPs2CurrentReferenceAuthority>());
        var published = await publisher.PublishAsync(prepared, rom, true, (_, _, _) => Task.CompletedTask, _ => null, CancellationToken.None);
        published.IsError.ShouldBeFalse();
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        facts.ReceivedCalls().ShouldBeEmpty();
        observations.ReceivedCalls().ShouldBeEmpty();
        creations.ReceivedCalls().ShouldBeEmpty();
        await roms.Received(1).SetPreferredRepresentationStagedAsync(rom.Id, artifact.OutputId, Arg.Any<CancellationToken>());
        await roms.DidNotReceive().ReleaseRepresentationsStagedAsync(rom.Id, Arg.Is<IReadOnlyList<int>>(ids => ids.Count > 0), Arg.Any<CancellationToken>());
        var retained = stagedMeasurement.ShouldNotBeNull();
        retained.ObservedParametersSha256.ShouldBe(container.Fingerprint);
        retained.Outputs.ShouldHaveSingleItem().Identity.ShouldBe(identity);
        return retained;
    }

    private void Serve(RomContentDescriptor current)
    {
        var key = StorageKey.FromHash(current.ArtifactSha256);
        _catalog.FindByRomIdAsync(current.RomId, Arg.Any<CancellationToken>()).Returns(current);
        _cas.OpenRandomAccessAsync(key, Arg.Any<CancellationToken>()).Returns(_ => new Bytes(_disc.RawBytes));
        _cas.OpenSynchronousRaw(key).Returns(_ => new ChdObservedRaw(_disc.RawBytes));
    }

    private RomContentReader Reader(ChdNativeIdentity resident)
    {
        var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new RvzMediaAdapter(), new IdentityMediaAdapter(), _libchdr),
            new() { MaxConcurrent = 1, MaxIdle = 0 });
        _decoders.Add(decoder);
        return new(_catalog, _cas, decoder, Admission(resident));
    }

    /// <summary>The production serving authority over a synthetic, already verified resident build.</summary>
    private static ChdDeliveryAdmission Admission(ChdNativeIdentity resident)
    {
        var candidate = new ChdNativeCandidate(
            Path.Combine(Path.GetTempPath(), "romd-chd-decoder-upgrade-" + Guid.NewGuid().ToString("N")), resident);
        return new(() => true, () => candidate,
            () => throw new InvalidOperationException("Evidence of another tuple must not invalidate readiness."),
            native => native.ExpectedIdentity);
    }

    private static ChdNativeIdentity Change(ChdNativeIdentity native, string changed) => changed switch
    {
        "rid" => native with { Rid = native.Rid == "linux-arm64" ? "linux-x64" : "linux-arm64" },
        "pin" => native with { UpstreamCommit = new string('a', 40) },
        _ => native with { RecipeVersion = native.RecipeVersion + 1 }
    };

    /// <summary>Stands in for libchdr: decodes the fixture and records the retained evidence the reader admitted.</summary>
    private sealed class DecodingAdapter(byte[] logical) : IRomMediaAdapter
    {
        public List<RomChdDeliveryEvidence> Admitted { get; } = [];
        public RomRepresentationEncoding Encoding => RomRepresentationEncoding.Chd;
        public bool ReusesIdleImages => false;

        public ValueTask<IRandomAccessContent> OpenAsync(RomArtifactSource source, RomMediaOpenContext context,
            MediaBlockCache cache, CancellationToken ct)
        {
            Admitted.Add(context.ChdEvidence.ShouldNotBeNull());
            return ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(logical, source.OpenSynchronousRaw()));
        }
    }

    private sealed class Upload(RomVerification proof) : ITempFile
    {
        public string Path => "unused-reupload-fixture";
        public Sha256 FileSha256 => proof.ArtifactSha256;
        public long FileSize => proof.ArtifactSize;
        public bool TryBeginChildUse() => throw new InvalidOperationException();
        public bool TryEndChildUse() => throw new InvalidOperationException();
        public bool Quarantine() => throw new InvalidOperationException();
        public Stream OpenRead() => throw new InvalidOperationException("Publication must use the staging port.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Bytes(byte[] bytes) : IRandomAccessContent
    {
        public long Length => bytes.Length;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;

        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(destination.Length, Math.Max(0, bytes.Length - offset));
            bytes.AsMemory((int)Math.Min(offset, bytes.Length), count).CopyTo(destination);
            return ValueTask.FromResult(count);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
