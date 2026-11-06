using System.Security.Cryptography;
using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RomCdProductionReaderTests
{
    [Fact]
    public async Task OpenAsync_SelectedAudioMember_UsesGlobalReadAdmissionAndNeverExposesSiblings()
    {
        var fixture = await FixtureAsync();
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdAsync(1, default).Returns(fixture.Content);
        var cas = Substitute.For<IContentAddressableStore>();
        cas.OpenSynchronousRaw(StorageKey.FromHash(fixture.Content.ArtifactSha256)).Returns(_ => new Raw(fixture.Path));
        var limits = new ReadDecoderOptions { MaxConcurrent = 1, MaxIdle = 0, CacheBytes = 1 << 20, AdmissionTimeoutMilliseconds = 0 };
        var cache = new MediaBlockCache(limits.CacheBytes);
        await using var adapter = new ChdMediaAdapter(Native, cache, limits);
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(adapter), limits, cache);
        var reader = new RomContentReader(catalog, cas, decoder, new Admission(true));
        var target = RomDeliveryPlanner.Plan(fixture.Content, "audio.bin", RomMediaPolicyComposition.CreateDeliveryPolicy(), null, new Admission(true))[0].Target.ShouldNotBeNull();
        await using var first = (await reader.OpenAsync(target)).ShouldNotBeNull();
        using var actual = new MemoryStream();
        await first.CopyToAsync(actual);
        actual.ToArray().ShouldBe(fixture.Audio);
        first.ReadByte().ShouldBe(-1);
        await Should.ThrowAsync<ResourceCapacityException>(() => reader.OpenAsync(target));
        await first.DisposeAsync();
        await using var next = (await reader.OpenSeekableAsync(target)).ShouldNotBeNull();
        next.Seek(next.Length - 3, SeekOrigin.Begin);
        byte[] tail = new byte[16];
        (await next.ReadAsync(tail)).ShouldBe(3);
        tail[..3].ShouldBe(fixture.Audio[^3..]);
        (await next.ReadAsync(tail)).ShouldBe(0);
        cas.ReceivedCalls().All(call => call.GetMethodInfo().Name == nameof(IContentAddressableStore.OpenSynchronousRaw)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("wrong-member")]
    [InlineData("wrong-representation")]
    [InlineData("wrong-output")]
    [InlineData("wrong-proof")]
    [InlineData("whole-artifact")]
    [InlineData("logical-mismatch")]
    public async Task OpenAsync_InvalidMemberOrCapability_RejectsBeforeStorage(string mutation)
    {
        var fixture = await FixtureAsync();
        var admission = new Admission(mutation != "revoked");
        var target = RomDeliveryPlanner.Plan(fixture.Content, "audio.bin", RomMediaPolicyComposition.CreateDeliveryPolicy(), null, new Admission(true))[0].Target!;
        var binding = (RomCdMemberBinding)target.ReadBinding!;
        target = mutation switch
        {
            "wrong-member" => target with { ReadBinding = binding with { Ordinal = 0 } },
            "wrong-representation" => target with { ReadBinding = binding with { RepresentationId = 99 } },
            "wrong-output" => target with { ReadBinding = binding with { OutputId = 103 } },
            "wrong-proof" => target with { ReadBinding = binding with { MeasurementId = Guid.NewGuid() } },
            "whole-artifact" => target with { Format = RomDeliveryFormat.Chd, Sha256 = target.ArtifactSha256, SizeBytes = target.ArtifactSizeBytes },
            _ => target
        };
        if (mutation == "logical-mismatch")
            target = target with { ContentSha256 = Sha256.Parse(new string('f', 64)), Sha256 = Sha256.Parse(new string('f', 64)) };
        var content = fixture.Content;
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdAsync(1, default).Returns(content);
        var cas = Substitute.For<IContentAddressableStore>();
        var decoder = Substitute.For<IRomRepresentationDecoder>();
        decoder.CanDecode(RomRepresentationEncoding.Chd).Returns(true);
        (await new RomContentReader(catalog, cas, decoder, admission).OpenAsync(target)).ShouldBeNull();
        cas.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Plan_SingleMember_NeverOffersSharedChdEvenWhenAnotherMemberIsAuthorized()
    {
        var fixture = await FixtureAsync();
        var options = RomDeliveryPlanner.Plan(fixture.Content, "audio.bin", RomMediaPolicyComposition.CreateDeliveryPolicy(), null, new Admission(true));
        options.Single(option => option.Format == RomDeliveryFormat.Original).State.ShouldBe(RomDeliveryState.Available);
        options.ShouldNotContain(option => option.Format == RomDeliveryFormat.Chd);
        options.Where(option => option.Target is not null).All(option => option.Target!.Sha256 == fixture.Content.ContentSha256).ShouldBeTrue();
    }

    [Fact]
    public async Task OpenAsync_NativeTupleChanges_RejectsTheRetainedMemberAndDisposesTheRawSnapshot()
    {
        var fixture = await FixtureAsync();
        var member = (RomReconstruction.CdMember)fixture.Content.Reconstruction;
        var prior = member.Facts;
        var measured = prior.Verifications.Single();
        var native = ((RomDecoderIdentity.Native)measured.Decoder).Build;
        var changed = new RomRepresentationVerification(Guid.NewGuid(), measured.ArtifactSha256, measured.ArtifactSize,
            new RomDecoderIdentity.Native(new(native.Rid, native.UpstreamCommit, native.RecipeVersion + 1)), measured.CheckedProfile,
            measured.VerifierBuild, measured.ObservedAt, measured.Outputs, prior.Container, measured.ComputedRawSha1, measured.ComputedOverallSha1);
        var facts = new RomRepresentationFacts(prior.Id, prior.FileId, prior.ArtifactSha256, prior.ArtifactSize, prior.Container, prior.Manifest,
            prior.Outputs, [changed], prior.Descriptors);
        var content = new RomContentDescriptor(1, fixture.Content.OutputId, fixture.Content.Identity, "audio.bin", [new(facts, fixture.Content.OutputId, true)], [], []);
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdAsync(1, default).Returns(content);
        var cas = Substitute.For<IContentAddressableStore>();
        var raw = new Raw(fixture.Path);
        cas.OpenSynchronousRaw(StorageKey.FromHash(content.ArtifactSha256)).Returns(raw);
        var limits = new ReadDecoderOptions { MaxConcurrent = 1, MaxIdle = 0, CacheBytes = 1 << 20, AdmissionTimeoutMilliseconds = 0 };
        var cache = new MediaBlockCache(limits.CacheBytes);
        await using var adapter = new ChdMediaAdapter(Native, cache, limits);
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(adapter), limits, cache);
        var target = RomDeliveryPlanner.Plan(content, "audio.bin", RomMediaPolicyComposition.CreateDeliveryPolicy(), null, new Admission(true))[0].Target!;
        await Should.ThrowAsync<InvalidDataException>(() => new RomContentReader(catalog, cas, decoder, new Admission(true)).OpenAsync(target));
        raw.Disposed.ShouldBeTrue();
    }

    private static ChdNativeCandidate Native => ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory);
    private static async Task<(string Path, RomContentDescriptor Content, byte[] Audio)> FixtureAsync()
    {
        string path = NativeTestBuilds.LibchdrFixture("cd-edge-multi.chd");
        var inspected = await ChdCdInspectionService.InspectAsync(path, Native, 2L << 30, 2L << 30);
        var inspection = inspected.Inspection.ShouldNotBeNull();
        var members = new List<ChdCdMemberVerification>();
        byte[] audio = [];
        await using var pool = new ChdReadPool(new MediaBlockCache(1 << 20), 1, 1);
        for (int number = 1; number <= 3; number++)
        {
            byte[] original = File.ReadAllBytes(NativeTestBuilds.LibchdrFixture($"cd-edge-track{number}.bin"));
            if (number == 2)
                audio = original;
            var track = inspection.Tracks[number - 1];
            var device = await ChdBlockDevice.OpenAsync(new ChdCdRawFile(path), pool, ChdCdLimits.Preflight, inspection.ArtifactSha256);
            await using var projection = await ChdCdBinContent.CreateAsync(device,
                [new((long)track.FirstFrame, (int)track.Frames, track.Type == Moonlark.Libchdr.ChdCdTrackType.Audio)]);
            byte[] reconstructed = new byte[original.Length];
            int offset = 0;
            while (offset < reconstructed.Length)
            {
                int read = await projection.ReadAtAsync(offset, reconstructed.AsMemory(offset));
                read.ShouldBeGreaterThan(0);
                offset += read;
            }
            (await projection.ReadAtAsync(offset, new byte[1])).ShouldBe(0);
            reconstructed.ShouldBe(original);
            members.Add(new($"track{number}.bin", "bin", [number], Hashes(Identity(original)), Hashes(Identity(reconstructed))));
        }
        var cue = Identity("REM retained original\r\n"u8.ToArray());
        members.Add(new("disc.cue", "cue", [], Hashes(cue), null));
        var comparison = new ChdCdVerification(ChdCdVerification.CurrentProfile, cue.Sha256.ToString(), members, null);
        var measured = RomCdMeasurementMapper.Map(inspection, comparison, inspected.NativeIdentity!, "test-independent-bin-comparison", DateTimeOffset.UtcNow);
        var outputs = measured.Measurement.Outputs.Select((output, index) => new RomRepresentationOutput(index + 101, index == 1 ? 1 : index == 0 ? 2 : 3,
            output.Selector, output.Identity)).ToArray();
        var facts = new RomRepresentationFacts(42, 2, measured.Measurement.ArtifactSha256, measured.Measurement.ArtifactSize, measured.Container, measured.Manifest,
            outputs, [measured.Measurement], [new(3, 11, cue.Sha256, cue.Size)]);
        var selected = outputs.Single(output => output.Selector == new RomOutputSelector.Member(1));
        return (path, new RomContentDescriptor(1, selected.Id, selected.Identity, "audio.bin", [new(facts, selected.Id, true)], [], []), audio);
    }
    private static ChdCdFileHashes Hashes(RomCdFileIdentity identity) => new(identity.Size, identity.Sha256.ToString(), identity.Sha1.ToString(), identity.Md5.ToString(), identity.Crc32.ToString());
    private static RomCdFileIdentity Identity(byte[] bytes) => new(bytes.Length, Sha256.FromSpan(SHA256.HashData(bytes)), Sha1.FromSpan(SHA1.HashData(bytes)),
        Md5.FromSpan(MD5.HashData(bytes)), Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(bytes)));
    private sealed class Admission(bool admitted) : IRomChdDeliveryAdmission
    {
        public RomChdDeliveryAssessment? Assess(RomChdDeliveryEvidence evidence) => null;
        public RomManagedReadBinding? AssessManaged(RomContentArtifact artifact) => null;
        public bool AdmitsCd(RomReconstruction.CdMember member) => AssessCd(member) is not null;
        public RomChdDecoderBuild? AssessCd(RomReconstruction.CdMember member) => admitted
            ? ((RomDecoderIdentity.Native)member.Facts.Verifications.Single().Decoder).Build : null;
    }
    private sealed class Raw(string path) : ISynchronousRawContent
    {
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _handle = File.OpenHandle(path);
        public long Length { get; } = new FileInfo(path).Length;
        public bool Disposed { get; private set; }
        public int ReadAt(long offset, Span<byte> destination) => RandomAccess.Read(_handle, destination, offset);
        public void Dispose() { Disposed = true; _handle.Dispose(); }
    }
}
