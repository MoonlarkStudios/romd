using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Storage;
using Romd.Storage.Exceptions;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Synthetic complete facts isolate scope, exact byte delivery and cleanup from native qualification.</summary>
public sealed class RomCdArchiveDownloadReaderTests
{
    private static readonly byte[] Cue = Encoding.ASCII.GetBytes("REM exact original\r\nFILE \"data.bin\" BINARY\r\n TRACK 01 MODE2/2352\r\n INDEX 01 00:00:00\r\nFILE \"audio.bin\" BINARY\r\n TRACK 02 AUDIO\r\n INDEX 01 00:00:00\r\n");
    private static readonly byte[] Artifact = Enumerable.Range(0, 1024).Select(index => (byte)index).ToArray();

    [Fact]
    public async Task OriginalCue_ReadsExactRetainedBytesWithoutInvokingADecoder()
    {
        var target = Target();
        var catalog = Substitute.For<IRomCdArchiveDownloadCatalog>();
        catalog.FindForLayoutAsync(1, Arg.Any<CancellationToken>()).Returns(target);
        var cas = Substitute.For<IContentAddressableStore>();
        var raw = new MemoryContent(Cue);
        cas.OpenRandomAccessAsync(StorageKey.FromHash(Identity(Cue).Sha256), Arg.Any<CancellationToken>()).Returns(raw);
        var admission = Substitute.For<IRomChdDeliveryAdmission>();
        var reader = new RomCdArchiveDownloadReader(catalog, cas, admission);
        await using (var stream = (await reader.OpenCueAsync(target)).ShouldNotBeNull())
        {
            using var copied = new MemoryStream();
            await stream.CopyToAsync(copied);
            copied.ToArray().ShouldBe(Cue);
        }
        raw.Disposed.ShouldBeTrue();
        admission.ReceivedCalls().ShouldBeEmpty();
        await cas.Received(1).OpenRandomAccessAsync(StorageKey.FromHash(Identity(Cue).Sha256), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("publication")]
    [InlineData("representation")]
    [InlineData("selected-owner")]
    [InlineData("sibling-owner")]
    public async Task CurrentScope_ChangedOrMissingPublication_RefusesCueAndContainerBeforeStorage(string change)
    {
        var target = Target();
        var facts = target.Representation;
        var catalog = Substitute.For<IRomCdArchiveDownloadCatalog>();
        if (change != "missing")
        {
            var outputs = facts.Outputs.Select((output, index) => (change == "selected-owner" && index == 0 || change == "sibling-owner" && index == 1)
                ? new RomRepresentationOutput(output.Id, null, output.Selector, output.Identity) : output).ToArray();
            var current = target with
            {
                PublicationId = change == "publication" ? Guid.NewGuid() : target.PublicationId,
                Representation = new(change == "representation" ? 43 : facts.Id, facts.FileId, facts.ArtifactSha256, facts.ArtifactSize, facts.Container, facts.Manifest,
                    outputs, facts.Verifications, facts.Descriptors)
            };
            catalog.FindForLayoutAsync(1, Arg.Any<CancellationToken>()).Returns(current);
        }
        var cas = Substitute.For<IContentAddressableStore>();
        var admission = Substitute.For<IRomChdDeliveryAdmission>();
        var reader = new RomCdArchiveDownloadReader(catalog, cas, admission);
        (await reader.OpenCueAsync(target)).ShouldBeNull();
        (await reader.OpenAsync(target)).ShouldBeNull();
        cas.ReceivedCalls().ShouldBeEmpty();
        admission.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Container_CurrentScope_RequiresNativeAdmissionBeforeReadingExactArtifact(bool admitted)
    {
        var target = Target();
        var catalog = Substitute.For<IRomCdArchiveDownloadCatalog>();
        catalog.FindForLayoutAsync(1, default).Returns(target);
        var cas = Substitute.For<IContentAddressableStore>();
        var raw = new MemoryContent(Artifact);
        cas.OpenRandomAccessAsync(StorageKey.FromHash(target.Representation.ArtifactSha256), Arg.Any<CancellationToken>()).Returns(raw);
        var admission = Substitute.For<IRomChdDeliveryAdmission>();
        admission.AdmitsCd(Arg.Any<RomReconstruction.CdMember>()).Returns(admitted);
        var stream = await new RomCdArchiveDownloadReader(catalog, cas, admission).OpenAsync(target);
        if (admitted)
        {
            await using (stream.ShouldNotBeNull())
            {
                using var copied = new MemoryStream();
                await stream.CopyToAsync(copied);
                copied.ToArray().ShouldBe(Artifact);
            }
            raw.Disposed.ShouldBeTrue();
        }
        else
        { stream.ShouldBeNull(); cas.ReceivedCalls().ShouldBeEmpty(); raw.Disposed.ShouldBeFalse(); }
        admission.Received(1).AdmitsCd(Arg.Is<RomReconstruction.CdMember>(member => member.RepresentationId == target.Representation.Id && member.Facts.Outputs.Count == 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cue_DamagedBacking_RefusesLengthOrFailsHashVerificationAndDisposes(bool sameLength)
    {
        var target = Target();
        var catalog = Substitute.For<IRomCdArchiveDownloadCatalog>();
        catalog.FindForLayoutAsync(1, default).Returns(target);
        byte[] bytes = sameLength ? [.. Cue] : Cue[..^1];
        bytes[0] ^= 1;
        var raw = new MemoryContent(bytes);
        var cas = Substitute.For<IContentAddressableStore>();
        cas.OpenRandomAccessAsync(StorageKey.FromHash(Identity(Cue).Sha256), Arg.Any<CancellationToken>()).Returns(raw);
        var stream = await new RomCdArchiveDownloadReader(catalog, cas, Substitute.For<IRomChdDeliveryAdmission>()).OpenCueAsync(target);
        if (sameLength)
        {
            await using (stream.ShouldNotBeNull())
                await Should.ThrowAsync<ContentCorruptedException>(() => stream.CopyToAsync(Stream.Null));
        }
        else
            stream.ShouldBeNull();
        raw.Disposed.ShouldBeTrue();
    }

    private static RomCdArchiveDownload Target()
    {
        var first = Identity(new byte[32 * 2352]);
        var secondBytes = new byte[32 * 2352];
        secondBytes[0] = 1;
        var second = Identity(secondBytes);
        var cue = Identity(Cue);
        var artifact = Identity(Artifact);
        var rawHash = Sha1.Parse(new string('b', 40));
        var container = new RomContainerFacts.Chd(5, 64 * 2448, 39168, 2448, 4, [0x63647a73, 0x6364666c, 0, 0], false, rawHash, rawHash,
            [new(0x43485432,1,"TRACK:1 TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:32 PREGAP:0 PGTYPE:MODE2_RAW PGSUB:NONE POSTGAP:0"u8),
             new(0x43485432,1,"TRACK:2 TYPE:AUDIO SUBTYPE:NONE FRAMES:32 PREGAP:0 PGTYPE:AUDIO PGSUB:NONE POSTGAP:0"u8)]);
        var manifest = new RomReconstructionManifest.CdTracks(64, [new(1, 0, 32, RomCdTrackKind.Mode2Raw), new(2, 32, 32, RomCdTrackKind.Audio)],
            [new RomCdBinMember("data.bin", first, first, [1]), new RomCdBinMember("audio.bin", second, second, [2]), new RomCdStoredMember("disc.cue", cue, RomCdStoredRole.Cue)]);
        RomLogicalIdentity Logical(RomCdFileIdentity member) => new(member.Size, member.Sha256, member.Sha1, member.Md5, member.Crc32);
        RomMeasuredOutput[] measured = [new(new RomOutputSelector.Member(0), Logical(first)), new(new RomOutputSelector.Member(1), Logical(second))];
        var proof = new RomRepresentationVerification(Guid.NewGuid(), artifact.Sha256, artifact.Size,
            new RomDecoderIdentity.Native(new("linux-x64", Sha1.Parse(new string('a', 40)), 2)), "test-complete-cd/v1", "test-verifier", DateTimeOffset.UnixEpoch,
            measured, container, rawHash, rawHash);
        var facts = new RomRepresentationFacts(42, 9, artifact.Sha256, artifact.Size, container, manifest,
            [new(101, 1, measured[0].Selector, measured[0].Identity), new(102, 2, measured[1].Selector, measured[1].Identity)],
            [proof], [new(2, 10, cue.Sha256, cue.Size)]);
        return new(1, Guid.NewGuid(), facts, "disc.chd");
    }
    private static RomCdFileIdentity Identity(byte[] bytes) => new(bytes.Length, Sha256.FromSpan(SHA256.HashData(bytes)), Sha1.FromSpan(SHA1.HashData(bytes)),
        Md5.FromSpan(MD5.HashData(bytes)), Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(bytes)));
    private sealed class MemoryContent(byte[] bytes) : IRandomAccessContent
    {
        public bool Disposed { get; private set; }
        public long Length => bytes.Length;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ObjectDisposedException.ThrowIf(Disposed, this);
            int count = (int)Math.Min(destination.Length, Math.Max(0, bytes.Length - offset));
            bytes.AsMemory((int)Math.Min(offset, bytes.Length), count).CopyTo(destination);
            return ValueTask.FromResult(count);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
