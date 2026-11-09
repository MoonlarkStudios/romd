using System.Buffers.Binary;
using System.Security.Cryptography;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Media.Chd;
using Romd.Media.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <remarks>Finite reader ownership/admission observers include verified native-gated lease regressions and fake-opener contract cases; not real-disc qualification.</remarks>
public sealed class ChdMediaAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodeAsync_CancelledNativeDemand_HoldsOriginalLeaseAdmissionUntilDisposeJoins(bool healthySharedWaiter)
    {
        var candidate = ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory);
        ChdNativeIdentity native = candidate.VerifyAndLoad();
        var image = SyntheticChdFixture.Create(hunks: 2, finalBytes: 4096);
        var fixture = ChdDeliveryFixture.Create(physical: image, nativeBuild: native);
        var source = new CountedRawContent(fixture.RawBytes, image.PayloadOffset, image.HunkBytes);
        var cache = new MediaBlockCache(0);
        var options = new ReadDecoderOptions { MaxConcurrent = healthySharedWaiter ? 2 : 1, MaxIdle = 0, CacheBytes = 0 };
        await using var adapter = new ChdMediaAdapter(candidate, cache, options);
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(adapter), options, cache);
        RomMediaOpenContext Context() => new(fixture.Artifact.ArtifactSha256, fixture.Evidence);
        var origin = await decoder.DecodeAsync(RomRepresentationEncoding.Chd,
            RomArtifactSource.FromSynchronousRaw(source), Context());
        IRandomAccessContent? healthy = healthySharedWaiter ? await decoder.DecodeAsync(RomRepresentationEncoding.Chd,
            RomArtifactSource.FromSynchronousRaw(new ChdObservedRaw(fixture.RawBytes)), Context()) : null;
        using var cancellation = new CancellationTokenSource();
        source.Arm();
        Task<int> cancelled = origin.ReadAtAsync(0, new byte[53], cancellation.Token).AsTask();
        byte[] bytes = new byte[53];
        Task<int>? joined = healthy?.ReadAtAsync(0, bytes).AsTask();
        Task? disposal = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => cancelled);
            disposal = origin.DisposeAsync().AsTask();
            disposal.IsCompleted.ShouldBeFalse();
            var refused = new ChdObservedRaw(fixture.RawBytes);
            await Should.ThrowAsync<ResourceCapacityException>(() => decoder.DecodeAsync(RomRepresentationEncoding.Chd,
                RomArtifactSource.FromSynchronousRaw(refused), Context()));
            refused.DisposeCalls.ShouldBe(1);
            source.DisposeCalls.ShouldBe(0);
            source.PayloadReads.ShouldBe(1);
        }
        finally
        {
            source.Release();
            if (joined is not null)
                await joined;
            await (disposal ?? origin.DisposeAsync().AsTask());
        }
        if (healthy is not null)
        {
            bytes.ShouldBe(fixture.LogicalBytes.AsSpan(0, bytes.Length).ToArray());
            await healthy.DisposeAsync();
        }
        source.DisposeCalls.ShouldBe(1);
        await using var next = await decoder.DecodeAsync(RomRepresentationEncoding.Chd,
            RomArtifactSource.FromSynchronousRaw(new ChdObservedRaw(fixture.RawBytes)), Context());
        (await next.ReadAtAsync(4096, bytes)).ShouldBe(bytes.Length);
        bytes.ShouldBe(fixture.LogicalBytes.AsSpan(4096, bytes.Length).ToArray());
    }

    [Fact]
    public async Task ReadAtAsync_TwoAdmittedLeases_SpeculativeOwnerJoinsThenDemandsAlongsideUnrelatedDemand()
    {
        var candidate = ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory);
        var native = candidate.VerifyAndLoad();
        var image = SyntheticChdFixture.Create(hunks: 5, finalBytes: 4096);
        var otherImage = SyntheticChdFixture.Create(hunks: 5, finalBytes: 4096, seed: 199);
        var fixture = ChdDeliveryFixture.Create(physical: image, nativeBuild: native);
        var otherFixture = ChdDeliveryFixture.Create(physical: otherImage, nativeBuild: native);
        var scanRaw = new CountedRawContent(fixture.RawBytes, image.PayloadOffset, 4096);
        var otherRaw = new CountedRawContent(otherFixture.RawBytes, otherImage.PayloadOffset, 4096);
        var cache = new MediaBlockCache(0);
        var options = new ReadDecoderOptions { MaxConcurrent = 2, MaxIdle = 0, CacheBytes = 0 };
        await using var adapter = new ChdMediaAdapter(candidate, cache, options);
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(adapter), options, cache);
        await using var scan = await decoder.DecodeAsync(RomRepresentationEncoding.Chd,
            RomArtifactSource.FromSynchronousRaw(scanRaw), new(fixture.Artifact.ArtifactSha256, fixture.Evidence));
        await using var other = await decoder.DecodeAsync(RomRepresentationEncoding.Chd,
            RomArtifactSource.FromSynchronousRaw(otherRaw), new(otherFixture.Artifact.ArtifactSha256, otherFixture.Evidence));
        scanRaw.Arm(blockAtHunk: 1);
        otherRaw.Arm(blockAtHunk: 0);
        Task<int>? otherDemand = null, distinct = null;
        byte[] bytes = new byte[53];
        try
        {
            await scan.ReadAtAsync(0, new byte[2048]);
            await scan.ReadAtAsync(2048, new byte[2048]);
            await scanRaw.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            otherDemand = other.ReadAtAsync(0, new byte[53]).AsTask();
            await otherRaw.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            adapter.Snapshot.ActiveHandles.ShouldBe(2);
            adapter.Snapshot.Speculations.ShouldBe(1);
            distinct = scan.ReadAtAsync(3 * 4096, bytes).AsTask();
            distinct.IsCompleted.ShouldBeFalse();
            scanRaw.PayloadReads.ShouldBe(3); // Zero-cache two foreground reads and the gated prefetch.
            // Drain only this lease's speculation. Its zero-timeout new demand now succeeds while
            // the unrelated admitted lease still holds the other native handle.
            scanRaw.Release();
            (await distinct).ShouldBe(bytes.Length);
            bytes.ShouldBe(fixture.LogicalBytes.AsSpan(3 * 4096, bytes.Length).ToArray());
            otherDemand.IsCompleted.ShouldBeFalse();
            adapter.Snapshot.PeakActiveHandles.ShouldBe(2);
            adapter.Snapshot.PeakLiveHandles.ShouldBe(2);
            scanRaw.PeakActive.ShouldBe(1);
        }
        finally
        {
            scanRaw.Release();
            otherRaw.Release();
            if (distinct is not null)
                await distinct;
            if (otherDemand is not null)
                await otherDemand;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_ExactProof_ForwardsOwnedRawSnapshotSharedPoolAndBoundedWait(bool boundedWait)
    {
        var fixture = ChdDeliveryFixture.Create();
        var raw = new ChdObservedRaw(fixture.RawBytes);
        var cache = new MediaBlockCache(8192);
        int verified = 0, opened = 0;
        ChdReadPool? capturedPool = null;
        TimeSpan? timeout = boundedWait ? TimeSpan.FromSeconds(2) : null;
        await using var adapter = new ChdMediaAdapter(cache, new() { MaxConcurrent = 1, MaxIdle = 0, CacheBytes = 8192 },
            _ => { verified++; return fixture.Native; }, (source, pool, limits, identity, wait, ct) =>
            {
                source.ShouldBeSameAs(raw);
                identity.ShouldBe(fixture.Descriptor.ArtifactSha256.ToString());
                wait.ShouldBe(timeout);
                limits.MaximumHunkBytes.ShouldBe(1U << 20);
                capturedPool = pool;
                opened++;
                return ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(fixture.LogicalBytes, source));
            });
        var content = await adapter.OpenAsync(raw, fixture.Evidence, fixture.Descriptor.ArtifactSha256, timeout);
        byte[] bytes = new byte[3072];
        (await content.ReadAtAsync(3072, bytes)).ShouldBe(bytes.Length);
        bytes.ShouldBe(fixture.LogicalBytes.AsSpan(3072, bytes.Length).ToArray());
        verified.ShouldBe(1);
        opened.ShouldBe(1);
        capturedPool.ShouldNotBeNull();
        raw.DisposeCalls.ShouldBe(0);
        await content.DisposeAsync();
        raw.DisposeCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData("binary-size")]
    [InlineData("binary-hash")]
    [InlineData("family")]
    [InlineData("upstream-version")]
    [InlineData("build")]
    public async Task OpenAsync_RebuiltDecoderWithTheRetainedTuple_OpensTheDevice(string changed)
    {
        var fixture = ChdDeliveryFixture.Create();
        var raw = new ChdObservedRaw(fixture.RawBytes);
        int opened = 0;
        var rebuilt = changed switch
        {
            "binary-size" => fixture.Native with { BinaryBytes = fixture.Native.BinaryBytes + 1 },
            "binary-hash" => fixture.Native with { BinarySha256 = new string('a', 64) },
            "family" => fixture.Native with { FamilyVersion = "rebuilt-family" },
            "upstream-version" => fixture.Native with { UpstreamVersion = "rebuilt-describe" },
            _ => fixture.Native with { BuildId = new string('a', 64) }
        };
        await using var adapter = Adapter(_ => rebuilt, (source, _, _, _, _, _) =>
        { opened++; return ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(fixture.LogicalBytes, source)); });

        await using var content = await adapter.OpenAsync(raw, fixture.Evidence, fixture.Descriptor.ArtifactSha256);

        content.Length.ShouldBe(fixture.LogicalBytes.Length);
        opened.ShouldBe(1);
    }

    [Theory]
    [InlineData("rid")]
    [InlineData("upstream-commit")]
    [InlineData("recipe")]
    public async Task OpenAsync_OneDifferentTupleFact_RefusesBeforeDeviceAndClosesSnapshot(string changed)
    {
        var fixture = ChdDeliveryFixture.Create();
        var raw = new ChdObservedRaw(fixture.RawBytes);
        int opened = 0;
        var foreign = changed switch
        {
            "rid" => fixture.Native with { Rid = "linux-arm64" },
            "upstream-commit" => fixture.Native with { UpstreamCommit = new string('a', 40) },
            _ => fixture.Native with { RecipeVersion = fixture.Native.RecipeVersion + 1 }
        };
        await using var adapter = Adapter(_ => foreign, (source, _, _, _, _, _) =>
        { opened++; return ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(fixture.LogicalBytes, source)); });
        var error = await Should.ThrowAsync<InvalidDataException>(() =>
            adapter.OpenAsync(raw, fixture.Evidence, fixture.Descriptor.ArtifactSha256).AsTask());
        error.Message.ShouldBe("Deployed CHD decoder disagrees with the retained verified build.");
        opened.ShouldBe(0);
        raw.DisposeCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData("logical")]
    [InlineData("hunk")]
    [InlineData("unit")]
    [InlineData("codec")]
    [InlineData("raw-hash")]
    [InlineData("overall-hash")]
    [InlineData("parent")]
    public async Task OpenAsync_ChangedBoundHeader_RefusesBeforeNativeVerificationAndDisposes(string changed)
    {
        var fixture = ChdDeliveryFixture.Create();
        byte[] bytes = [.. fixture.RawBytes];
        switch (changed)
        {
            case "logical":
                BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(32), 16384);
                break;
            case "hunk":
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(56), 8192);
                break;
            case "unit":
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(60), 2448);
                break;
            case "codec":
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 0x6c7a6d61);
                break;
            case "raw-hash":
                bytes[64] ^= 1;
                break;
            case "overall-hash":
                bytes[84] ^= 1;
                break;
            default:
                bytes[104] = 1;
                break;
        }
        var raw = new ChdObservedRaw(bytes);
        int verified = 0, opened = 0;
        await using var adapter = Adapter(_ => { verified++; return fixture.Native; }, (source, _, _, _, _, _) =>
        { opened++; return ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(fixture.LogicalBytes, source)); });
        await Should.ThrowAsync<InvalidDataException>(() => adapter.OpenAsync(raw, fixture.Evidence,
            fixture.Descriptor.ArtifactSha256).AsTask());
        verified.ShouldBe(0);
        opened.ShouldBe(0);
        raw.DisposeCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_CancellationBeforeOrAfterNativeVerification_PreservesTokenAndClosesSource(bool afterVerify)
    {
        var fixture = ChdDeliveryFixture.Create();
        var raw = new ChdObservedRaw(fixture.RawBytes);
        using var cancellation = new CancellationTokenSource();
        int opened = 0;
        if (!afterVerify)
            cancellation.Cancel();
        await using var adapter = Adapter(ct => { ct.ShouldBe(cancellation.Token); cancellation.Cancel(); return fixture.Native; },
            (source, _, _, _, _, _) => { opened++; return ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(fixture.LogicalBytes, source)); });
        var error = await Should.ThrowAsync<OperationCanceledException>(() =>
            adapter.OpenAsync(raw, fixture.Evidence, fixture.Descriptor.ArtifactSha256, ct: cancellation.Token).AsTask());
        error.CancellationToken.ShouldBe(cancellation.Token);
        opened.ShouldBe(0);
        raw.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task OpenAsync_AdapterDisposed_RefusesSubsequentOpenAndClosesIncomingSnapshot()
    {
        var fixture = ChdDeliveryFixture.Create();
        int verified = 0;
        var adapter = Adapter(_ => { verified++; return fixture.Native; }, (source, _, _, _, _, _) =>
            ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(fixture.LogicalBytes, source)));
        await adapter.DisposeAsync();
        await adapter.DisposeAsync();
        var raw = new ChdObservedRaw(fixture.RawBytes);
        await Should.ThrowAsync<ObjectDisposedException>(() => adapter.OpenAsync(raw, fixture.Evidence,
            fixture.Descriptor.ArtifactSha256).AsTask());
        verified.ShouldBe(0);
        raw.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task OpenAsync_DeviceWrongLogicalLength_DisposesDeviceAndOwnedSourceOnce()
    {
        var fixture = ChdDeliveryFixture.Create();
        var raw = new ChdObservedRaw(fixture.RawBytes);
        var wrong = new ChdObservedDecoded(new byte[8191], raw);
        await using var adapter = Adapter(_ => fixture.Native, (_, _, _, _, _, _) => ValueTask.FromResult<IRandomAccessContent>(wrong));
        await Should.ThrowAsync<InvalidDataException>(() => adapter.OpenAsync(raw, fixture.Evidence,
            fixture.Descriptor.ArtifactSha256).AsTask());
        wrong.DisposeCalls.ShouldBe(1);
        raw.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task OpenAsync_NativeVerificationThrows_DisposesSnapshotWithoutOpeningDevice()
    {
        var fixture = ChdDeliveryFixture.Create();
        var raw = new ChdObservedRaw(fixture.RawBytes);
        int opened = 0;
        await using var adapter = Adapter(_ => throw new InvalidDataException("native assets unavailable"), (source, _, _, _, _, _) =>
        { opened++; return ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(fixture.LogicalBytes, source)); });
        var error = await Should.ThrowAsync<InvalidDataException>(() => adapter.OpenAsync(raw, fixture.Evidence,
            fixture.Descriptor.ArtifactSha256).AsTask());
        error.Message.ShouldBe("native assets unavailable");
        opened.ShouldBe(0);
        raw.DisposeCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_OpenOrReadCapacityTimeout_UsesExistingBackpressureAndDisposesOwnedSource(bool duringRead)
    {
        var fixture = ChdDeliveryFixture.Create();
        var raw = new ChdObservedRaw(fixture.RawBytes);
        await using var adapter = Adapter(_ => fixture.Native, (source, _, _, _, _, _) =>
        {
            if (duringRead)
                return ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(fixture.LogicalBytes, source, timeout: true));
            source.Dispose(); // Device opener owns its input even when admission fails.
            throw new TimeoutException("pool full");
        });
        if (duringRead)
        {
            var content = await adapter.OpenAsync(raw, fixture.Evidence, fixture.Descriptor.ArtifactSha256);
            await Should.ThrowAsync<ResourceCapacityException>(() => content.ReadAtAsync(0, new byte[1]).AsTask());
            await content.DisposeAsync();
        }
        else
            await Should.ThrowAsync<ResourceCapacityException>(() =>
            adapter.OpenAsync(raw, fixture.Evidence, fixture.Descriptor.ArtifactSha256).AsTask());
        raw.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task OpenAsync_CancellationAfterDeviceOpen_DisposesDeviceAndPreservesToken()
    {
        var fixture = ChdDeliveryFixture.Create();
        var raw = new ChdObservedRaw(fixture.RawBytes);
        var device = new ChdObservedDecoded(fixture.LogicalBytes, raw);
        using var cancellation = new CancellationTokenSource();
        await using var adapter = Adapter(_ => fixture.Native, (_, _, _, _, _, ct) =>
        { ct.ShouldBe(cancellation.Token); cancellation.Cancel(); return ValueTask.FromResult<IRandomAccessContent>(device); });
        var error = await Should.ThrowAsync<OperationCanceledException>(() =>
            adapter.OpenAsync(raw, fixture.Evidence, fixture.Descriptor.ArtifactSha256, ct: cancellation.Token).AsTask());
        error.CancellationToken.ShouldBe(cancellation.Token);
        device.DisposeCalls.ShouldBe(1);
        raw.DisposeCalls.ShouldBe(1);
    }

    private static ChdMediaAdapter Adapter(Func<CancellationToken, ChdNativeIdentity> verify,
        ChdMediaAdapter.DeviceOpener open) => new(new MediaBlockCache(8192),
            new() { MaxConcurrent = 1, MaxIdle = 0, CacheBytes = 8192 }, verify, open);
}

