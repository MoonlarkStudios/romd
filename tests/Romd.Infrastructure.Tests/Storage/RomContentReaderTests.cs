using System.Security.Cryptography;
using NSubstitute;
using NSubstitute.Extensions;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Media.TestSupport;
using Romd.Media.Inspection;
using Romd.Media.Rvz;
using Romd.Storage;
using Romd.Storage.Exceptions;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RomContentReaderTests : IAsyncLifetime
{
    private static readonly Sha256 Logical = Sha256.Parse(new string('a', 64));
    private static readonly Sha256 Artifact = Sha256.Parse(new string('b', 64));
    private readonly IRomContentCatalog _catalog = Substitute.For<IRomContentCatalog>();
    private readonly IContentAddressableStore _cas = Substitute.For<IContentAddressableStore>();
    private readonly IRomRepresentationDecoder _decoder = Substitute.For<IRomRepresentationDecoder>();

    private readonly List<IAsyncDisposable> _ownedDecoders = [];
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    { foreach (var decoder in _ownedDecoders) await decoder.DisposeAsync(); }

    // The substitute stands in for a decoder that reads every encoding; tests without a reader use a real decoder.
    public RomContentReaderTests() => _decoder.CanDecode(Arg.Any<RomRepresentationEncoding>()).Returns(true);

    [Fact]
    public async Task OpenSeekableAsync_RvzTarget_UsesExactArtifactAndSupportsRangesWithoutDecoderCapacity()
    {
        var source = new Bytes([7, 8, 9, 10]);
        _decoder.DecodeAsync(Arg.Any<RomRepresentationEncoding>(), Arg.Any<IRandomAccessContent>(),
            Arg.Any<CancellationToken>(), Arg.Any<Sha256?>(), Arg.Any<TimeSpan?>())
            .Returns<Task<IRandomAccessContent>>(_ => throw new ResourceCapacityException("decoder full"));
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Artifact), Arg.Any<CancellationToken>()).Returns(source);
        var target = RvzTarget(Artifact, 4, logicalSize: 64);
        await using (var stream = (await Reader().OpenSeekableAsync(target)).ShouldNotBeNull())
        {
            stream.CanSeek.ShouldBeTrue();
            stream.Length.ShouldBe(4);
            stream.Seek(2, SeekOrigin.Begin);
            stream.ReadByte().ShouldBe(9);
            stream.Seek(-1, SeekOrigin.End);
            stream.ReadByte().ShouldBe(10);
            stream.ReadByte().ShouldBe(-1);
        }
        source.Disposed.ShouldBeTrue();
        _decoder.ReceivedCalls().ShouldBeEmpty();
        await _catalog.Received(1).FindByRomIdAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenSeekableAsync_OriginalTarget_FreshManagedBindingReturnsSeekableDecodedBytes()
    {
        var current = RvzContent(Logical, 4, Artifact, 2, fileId: 3);
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(current);
        var source = new Bytes([7, 8]);
        var decoded = new Bytes([1, 2, 3, 4]);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Artifact), Arg.Any<CancellationToken>()).Returns(source);
        _decoder.DecodeAsync(RomRepresentationEncoding.Rvz, source, default, Artifact).Returns(decoded);
        await using var stream = (await Reader().OpenSeekableAsync(Target(current, RomDeliveryFormat.Original))).ShouldNotBeNull();
        stream.CanSeek.ShouldBeTrue();
        stream.Seek(2, SeekOrigin.Begin);
        stream.ReadByte().ShouldBe(3);
        await _cas.DidNotReceive().OpenRandomAccessAsync(StorageKey.FromHash(Logical), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenSeekableAsync_RvzTargetDisappears_DoesNotSubstituteOriginalOrAnotherRepresentation()
    {
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Artifact), Arg.Any<CancellationToken>()).Returns((IRandomAccessContent?)null);
        var target = RvzTarget(Artifact, 2, logicalSize: 64);
        (await Reader().OpenSeekableAsync(target)).ShouldBeNull();
        await _cas.Received(1).OpenRandomAccessAsync(StorageKey.FromHash(Artifact), Arg.Any<CancellationToken>());
        await _catalog.Received(1).FindByRomIdAsync(1, Arg.Any<CancellationToken>());
        _decoder.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenSeekableAsync_RvzTargetWrongLength_FailsAndDisposesSource()
    {
        var source = new Bytes([7, 8, 9]);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Artifact), Arg.Any<CancellationToken>()).Returns(source);
        var target = RvzTarget(Artifact, 2, logicalSize: 64);
        await Should.ThrowAsync<InvalidDataException>(() => Reader().OpenSeekableAsync(target));
        source.Disposed.ShouldBeTrue();
        _decoder.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_Rvz_UsesArtifactKeyAndReturnsLogicalBytes()
    {
        var source = new Bytes([7, 8]);
        var decoded = new Bytes([1, 2, 3, 4]);
        _catalog.FindAsync(Logical, Arg.Any<CancellationToken>()).Returns(RvzContent(Logical, 4, Artifact, 2));
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Artifact), Arg.Any<CancellationToken>()).Returns(source);
        _decoder.DecodeAsync(RomRepresentationEncoding.Rvz, source, default, Artifact).Returns(decoded);

        await using (var stream = await Reader().OpenAsync(Logical))
        {
            stream.ShouldNotBeNull().Length.ShouldBe(4);
            stream.Seek(2, SeekOrigin.Begin);
            stream.ReadByte().ShouldBe(3);
        }
        decoded.Disposed.ShouldBeTrue();
        await _cas.DidNotReceive().OpenRandomAccessAsync(StorageKey.FromHash(Logical), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Descriptor_InconsistentIdentity_RefusesBeforeItCanReachTheReader()
    {
        Should.Throw<ArgumentException>(() => WholeContent(1, 2, Identity(Logical, 4), Artifact, 4,
            RomContainerFacts.Identity.Instance));
        _cas.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(5, 4)]
    [InlineData(2, 5)]
    public async Task OpenAsync_LengthMismatch_DisposesOwnedReader(long artifactLength, long decodedLength)
    {
        var source = new Bytes(new byte[artifactLength]);
        var decoded = new Bytes(new byte[decodedLength]);
        _catalog.FindAsync(Logical, Arg.Any<CancellationToken>()).Returns(RvzContent(Logical, 4, Artifact, 2));
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Artifact), Arg.Any<CancellationToken>()).Returns(source);
        _decoder.DecodeAsync(RomRepresentationEncoding.Rvz, source, default, Artifact).Returns(decoded);
        await Should.ThrowAsync<InvalidDataException>(() => Reader().OpenAsync(Logical));
        (artifactLength == 2 ? decoded : source).Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task OpenAsync_UnknownContent_DoesNotTreatHashAsCasGrant()
    {
        (await Reader().OpenAsync(Logical)).ShouldBeNull();
        _cas.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenVerifiedByRomIdAsync_CorruptedLogicalBytes_FailsAtEof()
    {
        var source = new Bytes([7, 8]);
        var decoded = new Bytes([1, 2, 3, 4]);
        _catalog.FindByRomIdAsync(2, Arg.Any<CancellationToken>()).Returns(RvzContent(Logical, 4, Artifact, 2));
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Artifact), Arg.Any<CancellationToken>()).Returns(source);
        _decoder.DecodeAsync(RomRepresentationEncoding.Rvz, source, default, Artifact).Returns(decoded);
        await using var stream = (await Reader().OpenVerifiedByRomIdAsync(2)).ShouldNotBeNull();
        await Should.ThrowAsync<ContentCorruptedException>(() => stream.CopyToAsync(Stream.Null));
    }

    [Fact]
    public async Task OpenAsync_OriginalTargetAfterRepresentationReplacement_RefusesStaleBindingAndReadsFreshTarget()
    {
        byte[] logicalBytes = [1, 2, 3, 4];
        var identity = Identity(logicalBytes);
        var original = WholeContent(1, 2, identity, identity.Sha256, 4, RomContainerFacts.Identity.Instance);
        var current = WholeContent(1, 3, identity, Artifact, 2, Rvz(4));
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(current);
        var source = new Bytes([7, 8]);
        var decoded = new Bytes(logicalBytes);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Artifact), Arg.Any<CancellationToken>()).Returns(source);
        _decoder.DecodeAsync(RomRepresentationEncoding.Rvz, source, default, Artifact).Returns(decoded);
        (await Reader().OpenAsync(Target(original, RomDeliveryFormat.Original))).ShouldBeNull();
        _cas.ReceivedCalls().ShouldBeEmpty();
        await using var stream = (await Reader().OpenAsync(Target(current, RomDeliveryFormat.Original))).ShouldNotBeNull();
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        output.ToArray().ShouldBe(logicalBytes);
        await _cas.DidNotReceive().OpenRandomAccessAsync(StorageKey.FromHash(identity.Sha256), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_OriginalIdentityChangedOrDisappeared_ReturnsMissingBeforeStorage(bool disappeared)
    {
        if (!disappeared)
            _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(
            WholeContent(1, 3, Identity(Artifact, 2), Artifact, 2, RomContainerFacts.Identity.Instance));
        var target = new RomDeliveryTarget(1, 2, RomDeliveryFormat.Original, "disc.iso", Logical, 4,
            Logical, 4, Logical, 4, RomRepresentationEncoding.Identity);
        (await Reader().OpenAsync(target)).ShouldBeNull();
        _cas.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_RvzTarget_DeliversRawArtifactAfterAdmissionWithoutDecoderCapacity()
    {
        byte[] bytes = [7, 8];
        var hash = Sha256.FromSpan(SHA256.HashData(bytes));
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(hash), Arg.Any<CancellationToken>()).Returns(new Bytes(bytes));
        _decoder.DecodeAsync(Arg.Any<RomRepresentationEncoding>(), Arg.Any<IRandomAccessContent>(),
            Arg.Any<CancellationToken>(), Arg.Any<Sha256?>(), Arg.Any<TimeSpan?>())
            .Returns<Task<IRandomAccessContent>>(_ => throw new ResourceCapacityException("decoder full"));
        var target = RvzTarget(hash, 2, logicalSize: 4);

        await using var stream = (await Reader().OpenAsync(target)).ShouldNotBeNull();
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);

        output.ToArray().ShouldBe(bytes);
        await _catalog.Received(1).FindByRomIdAsync(1, Arg.Any<CancellationToken>());
        _decoder.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("catalog-missing")]
    [InlineData("proof-withdrawn")]
    [InlineData("different-proof")]
    [InlineData("different-representation")]
    [InlineData("different-output")]
    [InlineData("different-decoder")]
    [InlineData("no-admission")]
    public async Task OpenAsync_ManagedTargetAuthorityChanged_RefusesOriginalAndStoredBytesBeforeCas(string changed)
    {
        var content = RvzContent(Logical, 4, Artifact, 2);
        var original = Target(content, RomDeliveryFormat.Original);
        var stored = Target(content, RomDeliveryFormat.Rvz);
        if (changed == "catalog-missing")
            content = null;
        else if (changed == "proof-withdrawn")
        {
            var facts = content.Preferred.Facts;
            var unmeasured = new RomRepresentationFacts(facts.Id, facts.FileId, facts.ArtifactSha256, facts.ArtifactSize,
                facts.Container, facts.Manifest, facts.Outputs, [], []);
            content = new(1, content.OutputId, content.Identity, "disc.iso", [new(unmeasured, content.OutputId, true)], [], []);
        }
        else if (changed is "different-proof" or "different-representation" or "different-output" or "different-decoder")
        {
            var bound = (RomManagedReadBinding)original.ReadBinding!;
            var foreign = changed switch
            {
                "different-proof" => bound with { MeasurementId = Guid.NewGuid() },
                "different-representation" => bound with { RepresentationId = bound.RepresentationId + 1 },
                "different-output" => bound with { OutputId = bound.OutputId + 1 },
                _ => bound with { DecoderBuild = bound.DecoderBuild + "-foreign" }
            };
            original = original with { ReadBinding = foreign };
            stored = stored with { ReadBinding = foreign };
        }
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(content);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Artifact), Arg.Any<CancellationToken>()).Returns(new Bytes([7, 8]));
        var reader = new RomContentReader(_catalog, _cas, _decoder, changed == "no-admission" ? null : ManagedAdmission());
        (await reader.OpenAsync(original)).ShouldBeNull();
        (await reader.OpenSeekableAsync(stored)).ShouldBeNull();
        _cas.ReceivedCalls().ShouldBeEmpty();
        _decoder.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_RvzTargetWrongCasLength_ThrowsAndDisposesSource()
    {
        var source = new Bytes([7, 8, 9]);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Artifact), Arg.Any<CancellationToken>()).Returns(source);
        var target = RvzTarget(Artifact, 2, logicalSize: 4);

        var error = await Should.ThrowAsync<InvalidDataException>(() => Reader().OpenAsync(target));

        error.Message.ShouldBe("Artifact length disagrees with the bound delivery target.");
        source.Disposed.ShouldBeTrue();
        _decoder.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_RvzTargetAbsent_DoesNotSubstituteNewArtifactOrOriginal()
    {
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Artifact), Arg.Any<CancellationToken>()).Returns((IRandomAccessContent?)null);
        var target = RvzTarget(Artifact, 2, logicalSize: 4);
        (await Reader().OpenAsync(target)).ShouldBeNull();
        await _cas.Received(1).OpenRandomAccessAsync(StorageKey.FromHash(Artifact), default);
        await _catalog.Received(1).FindByRomIdAsync(1, Arg.Any<CancellationToken>());
        _decoder.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_RvzCorruption_FailsFullSequentialVerification()
    {
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Artifact), Arg.Any<CancellationToken>()).Returns(new Bytes([7, 8]));
        var target = RvzTarget(Artifact, 2, logicalSize: 4);
        await using var stream = (await Reader().OpenAsync(target)).ShouldNotBeNull();
        await Should.ThrowAsync<ContentCorruptedException>(() => stream.CopyToAsync(Stream.Null));
    }

    [Fact]
    public async Task OpenAsync_RvzTargetCancelledAfterCasOpen_DisposesArtifactAndPropagatesToken()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new Bytes([7, 8]);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Artifact), cancellation.Token).Returns(_ =>
        {
            cancellation.Cancel();
            return source;
        });
        var target = RvzTarget(Artifact, 2, logicalSize: 4);

        var error = await Should.ThrowAsync<OperationCanceledException>(() => Reader().OpenAsync(target, cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        source.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task OpenAsync_InvalidTarget_RejectsBeforeStorage()
    {
        var target = new RomDeliveryTarget(1, 2, (RomDeliveryFormat)99, "disc.rvz", Artifact, 2,
            Logical, 4, Artifact, 2, RomRepresentationEncoding.Rvz);
        (await Reader().OpenAsync(target)).ShouldBeNull();
        _catalog.ReceivedCalls().ShouldBeEmpty();
        _cas.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Decode_InvalidRvzHeader_DisposesSource()
    {
        var source = new Bytes([1, 2, 3, 4]);
        await Should.ThrowAsync<InvalidDataException>(() => new RomRepresentationDecoder().DecodeAsync(RomRepresentationEncoding.Rvz, source));
        source.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_CancelledAfterCasOpen_DisposesArtifactWithoutDecoding(bool encoded)
    {
        using var cancelled = new CancellationTokenSource();
        var source = new Bytes("RVZ\u0001"u8.ToArray());
        var content = encoded ? RvzContent(Logical, 4, Logical, 4) :
            WholeContent(1, 2, Identity(Logical, 4), Logical, 4, RomContainerFacts.Identity.Instance);
        _catalog.FindAsync(Logical, cancelled.Token).Returns(content);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Logical), cancelled.Token).Returns(_ =>
        { cancelled.Cancel(); return source; });
        await using var decoder = new RomRepresentationDecoder();
        var reader = new RomContentReader(_catalog, _cas, decoder, ManagedAdmission());
        var error = await Should.ThrowAsync<OperationCanceledException>(() => reader.OpenAsync(Logical, cancelled.Token));
        error.CancellationToken.ShouldBe(cancelled.Token);
        source.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Decode_CancelledDuringHeaderRead_PropagatesTokenAndDisposesSource()
    {
        using var cancelled = new CancellationTokenSource();
        var source = new CancellingHeader(cancelled);

        var error = await Should.ThrowAsync<OperationCanceledException>(() =>
            new RomRepresentationDecoder().DecodeAsync(RomRepresentationEncoding.Rvz, source, cancelled.Token));

        error.CancellationToken.ShouldBe(cancelled.Token);
        source.ObservedToken.ShouldBe(cancelled.Token);
        source.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task OpenAsync_ActiveCancellationToken_ReachesDecoder()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new Bytes([7, 8]);
        var decoded = new Bytes([1, 2, 3, 4]);
        _catalog.FindAsync(Logical, cancellation.Token).Returns(RvzContent(Logical, 4, Artifact, 2));
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Artifact), cancellation.Token).Returns(source);
        _decoder.DecodeAsync(RomRepresentationEncoding.Rvz, source, cancellation.Token, Artifact).Returns(decoded);

        await using var stream = await Reader().OpenAsync(Logical, cancellation.Token);

        stream.ShouldNotBeNull().Length.ShouldBe(4);
        await _decoder.Received(1).DecodeAsync(RomRepresentationEncoding.Rvz, source, cancellation.Token, Artifact);
    }

    [Fact]
    public async Task OpenAsync_RvzArtifact_ReconstructsLogicalBytesThroughTheManagedDecoder()
    {
        var disc = new SyntheticRvzBuilder(0x300000, 0x20000).Padding(0x18000, 0x8000, new byte[68]).Build();
        var artifact = Sha256.FromSpan(SHA256.HashData(disc.File));
        var logical = Sha256.FromSpan(SHA256.HashData(disc.ExpectedDisc));
        var descriptor = await MeasuredRvzAsync(disc.File, disc.ExpectedDisc);
        _catalog.FindAsync(logical, Arg.Any<CancellationToken>()).Returns(descriptor);
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(descriptor);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(artifact), Arg.Any<CancellationToken>()).Returns(_ => new MemoryContent(disc.File));
        await using var pool = new RomRepresentationDecoder();
        var reader = new RomContentReader(_catalog, _cas, pool, ManagedAdmission());

        await using (var ranged = (await reader.OpenAsync(logical)).ShouldNotBeNull())
        {
            ranged.Seek(0x2A0000, SeekOrigin.Begin);
            byte[] range = new byte[0x30000];
            await ranged.ReadExactlyAsync(range);
            range.ShouldBe(disc.ExpectedDisc.AsSpan(0x2A0000, 0x30000).ToArray());
        }
        await using var verified = (await reader.OpenVerifiedByRomIdAsync(1)).ShouldNotBeNull();
        using var copy = new MemoryStream();
        await verified.CopyToAsync(copy);
        copy.ToArray().ShouldBe(disc.ExpectedDisc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_StoredChd_RechecksExactEvidenceAndDeliversArtifactWithoutDecoder(bool seekable)
    {
        var fixture = ChdDeliveryFixture.Create();
        var source = new Bytes(fixture.RawBytes);
        var admission = new ChdAdmission(fixture.Evidence.Decoder);
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(fixture.Artifact.ArtifactSha256), Arg.Any<CancellationToken>()).Returns(source);
        var reader = new RomContentReader(_catalog, _cas, _decoder, admission);
        var target = fixture.Target(RomDeliveryFormat.Chd);
        await using (var stream = (seekable ? await reader.OpenSeekableAsync(target) : await reader.OpenAsync(target)).ShouldNotBeNull())
        {
            if (seekable)
            {
                stream.Seek(126, SeekOrigin.Begin);
                byte[] range = new byte[2];
                await stream.ReadExactlyAsync(range);
                range.ShouldBe(fixture.RawBytes.AsSpan(126, 2).ToArray());
                stream.Position = 0;
            }
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            output.ToArray().ShouldBe(fixture.RawBytes);
        }
        source.Disposed.ShouldBeTrue();
        admission.Calls.ShouldBe(1);
        _decoder.ReceivedCalls().ShouldBeEmpty();
        _cas.DidNotReceive().OpenSynchronousRaw(Arg.Any<StorageKey>());
    }

    [Theory]
    [InlineData("bound")]
    [InlineData("bound-seekable")]
    [InlineData("hash")]
    [InlineData("verified")]
    public async Task OpenAsync_OriginalChd_UsesOwnedSynchronousRawAndExactLogicalBytes(string route)
    {
        var fixture = ChdDeliveryFixture.Create();
        var raw = new ChdObservedRaw(fixture.RawBytes);
        var adapter = Substitute.For<IChdOpen>();
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        _catalog.FindAsync(fixture.Descriptor.ContentSha256, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        _cas.OpenSynchronousRaw(StorageKey.FromHash(fixture.Artifact.ArtifactSha256)).Returns(raw);
        adapter.OpenAsync(raw, MatchingEvidence(fixture), fixture.Artifact.ArtifactSha256, null, default)
            .Returns(ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(fixture.LogicalBytes, raw)));
        var reader = ChdReader(fixture, adapter);
        var opened = route switch
        {
            "bound" => await reader.OpenAsync(fixture.Target(RomDeliveryFormat.Original)),
            "bound-seekable" => await reader.OpenSeekableAsync(fixture.Target(RomDeliveryFormat.Original)),
            "hash" => await reader.OpenAsync(fixture.Descriptor.ContentSha256),
            _ => await reader.OpenVerifiedByRomIdAsync(1)
        };
        await using (var stream = opened.ShouldNotBeNull())
        { using var output = new MemoryStream(); await stream.CopyToAsync(output); output.ToArray().ShouldBe(fixture.LogicalBytes); }
        raw.DisposeCalls.ShouldBe(1);
        await _cas.DidNotReceive().OpenRandomAccessAsync(Arg.Any<StorageKey>(), Arg.Any<CancellationToken>());
        await ShouldAdmitNextChdOpenAsync(reader, fixture, adapter);
    }

    [Fact]
    public async Task OpenAsync_OriginalAfterChdReplacement_RefusesStaleTargetAndReadsFreshBinding()
    {
        var original = ChdDeliveryFixture.Create();
        var replacement = ChdReplacement();
        var raw = new ChdObservedRaw(replacement.RawBytes);
        var adapter = Substitute.For<IChdOpen>();
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(replacement.Descriptor);
        _cas.OpenSynchronousRaw(StorageKey.FromHash(replacement.Artifact.ArtifactSha256)).Returns(raw);
        adapter.OpenAsync(raw, MatchingEvidence(replacement), replacement.Artifact.ArtifactSha256, null, default)
            .Returns(ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(replacement.LogicalBytes, raw)));
        var reader = ChdReader(replacement, adapter);
        (await reader.OpenAsync(original.Target(RomDeliveryFormat.Original))).ShouldBeNull();
        _cas.ReceivedCalls().ShouldBeEmpty();
        await using var stream = (await reader.OpenAsync(replacement.Target(RomDeliveryFormat.Original))).ShouldNotBeNull();
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        output.ToArray().ShouldBe(original.LogicalBytes);
        _cas.DidNotReceive().OpenSynchronousRaw(StorageKey.FromHash(original.Artifact.ArtifactSha256));
    }

    [Theory]
    [InlineData("missing-facts")]
    [InlineData("owner")]
    [InlineData("measurement")]
    [InlineData("logical-tuple")]
    [InlineData("unapproved")]
    [InlineData("different-native")]
    public async Task OpenAsync_StoredChdWithdrawnOrChangedEvidence_RefusesBeforeCas(string changed)
    {
        var fixture = ChdDeliveryFixture.Create();
        var current = fixture.Descriptor;
        var admission = new ChdAdmission(fixture.Evidence.Decoder);
        var facts = fixture.Artifact.Facts;
        if (changed is "missing-facts" or "owner" or "measurement" or "logical-tuple")
        {
            var identity = changed == "logical-tuple" ? new RomLogicalIdentity(current.ContentSize, current.ContentSha256,
                current.ContentSha1, current.ContentMd5, Crc32.Parse("ffffffff")) : current.Identity;
            var old = fixture.Evidence.Measurement;
            var proof = new RomRepresentationVerification(Guid.NewGuid(), facts.ArtifactSha256, facts.ArtifactSize, old.Decoder,
                old.CheckedProfile, old.VerifierBuild, old.ObservedAt, [new(RomOutputSelector.Whole.Instance, identity)],
                facts.Container, old.ComputedRawSha1, old.ComputedOverallSha1);
            var updated = new RomRepresentationFacts(changed == "owner" ? 8 : facts.Id, facts.FileId, facts.ArtifactSha256,
                facts.ArtifactSize, facts.Container, facts.Manifest, [new(current.OutputId, 1, RomOutputSelector.Whole.Instance, identity)],
                changed == "missing-facts" ? [] : [proof], []);
            current = new(1, current.OutputId, identity, "disc.iso",
                [new(updated, current.OutputId, true, fixture.Artifact.ArtifactMd5, fixture.Artifact.ArtifactSha1)], [], []);
        }
        if (changed == "unapproved")
            admission.Enabled = false;
        if (changed == "different-native")
            admission.Decoder = ForeignDecoder(fixture.Evidence.Decoder);
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(current);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(fixture.Artifact.ArtifactSha256), Arg.Any<CancellationToken>())
            .Returns(new Bytes(fixture.RawBytes));
        var reader = new RomContentReader(_catalog, _cas, _decoder, admission);
        (await reader.OpenAsync(fixture.Target(RomDeliveryFormat.Chd))).ShouldBeNull();
        _cas.ReceivedCalls().ShouldBeEmpty();
        _decoder.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_StoredChdAdmissionWithdrawn_RefusesNextOpenWithoutReusingPriorDecision()
    {
        var fixture = ChdDeliveryFixture.Create();
        var admission = new ChdAdmission(fixture.Evidence.Decoder);
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(fixture.Artifact.ArtifactSha256), Arg.Any<CancellationToken>())
            .Returns(_ => new Bytes(fixture.RawBytes));
        var reader = new RomContentReader(_catalog, _cas, _decoder, admission);
        await using (var first = (await reader.OpenSeekableAsync(fixture.Target(RomDeliveryFormat.Chd))).ShouldNotBeNull())
            first.Length.ShouldBe(fixture.RawBytes.Length);
        admission.Enabled = false;
        (await reader.OpenSeekableAsync(fixture.Target(RomDeliveryFormat.Chd))).ShouldBeNull();
        await _cas.Received(1).OpenRandomAccessAsync(StorageKey.FromHash(fixture.Artifact.ArtifactSha256), default);
    }

    [Fact]
    public async Task OpenAsync_StoredChdPreferenceChanges_KeepsExactRetainedArtifactBinding()
    {
        var original = ChdDeliveryFixture.Create();
        var replacement = ChdReplacement();
        var current = new RomContentDescriptor(1, replacement.Artifact.OutputId, replacement.Descriptor.Identity, "disc.iso",
            [replacement.Artifact, new(original.Artifact.Facts, original.Artifact.OutputId, false, original.Artifact.ArtifactMd5, original.Artifact.ArtifactSha1)], [], []);
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(current);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(original.Artifact.ArtifactSha256), Arg.Any<CancellationToken>())
            .Returns(new Bytes(original.RawBytes));
        var reader = new RomContentReader(_catalog, _cas, _decoder, new ChdAdmission(original.Evidence.Decoder));
        await using var stream = (await reader.OpenAsync(original.Target(RomDeliveryFormat.Chd))).ShouldNotBeNull();
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        output.ToArray().ShouldBe(original.RawBytes);
        await _cas.DidNotReceive().OpenRandomAccessAsync(StorageKey.FromHash(replacement.Artifact.ArtifactSha256), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_StoredChdRemovedOrCasMissing_DoesNotSubstituteReplacementOrOriginal(bool removed)
    {
        var original = ChdDeliveryFixture.Create();
        var replacement = ChdReplacement();
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(removed ? replacement.Descriptor : original.Descriptor);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(original.Artifact.ArtifactSha256), Arg.Any<CancellationToken>())
            .Returns(removed ? new Bytes(original.RawBytes) : (IRandomAccessContent?)null);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(replacement.Artifact.ArtifactSha256), Arg.Any<CancellationToken>())
            .Returns(new Bytes(replacement.RawBytes));
        var reader = new RomContentReader(_catalog, _cas, _decoder, new ChdAdmission(original.Evidence.Decoder));
        (await reader.OpenAsync(original.Target(RomDeliveryFormat.Chd))).ShouldBeNull();
        await _cas.DidNotReceive().OpenRandomAccessAsync(StorageKey.FromHash(replacement.Artifact.ArtifactSha256), Arg.Any<CancellationToken>());
        _cas.DidNotReceive().OpenSynchronousRaw(Arg.Any<StorageKey>());
        _decoder.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_StoredChdCorruption_VerifiesPhysicalArtifactAtEof()
    {
        var fixture = ChdDeliveryFixture.Create();
        byte[] corrupted = [.. fixture.RawBytes];
        corrupted[^1] ^= 1;
        var source = new Bytes(corrupted);
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(fixture.Artifact.ArtifactSha256), Arg.Any<CancellationToken>()).Returns(source);
        var reader = new RomContentReader(_catalog, _cas, _decoder, new ChdAdmission(fixture.Evidence.Decoder));
        await using (var stream = (await reader.OpenAsync(fixture.Target(RomDeliveryFormat.Chd))).ShouldNotBeNull())
            await Should.ThrowAsync<ContentCorruptedException>(() => stream.CopyToAsync(Stream.Null));
        source.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_OriginalChdWrongLength_DisposesSourceOrDecodedOwner(bool decodedWrong)
    {
        var fixture = ChdDeliveryFixture.Create();
        var raw = new ChdObservedRaw(decodedWrong ? fixture.RawBytes : new byte[127]);
        var adapter = Substitute.For<IChdOpen>();
        var decoded = new ChdObservedDecoded(new byte[8191], raw);
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        _cas.OpenSynchronousRaw(StorageKey.FromHash(fixture.Artifact.ArtifactSha256)).Returns(raw);
        adapter.OpenAsync(raw, MatchingEvidence(fixture), fixture.Artifact.ArtifactSha256, null, default)
            .Returns(ValueTask.FromResult<IRandomAccessContent>(decoded));
        var reader = ChdReader(fixture, adapter);
        await Should.ThrowAsync<InvalidDataException>(() => reader.OpenAsync(fixture.Target(RomDeliveryFormat.Original)));
        raw.DisposeCalls.ShouldBe(1);
        decoded.DisposeCalls.ShouldBe(decodedWrong ? 1 : 0);
        await ShouldAdmitNextChdOpenAsync(reader, fixture, adapter);
    }

    [Fact]
    public async Task OpenAsync_OriginalChdRefusedReadSlot_DisposesRawSnapshotWithoutOpeningIt()
    {
        var fixture = ChdDeliveryFixture.Create();
        var adapter = Substitute.For<IChdOpen>();
        var raw = new ChdObservedRaw(fixture.RawBytes);
        var refused = new ChdObservedRaw(fixture.RawBytes);
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        _cas.OpenSynchronousRaw(StorageKey.FromHash(fixture.Artifact.ArtifactSha256)).Returns(raw, refused);
        adapter.OpenAsync(raw, MatchingEvidence(fixture), fixture.Artifact.ArtifactSha256, null, default)
            .Returns(ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(fixture.LogicalBytes, raw)));
        var reader = ChdReader(fixture, adapter);
        await using (var held = (await reader.OpenAsync(fixture.Target(RomDeliveryFormat.Original))).ShouldNotBeNull())
            await Should.ThrowAsync<ResourceCapacityException>(() => reader.OpenAsync(fixture.Target(RomDeliveryFormat.Original)));
        refused.DisposeCalls.ShouldBe(1);
        adapter.ReceivedCalls().Count().ShouldBe(1);
    }

    [Fact]
    public async Task OpenAsync_OriginalChdCancelledAfterRawOpen_PropagatesTokenAndDisposesBeforeAdapter()
    {
        var fixture = ChdDeliveryFixture.Create();
        var raw = new ChdObservedRaw(fixture.RawBytes);
        using var cancellation = new CancellationTokenSource();
        var adapter = Substitute.For<IChdOpen>();
        _catalog.FindByRomIdAsync(1, cancellation.Token).Returns(fixture.Descriptor);
        _cas.OpenSynchronousRaw(StorageKey.FromHash(fixture.Artifact.ArtifactSha256)).Returns(_ => { cancellation.Cancel(); return raw; });
        var error = await Should.ThrowAsync<OperationCanceledException>(() =>
            ChdReader(fixture, adapter).OpenAsync(fixture.Target(RomDeliveryFormat.Original), cancellation.Token));
        error.CancellationToken.ShouldBe(cancellation.Token);
        raw.DisposeCalls.ShouldBe(1);
        adapter.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_OriginalChdWithoutAdapterOrAdmission_RefusesBeforeStorage()
    {
        var fixture = ChdDeliveryFixture.Create();
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        await using var absent = new RomRepresentationDecoder();
        var noAdapter = new RomContentReader(_catalog, _cas, absent, new ChdAdmission(fixture.Evidence.Decoder));
        (await noAdapter.OpenAsync(fixture.Target(RomDeliveryFormat.Original))).ShouldBeNull();
        var adapter = Substitute.For<IChdOpen>();
        await using var available = new RomRepresentationDecoder(ChdAdapters(adapter), new());
        var noAdmission = new RomContentReader(_catalog, _cas, available);
        (await noAdmission.OpenAsync(fixture.Target(RomDeliveryFormat.Original))).ShouldBeNull();
        _cas.ReceivedCalls().ShouldBeEmpty();
        adapter.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_LogicalChdDecoderCannotReadChd_RefusesBeforeAdmissionOrStorage()
    {
        var fixture = ChdDeliveryFixture.Create();
        var admission = new ChdAdmission(fixture.Evidence.Decoder);
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        // The reader keeps no registry of its own: only the decoder decides that this host has no CHD reader.
        await using var decoder = new RomRepresentationDecoder();
        var reader = new RomContentReader(_catalog, _cas, decoder, admission);

        _catalog.FindAsync(fixture.Descriptor.ContentSha256, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        (await reader.OpenAsync(fixture.Descriptor.ContentSha256)).ShouldBeNull();
        (await reader.OpenVerifiedByRomIdAsync(1)).ShouldBeNull();

        admission.Calls.ShouldBe(0);
        _cas.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("unapproved")]
    [InlineData("different-native")]
    public async Task OpenAsync_OriginalChdAdmissionWithdrawnOrNativeChanged_RefusesNextOpenBeforeStorage(string changed)
    {
        var fixture = ChdDeliveryFixture.Create();
        var raw = new ChdObservedRaw(fixture.RawBytes);
        var admission = new ChdAdmission(fixture.Evidence.Decoder);
        var adapter = Substitute.For<IChdOpen>();
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        _cas.OpenSynchronousRaw(StorageKey.FromHash(fixture.Artifact.ArtifactSha256)).Returns(raw);
        adapter.OpenAsync(raw, MatchingEvidence(fixture), fixture.Artifact.ArtifactSha256, null, default)
            .Returns(ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(fixture.LogicalBytes, raw)));
        await using var decoder = new RomRepresentationDecoder(ChdAdapters(adapter), new() { MaxConcurrent = 1, MaxIdle = 0 });
        var reader = new RomContentReader(_catalog, _cas, decoder, admission);
        await using (var first = (await reader.OpenAsync(fixture.Target(RomDeliveryFormat.Original))).ShouldNotBeNull())
            await first.CopyToAsync(Stream.Null);

        if (changed == "unapproved")
            admission.Enabled = false;
        else
            admission.Decoder = ForeignDecoder(fixture.Evidence.Decoder);

        (await reader.OpenAsync(fixture.Target(RomDeliveryFormat.Original))).ShouldBeNull();
        admission.Calls.ShouldBe(3);
        _cas.Received(1).OpenSynchronousRaw(Arg.Any<StorageKey>());
        adapter.ReceivedCalls().Count().ShouldBe(1);
    }

    [Fact]
    public async Task OpenAsync_EntryAdmissionRefuses_RefusesBeforeStorageOrDecoding()
    {
        _catalog.FindAsync(Logical, Arg.Any<CancellationToken>())
            .Returns(RvzContent(Logical, 4, Artifact, 2));
        var reader = EntryReader(RomRepresentationEncoding.Rvz, new RomReadAdmission(_ => null));

        (await reader.OpenAsync(Logical)).ShouldBeNull();

        _cas.ReceivedCalls().ShouldBeEmpty();
        _decoder.ReceivedCalls().Select(call => call.GetMethodInfo().Name).ShouldBe([nameof(IRomRepresentationDecoder.CanDecode)]);
    }

    [Fact]
    public async Task OpenAsync_EntryAdmissionNamesSynchronousRaw_OpensOnlyThatAccessWithTheAdmittedEvidence()
    {
        var fixture = ChdDeliveryFixture.Create();
        var raw = new ChdObservedRaw([7, 8]);
        var decoded = new Bytes([1, 2, 3, 4]);
        _catalog.FindAsync(Logical, Arg.Any<CancellationToken>())
            .Returns(RvzContent(Logical, 4, Artifact, 2));
        _cas.OpenSynchronousRaw(StorageKey.FromHash(Artifact)).Returns(raw);
        _decoder.DecodeAsync(RomRepresentationEncoding.Rvz, Arg.Any<RomArtifactSource>(), Arg.Any<RomMediaOpenContext>(),
            Arg.Any<CancellationToken>()).Returns(decoded);
        var reader = EntryReader(RomRepresentationEncoding.Rvz,
            new RomReadAdmission(_ => RomOriginalReadDecision.SynchronousRaw(fixture.Evidence)));

        await using (var stream = (await reader.OpenAsync(Logical)).ShouldNotBeNull())
            stream.Length.ShouldBe(4);

        await _cas.DidNotReceive().OpenRandomAccessAsync(Arg.Any<StorageKey>(), Arg.Any<CancellationToken>());
        await _decoder.Received(1).DecodeAsync(RomRepresentationEncoding.Rvz, Arg.Any<RomArtifactSource>(),
            new RomMediaOpenContext(Artifact, fixture.Evidence), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenAsync_RawOnlyEntryAdmittedForRandomAccess_RefusesBeforeStorageOrDecoding()
    {
        var fixture = ChdDeliveryFixture.Create();
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        // A bypass would actually open bytes, so the refusal cannot pass on an unconfigured CAS default.
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(fixture.Artifact.ArtifactSha256), Arg.Any<CancellationToken>())
            .Returns(new Bytes(fixture.RawBytes));
        var reader = EntryReader(RomRepresentationEncoding.Chd, new RomReadAdmission(_ => RomOriginalReadDecision.RandomAccess),
            new ChdAdmission(fixture.Evidence.Decoder));

        (await reader.OpenAsync(fixture.Target(RomDeliveryFormat.Original))).ShouldBeNull();

        await _cas.DidNotReceive().OpenRandomAccessAsync(Arg.Any<StorageKey>(), Arg.Any<CancellationToken>());
        _cas.DidNotReceive().OpenSynchronousRaw(Arg.Any<StorageKey>());
        _decoder.ReceivedCalls().Select(call => call.GetMethodInfo().Name).ShouldBe([nameof(IRomRepresentationDecoder.CanDecode)]);
    }

    [Fact]
    public async Task OpenAsync_OtherEncodingsAndFacets_AreNeverBuiltForTheRead()
    {
        var source = new Bytes([7, 8]);
        var decoded = new Bytes([1, 2, 3, 4]);
        _catalog.FindAsync(Logical, Arg.Any<CancellationToken>())
            .Returns(RvzContent(Logical, 4, Artifact, 2));
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(Artifact), Arg.Any<CancellationToken>()).Returns(source);
        _decoder.DecodeAsync(RomRepresentationEncoding.Rvz, source, default, Artifact).Returns(decoded);
        var entries = RomEncodingComposition.Entries.Select(entry => entry with
        {
            Reader = _ => throw new InvalidOperationException("The content reader built a reader facet."),
            Preparation = _ => throw new InvalidOperationException("The content reader built a preparation facet."),
            ReadAdmission = entry.Descriptor.Encoding == RomRepresentationEncoding.Rvz ? entry.ReadAdmission
                : _ => throw new InvalidOperationException("The content reader built another encoding's admission.")
        }).ToArray();

        await using var stream = (await new RomContentReader(_catalog, _cas, _decoder, entries,
            new RomReadAdmissionDependencies(ManagedAdmission())).OpenAsync(Logical)).ShouldNotBeNull();

        stream.Length.ShouldBe(4);
    }

    [Fact]
    public async Task OpenAsync_OriginalChdMissingOptionalPhysicalHashes_StillRequiresExactNativeEvidence()
    {
        var fixture = ChdDeliveryFixture.Create();
        var artifact = new RomContentArtifact(fixture.Artifact.Facts, fixture.Artifact.OutputId, true);
        var current = new RomContentDescriptor(1, artifact.OutputId, fixture.Descriptor.Identity, "disc.iso", [artifact], [], []);
        var raw = new ChdObservedRaw(fixture.RawBytes);
        var adapter = Substitute.For<IChdOpen>();
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(current);
        _cas.OpenSynchronousRaw(StorageKey.FromHash(fixture.Artifact.ArtifactSha256)).Returns(raw);
        adapter.OpenAsync(raw, MatchingEvidence(fixture), fixture.Artifact.ArtifactSha256, null, default)
            .Returns(ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(fixture.LogicalBytes, raw)));
        await using var stream = (await ChdReader(fixture, adapter).OpenAsync(fixture.Target(RomDeliveryFormat.Original))).ShouldNotBeNull();
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        output.ToArray().ShouldBe(fixture.LogicalBytes);
    }

    [Fact]
    public async Task OpenVerifiedByRomIdAsync_OriginalChdWrongLogicalBytes_FailsLogicalHashAtEof()
    {
        var fixture = ChdDeliveryFixture.Create();
        var raw = new ChdObservedRaw(fixture.RawBytes);
        byte[] logical = [.. fixture.LogicalBytes];
        logical[42] ^= 1;
        var adapter = Substitute.For<IChdOpen>();
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        _cas.OpenSynchronousRaw(StorageKey.FromHash(fixture.Artifact.ArtifactSha256)).Returns(raw);
        adapter.OpenAsync(raw, MatchingEvidence(fixture), fixture.Artifact.ArtifactSha256, null, default)
            .Returns(ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(logical, raw)));
        await using (var stream = (await ChdReader(fixture, adapter).OpenVerifiedByRomIdAsync(1)).ShouldNotBeNull())
            await Should.ThrowAsync<ContentCorruptedException>(() => stream.CopyToAsync(Stream.Null));
        raw.DisposeCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_StoredChdLengthOrCancellation_ClosesRawArtifactBeforeReturningStream(bool cancelled)
    {
        var fixture = ChdDeliveryFixture.Create();
        var source = new Bytes(cancelled ? fixture.RawBytes : new byte[127]);
        using var cancellation = new CancellationTokenSource();
        _catalog.FindByRomIdAsync(1, cancellation.Token).Returns(fixture.Descriptor);
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(fixture.Artifact.ArtifactSha256), cancellation.Token)
            .Returns(_ => { if (cancelled) cancellation.Cancel(); return source; });
        var reader = new RomContentReader(_catalog, _cas, _decoder, new ChdAdmission(fixture.Evidence.Decoder));
        if (cancelled)
        {
            var error = await Should.ThrowAsync<OperationCanceledException>(() =>
                reader.OpenAsync(fixture.Target(RomDeliveryFormat.Chd), cancellation.Token));
            error.CancellationToken.ShouldBe(cancellation.Token);
        }
        else
            await Should.ThrowAsync<InvalidDataException>(() => reader.OpenAsync(fixture.Target(RomDeliveryFormat.Chd), cancellation.Token));
        source.Disposed.ShouldBeTrue();
        _decoder.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_OriginalChdSynchronousRawUnavailable_RefusesCompressedFallback()
    {
        var fixture = ChdDeliveryFixture.Create();
        var adapter = Substitute.For<IChdOpen>();
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        _cas.OpenSynchronousRaw(StorageKey.FromHash(fixture.Artifact.ArtifactSha256))
            .Returns(_ => throw new NotSupportedException("Synchronous raw access does not accept compressed CAS content."));
        // A random-access fallback would be available; the CHD source boundary must still reject it.
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(fixture.Artifact.ArtifactSha256), Arg.Any<CancellationToken>())
            .Returns(new Bytes(fixture.RawBytes));
        var reader = ChdReader(fixture, adapter);
        var error = await Should.ThrowAsync<NotSupportedException>(() => reader.OpenAsync(fixture.Target(RomDeliveryFormat.Original)));
        error.Message.ShouldBe("Synchronous raw access does not accept compressed CAS content.");
        await _cas.DidNotReceive().OpenRandomAccessAsync(Arg.Any<StorageKey>(), Arg.Any<CancellationToken>());
        adapter.ReceivedCalls().ShouldBeEmpty();
        await ShouldAdmitNextChdOpenAsync(reader, fixture, adapter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_OriginalChdAndRvz_ShareOneHostReadBudget(bool chdHeld)
    {
        var fixture = ChdDeliveryFixture.Create();
        var disc = new SyntheticRvzBuilder(0x300000, 0x20000).Padding(0x18000, 0x8000, new byte[68]).Build();
        var rvzArtifact = Sha256.FromSpan(SHA256.HashData(disc.File));
        var rvzLogical = Sha256.FromSpan(SHA256.HashData(disc.ExpectedDisc));
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        _catalog.FindAsync(rvzLogical, Arg.Any<CancellationToken>()).Returns(await MeasuredRvzAsync(disc.File, disc.ExpectedDisc, romId: 9, fileId: 10));
        _cas.OpenRandomAccessAsync(StorageKey.FromHash(rvzArtifact), Arg.Any<CancellationToken>()).Returns(_ => new MemoryContent(disc.File));
        _cas.OpenSynchronousRaw(StorageKey.FromHash(fixture.Artifact.ArtifactSha256)).Returns(_ => new ChdObservedRaw(fixture.RawBytes));
        var adapter = Substitute.For<IChdOpen>();
        adapter.OpenAsync(Arg.Any<ISynchronousRawContent>(), MatchingEvidence(fixture), fixture.Artifact.ArtifactSha256, null, default)
            .Returns(call => ValueTask.FromResult<IRandomAccessContent>(
                new ChdObservedDecoded(fixture.LogicalBytes, call.Arg<ISynchronousRawContent>())));
        var adapters = ChdAdapters(adapter);
        await using var decoder = new RomRepresentationDecoder(adapters, new() { MaxConcurrent = 1, MaxIdle = 0 });
        var reader = new RomContentReader(_catalog, _cas, decoder, new ChdAdmission(fixture.Evidence.Decoder));
        Func<Task<Stream?>> chd = () => reader.OpenAsync(fixture.Target(RomDeliveryFormat.Original));
        Func<Task<Stream?>> rvz = () => reader.OpenAsync(rvzLogical);

        await using (var held = (await (chdHeld ? chd : rvz)()).ShouldNotBeNull())
            await Should.ThrowAsync<ResourceCapacityException>(chdHeld ? rvz : chd);

        await using var next = (await (chdHeld ? rvz : chd)()).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("header")]
    [InlineData("native")]
    public async Task OpenAsync_OriginalChdReopenWithIdleCapacity_RechecksHeaderAndNativeBuildOnEveryOpen(string changed)
    {
        var fixture = ChdDeliveryFixture.Create();
        var native = fixture.Native;
        var raw = fixture.RawBytes;
        int opened = 0;
        byte[] tampered = [.. fixture.RawBytes];
        tampered[64] ^= 1;
        ReadDecoderOptions limits = new() { MaxConcurrent = 2, MaxIdle = 2, CacheBytes = 8192 };
        await using var chd = new ChdMediaAdapter(new MediaBlockCache(8192), limits, _ => native, (source, _, _, _, _, _) =>
        { opened++; return ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(fixture.LogicalBytes, source)); });
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(chd), limits);
        // Admission keeps approving, so only the adapter's own per-open checks can refuse the reopen.
        var reader = new RomContentReader(_catalog, _cas, decoder, new ChdAdmission(fixture.Evidence.Decoder));
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(fixture.Descriptor);
        _cas.OpenSynchronousRaw(StorageKey.FromHash(fixture.Artifact.ArtifactSha256)).Returns(_ => new ChdObservedRaw(raw));
        await using (var first = (await reader.OpenAsync(fixture.Target(RomDeliveryFormat.Original))).ShouldNotBeNull())
            await first.CopyToAsync(Stream.Null);

        if (changed == "header")
            raw = tampered;
        else
            native = native with { RecipeVersion = native.RecipeVersion + 1 };

        await Should.ThrowAsync<InvalidDataException>(() => reader.OpenAsync(fixture.Target(RomDeliveryFormat.Original)));
        opened.ShouldBe(1);
    }

    // Original CHD opens go through a real decoder, the only component that knows the host can read CHD.
    // One read slot, so ShouldAdmitNextChdOpenAsync can observe that earlier opens left it free.
    private RomContentReader ChdReader(ChdDeliveryFixture fixture, IChdOpen adapter)
    {
        var decoder = new RomRepresentationDecoder(ChdAdapters(adapter), new() { MaxConcurrent = 1, MaxIdle = 0 });
        _ownedDecoders.Add(decoder);
        return new(_catalog, _cas, decoder, new ChdAdmission(fixture.Evidence.Decoder));
    }

    private async Task ShouldAdmitNextChdOpenAsync(RomContentReader reader, ChdDeliveryFixture fixture, IChdOpen adapter)
    {
        var next = new ChdObservedRaw(fixture.RawBytes);
        _cas.Configure().OpenSynchronousRaw(StorageKey.FromHash(fixture.Artifact.ArtifactSha256)).Returns(next);
        adapter.OpenAsync(next, MatchingEvidence(fixture), fixture.Artifact.ArtifactSha256, null, default)
            .Returns(ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(fixture.LogicalBytes, next)));
        var stream = (await reader.OpenAsync(fixture.Target(RomDeliveryFormat.Original))).ShouldNotBeNull();
        await stream.DisposeAsync();
        next.DisposeCalls.ShouldBe(1);
    }

    private static RomMediaAdapterRegistry ChdAdapters(IChdOpen open) =>
        new(new RvzMediaAdapter(), new IdentityMediaAdapter(), new ChdTestAdapter(open));

    /// <summary>The CHD open a substitute observes, exactly as the decoder hands it to the real adapter.</summary>
    public interface IChdOpen
    {
        ValueTask<IRandomAccessContent> OpenAsync(ISynchronousRawContent source, RomChdDeliveryEvidence evidence,
            Sha256 artifact, TimeSpan? admissionTimeout = null, CancellationToken ct = default);
    }

    private sealed class ChdTestAdapter(IChdOpen open) : IRomMediaAdapter
    {
        public RomRepresentationEncoding Encoding => RomRepresentationEncoding.Chd;
        public bool ReusesIdleImages => false;
        public ValueTask<IRandomAccessContent> OpenAsync(RomArtifactSource source, RomMediaOpenContext context,
            MediaBlockCache cache, CancellationToken ct) =>
            open.OpenAsync(source.OpenSynchronousRaw(), context.ChdEvidence!, context.Artifact!.Value, context.AdmissionTimeout, ct);
    }

    private static RomChdDeliveryEvidence MatchingEvidence(ChdDeliveryFixture fixture) =>
        Arg.Is<RomChdDeliveryEvidence>(evidence => evidence.Bind() == fixture.Evidence.Bind() &&
            evidence.Facts.ArtifactSha256 == fixture.Artifact.ArtifactSha256 && evidence.Output.Identity == fixture.Descriptor.Identity);

    private static ChdDeliveryFixture ChdReplacement()
    {
        var fixture = ChdDeliveryFixture.Create(24);
        var old = fixture.Artifact.Facts;
        var facts = new RomRepresentationFacts(8, 3, old.ArtifactSha256, old.ArtifactSize, old.Container, old.Manifest,
            [new(12, 1, RomOutputSelector.Whole.Instance, fixture.Descriptor.Identity)], old.Verifications, []);
        var artifact = new RomContentArtifact(facts, 12, true, fixture.Artifact.ArtifactMd5, fixture.Artifact.ArtifactSha1);
        var evidence = artifact.ChdEvidence.Single();
        var descriptor = new RomContentDescriptor(1, 12, fixture.Descriptor.Identity, "disc.iso", [artifact], [], []);
        return fixture with { Descriptor = descriptor, Artifact = artifact, Evidence = evidence };
    }

    private static RomChdDecoderBuild ForeignDecoder(RomChdDecoderBuild decoder) =>
        new(decoder.Rid, decoder.UpstreamCommit, decoder.RecipeVersion + 1);

    private sealed class ChdAdmission(RomChdDecoderBuild decoder) : IRomChdDeliveryAdmission
    {
        internal bool Enabled { get; set; } = true;
        internal int Calls { get; private set; }
        internal RomChdDecoderBuild Decoder { get; set; } = decoder;
        public RomChdDeliveryAssessment? Assess(RomChdDeliveryEvidence evidence)
        { Calls++; return Enabled ? new(Decoder) : null; }
        public RomManagedReadBinding? AssessManaged(RomContentArtifact artifact) => ManagedAdmission().AssessManaged(artifact);
        public bool AdmitsCd(RomReconstruction.CdMember member) => false;
        public RomChdDecoderBuild? AssessCd(RomReconstruction.CdMember member) => null;
    }

    private RomContentReader Reader() => new(_catalog, _cas, _decoder, ManagedAdmission());

    private RomContentReader EntryReader(RomRepresentationEncoding encoding, RomReadAdmission admission,
        IRomChdDeliveryAdmission? authority = null) =>
        new(_catalog, _cas, _decoder, RomEncodingComposition.Entries.Select(entry =>
            entry.Descriptor.Encoding == encoding ? entry with { ReadAdmission = _ => admission } : entry).ToArray(),
            new RomReadAdmissionDependencies(authority));

    private static ChdDeliveryAdmission ManagedAdmission()
    {
        var media = RomMediaPolicyComposition.Create();
        var runtime = new ChdDeploymentRuntime(new NativeToolOptions(), requireTool: false);
        return new(runtime, new MediaHostCapabilities(runtime, media, encodes: false), media.Kinds);
    }

    // Controlled complete declarations for mocked IO, cancellation and disposal tests.
    // Actual managed reconstruction below captures the real header and independently hashes the original bytes.
    private static RomLogicalIdentity Identity(Sha256 sha256, long size) =>
        new(size, sha256, Sha1.Parse(new string('c', 40)), Md5.Parse(new string('d', 32)), Crc32.FromUInt32(0));
    private static RomLogicalIdentity Identity(byte[] bytes) => new(bytes.Length, Sha256.FromSpan(SHA256.HashData(bytes)),
        Sha1.FromSpan(SHA1.HashData(bytes)), Md5.FromSpan(MD5.HashData(bytes)), Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(bytes)));
    private static RomContainerFacts.Rvz Rvz(long logicalBytes) => new(0x1000000, 1, 5, 19, 131072, false, logicalBytes);
    private static RomContentDescriptor RvzContent(Sha256 logical, long logicalSize, Sha256 artifact, long artifactSize, int fileId = 2) =>
        WholeContent(1, fileId, Identity(logical, logicalSize), artifact, artifactSize, Rvz(logicalSize));
    private static RomContentDescriptor WholeContent(int romId, int fileId, RomLogicalIdentity identity,
        Sha256 artifact, long artifactSize, RomContainerFacts container)
    {
        var proof = new RomRepresentationVerification(Guid.NewGuid(), artifact, artifactSize,
            new RomDecoderIdentity.Managed(MediaInspectionService.DecoderBuild), "complete-test-read/v1", "test-verifier",
            DateTimeOffset.UnixEpoch.AddTicks(17), [new(RomOutputSelector.Whole.Instance, identity)], container);
        var facts = new RomRepresentationFacts(fileId + 100, fileId, artifact, artifactSize, container,
            RomReconstructionManifest.WholeFile.Instance, [new(fileId + 200, romId, RomOutputSelector.Whole.Instance, identity)], [proof], []);
        return new(romId, facts.Outputs[0].Id, identity, "disc.iso", [new(facts, facts.Outputs[0].Id, true)], [], []);
    }
    private RomDeliveryTarget RvzTarget(Sha256 artifact, long artifactSize, long logicalSize = 4)
    {
        var current = RvzContent(Logical, logicalSize, artifact, artifactSize);
        _catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(current);
        return Target(current, RomDeliveryFormat.Rvz);
    }
    private static RomDeliveryTarget Target(RomContentDescriptor current, RomDeliveryFormat format) =>
        RomDeliveryPlanner.Plan(current, "disc.iso", RomMediaPolicyComposition.CreateDeliveryPolicy(), chdAdmission: ManagedAdmission())
            .Single(option => option.Format == format).Target.ShouldNotBeNull();
    private static async Task<RomContentDescriptor> MeasuredRvzAsync(byte[] artifact, byte[] logical, int romId = 1, int fileId = 2)
    {
        await using var image = await RvzImage.OpenAsync(new MemoryContent(artifact), new RvzReadOptions(), new MediaBlockCache(8192));
        var header = image.Descriptor;
        return WholeContent(romId, fileId, Identity(logical), Sha256.FromSpan(SHA256.HashData(artifact)), artifact.Length,
            new RomContainerFacts.Rvz(header.Version, header.DiscType, header.Compression, header.CompressionLevel,
                header.ChunkSize, header.HasWiiPartitions, image.Length));
    }

    private sealed class CancellingHeader(CancellationTokenSource cancellation) : IRandomAccessContent
    {
        public bool Disposed { get; private set; }
        public CancellationToken ObservedToken { get; private set; }
        public long Length => 4;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            ObservedToken = ct;
            "RVZ\u0001"u8.CopyTo(destination.Span);
            cancellation.Cancel();
            return ValueTask.FromResult(4);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Bytes(byte[] bytes) : IRandomAccessContent
    {
        public bool Disposed { get; private set; }
        public long Length => bytes.Length;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(destination.Length, Math.Max(0, bytes.Length - offset));
            bytes.AsMemory((int)Math.Min(offset, bytes.Length), count).CopyTo(destination);
            return ValueTask.FromResult(count);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
