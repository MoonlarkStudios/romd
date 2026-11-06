using System.Security.Cryptography;
using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed partial class RomCdArchiveTests
{
    [Fact]
    public async Task Reader_ActiveMember_HoldsAdmissionUntilIdempotentDisposal()
    {
        var fixture = await FixtureAsync(false);
        var content = Content(Facts(fixture.Measured), 1);
        var cas = Substitute.For<IContentAddressableStore>();
        cas.OpenSynchronousRaw(StorageKey.FromHash(content.ArtifactSha256)).Returns(_ => new RawFile(fixture.Chd.Path));
        await using var stack = new ReaderStack(content, cas);
        var target = Target(content);
        var first = (await stack.Reader.OpenSeekableAsync(target)).ShouldNotBeNull();
        await Should.ThrowAsync<ResourceCapacityException>(() => stack.Reader.OpenSeekableAsync(target));
        byte[] odd = new byte[31];
        first.Seek(1, SeekOrigin.Begin);
        (await first.ReadAsync(odd)).ShouldBe(31);
        odd.ShouldBe(fixture.Originals[fixture.Manifest.Members[1].Path].AsSpan(1, 31).ToArray());
        await first.DisposeAsync();
        await first.DisposeAsync();
        await using var next = (await stack.Reader.OpenSeekableAsync(target)).ShouldNotBeNull();
        await Should.ThrowAsync<ObjectDisposedException>(() => first.ReadAsync(odd).AsTask());
    }

    [Fact]
    public async Task Reader_HeaderMismatch_DisposesSnapshotAndReturnsAdmission()
    {
        var fixture = await FixtureAsync(true);
        var content = Content(Facts(fixture.Measured), 0);
        var cas = Substitute.For<IContentAddressableStore>();
        byte[] bytes = File.ReadAllBytes(fixture.Chd.Path);
        bytes[60] ^= 1;
        List<ChdObservedRaw> snapshots = [];
        cas.OpenSynchronousRaw(StorageKey.FromHash(content.ArtifactSha256)).Returns(_ => { var source = new ChdObservedRaw(bytes); snapshots.Add(source); return source; });
        await using var stack = new ReaderStack(content, cas);
        for (int attempt = 0; attempt < 2; attempt++)
            await Should.ThrowAsync<InvalidDataException>(() => stack.Reader.OpenSeekableAsync(Target(content)));
        snapshots.Count.ShouldBe(2);
        snapshots.ShouldAllBe(source => source.DisposeCalls == 1);
    }

    [Fact]
    public async Task Reader_CancelledNativeRead_KeepsAdmissionAndSourceUntilDisposalDrains()
    {
        var fixture = await FixtureAsync(true);
        var content = Content(Facts(fixture.Measured), 0);
        var cas = Substitute.For<IContentAddressableStore>();
        using var source = new GatedRaw(File.ReadAllBytes(fixture.Chd.Path));
        cas.OpenSynchronousRaw(StorageKey.FromHash(content.ArtifactSha256)).Returns(source);
        await using var stack = new ReaderStack(content, cas);
        var target = Target(content);
        var stream = (await stack.Reader.OpenSeekableAsync(target)).ShouldNotBeNull();
        using var cancellation = new CancellationTokenSource();
        source.Armed = true;
        var read = stream.ReadAsync(new byte[31], cancellation.Token).AsTask();
        Task? disposal = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            var error = await Should.ThrowAsync<OperationCanceledException>(() => read);
            error.CancellationToken.ShouldBe(cancellation.Token);
            disposal = stream.DisposeAsync().AsTask();
            disposal.IsCompleted.ShouldBeFalse();
            source.Disposed.ShouldBeFalse();
            await Should.ThrowAsync<ResourceCapacityException>(() => stack.Reader.OpenSeekableAsync(target));
        }
        finally
        {
            source.Release.Set();
            await (disposal ?? stream.DisposeAsync().AsTask());
            try
            { await read; }
            catch (OperationCanceledException) { }
        }
        source.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Reader_MismatchedWholeMemberDigest_RefusesSuccessfulEof()
    {
        var fixture = await FixtureAsync(true);
        var manifest = fixture.Manifest;
        var original = (RomCdBinMember)manifest.Members[0];
        var wrong = new RomCdFileIdentity(original.Identity.Size, Sha256.Parse(new string('f', 64)), original.Identity.Sha1, original.Identity.Md5, original.Identity.Crc32);
        var alteredManifest = new RomReconstructionManifest.CdTracks(manifest.LogicalFrames, manifest.Tracks,
            [new RomCdBinMember(original.Path, wrong, wrong, original.Tracks), .. manifest.Members.Skip(1)]);
        var prior = fixture.Measured.Measurement;
        var alteredProof = new RomRepresentationVerification(Guid.NewGuid(), prior.ArtifactSha256, prior.ArtifactSize, prior.Decoder, prior.CheckedProfile,
            prior.VerifierBuild, prior.ObservedAt, [new(new RomOutputSelector.Member(0), Logical(wrong))], fixture.Measured.Container, prior.ComputedRawSha1, prior.ComputedOverallSha1);
        var content = Content(Facts(new(fixture.Measured.Container, alteredManifest, alteredProof)), 0);
        var cas = Substitute.For<IContentAddressableStore>();
        cas.OpenSynchronousRaw(StorageKey.FromHash(content.ArtifactSha256)).Returns(_ => new RawFile(fixture.Chd.Path));
        await using var stack = new ReaderStack(content, cas);
        await using var stream = (await stack.Reader.OpenAsync(Target(content))).ShouldNotBeNull();
        await Should.ThrowAsync<Romd.Storage.Exceptions.ContentCorruptedException>(() => stream.CopyToAsync(Stream.Null));
    }

    private static RomRepresentationFacts Facts(RomMeasuredRepresentation measured)
    {
        var manifest = (RomReconstructionManifest.CdTracks)measured.Manifest;
        return new(42, 9, measured.Measurement.ArtifactSha256, measured.Measurement.ArtifactSize, measured.Container, manifest,
            measured.Measurement.Outputs.Select((output, index) => new RomRepresentationOutput(101 + index, index + 1, output.Selector, output.Identity)).ToArray(),
            [measured.Measurement], manifest.Members.Select((member, ordinal) => (member, ordinal)).Where(pair => pair.member is RomCdStoredMember)
                .Select(pair => new RomRetainedDescriptor(pair.ordinal, 20 + pair.ordinal, pair.member.Identity.Sha256, pair.member.Identity.Size)).ToArray());
    }
    private static RomContentDescriptor Content(RomRepresentationFacts facts, int ordinal) => Content(facts,
        facts.Outputs.Single(output => output.Selector == new RomOutputSelector.Member(ordinal)));
    private static RomContentDescriptor Content(RomRepresentationFacts facts, RomRepresentationOutput output) =>
        new(output.RomId!.Value, output.Id, output.Identity, ((RomReconstructionManifest.CdTracks)facts.Manifest).Members[((RomOutputSelector.Member)output.Selector).Ordinal].Path,
            [new(facts, output.Id, true)], [], []);
    private static RomDeliveryTarget Target(RomContentDescriptor content) => RomDeliveryPlanner.Plan(content, content.OriginalFilename,
        RomMediaPolicyComposition.CreateDeliveryPolicy(), null, new Admission())[0].Target.ShouldNotBeNull();
    private sealed class Admission : IRomChdDeliveryAdmission
    {
        public RomChdDeliveryAssessment? Assess(RomChdDeliveryEvidence evidence) => null;
        public RomManagedReadBinding? AssessManaged(RomContentArtifact artifact) => null;
        public bool AdmitsCd(RomReconstruction.CdMember member) => AssessCd(member) is not null;
        public RomChdDecoderBuild? AssessCd(RomReconstruction.CdMember member) =>
            member.Facts.Verifications.Select(proof => proof.Decoder).OfType<RomDecoderIdentity.Native>().Single().Build;
    }
    private sealed class ReaderStack : IAsyncDisposable
    {
        private readonly ChdMediaAdapter adapter; private readonly RomRepresentationDecoder decoder;
        public RomContentReader Reader { get; }
        public ReaderStack(RomContentDescriptor content, IContentAddressableStore cas)
        {
            var catalog = Substitute.For<IRomContentCatalog>();
            catalog.FindByRomIdAsync(content.RomId, Arg.Any<CancellationToken>()).Returns(content);
            var limits = new ReadDecoderOptions { MaxConcurrent = 1, MaxIdle = 0, CacheBytes = 0, AdmissionTimeoutMilliseconds = 0 };
            var cache = new MediaBlockCache(0);
            adapter = new(Native, cache, limits);
            decoder = new(new RomMediaAdapterRegistry(adapter), limits, cache);
            Reader = new(catalog, cas, decoder, new Admission());
        }
        public async ValueTask DisposeAsync() { await decoder.DisposeAsync(); await adapter.DisposeAsync(); }
    }
}