// A bounded header/proof fixture for fake-device ownership tests. It is not a decodable native CHD.
internal sealed record ChdDeliveryFixture(byte[] RawBytes, byte[] LogicalBytes, RomContentDescriptor Descriptor,
    RomContentArtifact Artifact, RomChdDeliveryEvidence Evidence, ChdNativeIdentity Native)
{
    internal RomDeliveryTarget Target(RomDeliveryFormat format) => new(Descriptor.RomId, Artifact.FileId, format,
        format == RomDeliveryFormat.Original ? "disc.iso" : "disc.chd",
        format == RomDeliveryFormat.Original ? Descriptor.ContentSha256 : Artifact.ArtifactSha256,
        format == RomDeliveryFormat.Original ? Descriptor.ContentSize : Artifact.ArtifactSize,
        Descriptor.ContentSha256, Descriptor.ContentSize, Artifact.ArtifactSha256, Artifact.ArtifactSize,
        RomRepresentationEncoding.Chd, format == RomDeliveryFormat.Original ? Descriptor.ContentMd5 : Artifact.ArtifactMd5,
        format == RomDeliveryFormat.Original ? Descriptor.ContentSha1 : Artifact.ArtifactSha1, Evidence.Bind());

    internal static ChdDeliveryFixture Create(byte marker = 23, string rid = "linux-x64",
        SyntheticChdFixture? physical = null, ChdNativeIdentity? nativeBuild = null)
    {
        byte[] logical = physical?.Expected(0, checked((int)physical.LogicalBytes)) ??
            Enumerable.Range(0, 8192).Select(index => (byte)(index * 13 + 7)).ToArray();
        var content = Sha256.FromSpan(SHA256.HashData(logical));
        var sha1 = Sha1.FromSpan(SHA1.HashData(logical));
        var md5 = Md5.FromSpan(MD5.HashData(logical));
        var crc = Crc32.Parse("00000000");
        var decoder = nativeBuild is null ? new RomChdDecoderBuild(rid, Sha1.Parse(new string('f', 40)), 1) :
            ChdBuildEvidence.Decoder(nativeBuild);
        uint hunkBytes = checked((uint)(physical?.HunkBytes ?? 4096));
        byte[] raw = physical?.Bytes.ToArray() ?? new byte[152];
        "MComprHD"u8.CopyTo(raw);
        RomChdMetadataEntry[] metadata = physical is null ? [new(0x44564420, 1, [0])] : [];
        if (physical is null)
        {
            BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(48), 124);
            BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(124), metadata[0].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(128), 0x01000001);
        }
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(8), 124);
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(16), 0x7a6c6962);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(32), (ulong)logical.Length);
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(56), hunkBytes);
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(60), 2048);
        SHA1.HashData(logical).CopyTo(raw, 64);
        byte[] overall = Enumerable.Repeat((byte)0x51, 20).ToArray();
        overall.CopyTo(raw, 84);
        if (physical is null)
            raw[^1] = marker;
        var artifactHash = Sha256.FromSpan(SHA256.HashData(raw));
        var container = new RomContainerFacts.Chd(5, logical.Length, hunkBytes, 2048, checked((uint)((logical.Length + hunkBytes - 1) / hunkBytes)),
            [0x7a6c6962, 0, 0, 0], false, sha1, Sha1.FromSpan(overall), metadata);
        var identity = new RomLogicalIdentity(logical.Length, content, sha1, md5, crc);
        var measurement = new RomRepresentationVerification(Guid.NewGuid(), artifactHash, raw.Length, new RomDecoderIdentity.Native(decoder),
            "test-independent-measurement/v1", "test-verifier", new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero).AddTicks(47),
            [new(RomOutputSelector.Whole.Instance, identity)], container, sha1, container.StoredOverallSha1);
        var facts = new RomRepresentationFacts(7, 2, artifactHash, raw.Length, container, RomReconstructionManifest.WholeFile.Instance,
            [new(11, 1, RomOutputSelector.Whole.Instance, identity)], [measurement], []);
        var evidence = new RomChdDeliveryEvidence(facts, 11, measurement.Id);
        var artifact = new RomContentArtifact(facts, 11, true, Md5.FromSpan(MD5.HashData(raw)), Sha1.FromSpan(SHA1.HashData(raw)));
        var descriptor = new RomContentDescriptor(1, 11, identity, "disc.iso", [artifact], [], []);
        var native = nativeBuild ?? new ChdNativeIdentity(decoder.Rid, "libmoonlark_chdr.so", 100, new string('c', 64),
            decoder.UpstreamCommit.ToString(), "test-only-upstream", decoder.RecipeVersion, "test-only-family",
            new string('e', 64), true, true, true);
        return new(raw, logical, descriptor, artifact, evidence, native);
    }
}

internal sealed class ChdObservedRaw(byte[] bytes) : ISynchronousRawContent
{
    internal int DisposeCalls { get; private set; }
    public long Length => bytes.Length;
    public int ReadAt(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
        int count = (int)Math.Min(destination.Length, Math.Max(0, bytes.Length - offset));
        bytes.AsSpan((int)Math.Min(offset, bytes.Length), count).CopyTo(destination);
        return count;
    }
    public void Dispose() => DisposeCalls++;
}

internal sealed class ChdObservedDecoded(byte[] bytes, ISynchronousRawContent source, bool timeout = false) : IRandomAccessContent
{
    internal int DisposeCalls { get; private set; }
    public long Length => bytes.Length;
    public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
    public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
        ct.ThrowIfCancellationRequested();
        if (timeout)
            throw new TimeoutException("pool full");
        int count = (int)Math.Min(destination.Length, Math.Max(0, bytes.Length - offset));
        bytes.AsMemory((int)Math.Min(offset, bytes.Length), count).CopyTo(destination);
        return ValueTask.FromResult(count);
    }
    public ValueTask DisposeAsync() { if (++DisposeCalls == 1) source.Dispose(); return ValueTask.CompletedTask; }
}
