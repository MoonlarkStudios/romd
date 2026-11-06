using System.Reflection;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Media.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RomArtifactSourceTests
{
    [Fact]
    public void RomMediaOpenContext_OutsideInfrastructure_CannotBeConstructedOrRewritten()
    {
        // Only the content reader may attach admitted CHD evidence; a public constructor or a public init accessor
        // (which also enables `with`) would let any caller hand the decoder evidence without delivery admission.
        var type = typeof(RomMediaOpenContext);

        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty();
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is { IsPublic: true }).Select(property => property.Name).ShouldBeEmpty();
    }

    [Fact]
    public async Task Claims_OfferOnlyTheOpenedAccessOnce_AndDisposalReleasesOnlyUnclaimed()
    {
        var raw = new ChdObservedRaw(new byte[4]);
        var rawSource = RomArtifactSource.FromSynchronousRaw(raw);
        await Should.ThrowAsync<NotSupportedException>(() => rawSource.OpenRandomAccessAsync().AsTask());
        rawSource.OpenSynchronousRaw().ShouldBeSameAs(raw);
        Should.Throw<NotSupportedException>(() => rawSource.OpenSynchronousRaw());
        await rawSource.DisposeAsync();
        raw.DisposeCalls.ShouldBe(0);

        var unclaimed = new ChdObservedRaw(new byte[4]);
        await RomArtifactSource.FromSynchronousRaw(unclaimed).DisposeAsync();
        unclaimed.DisposeCalls.ShouldBe(1);

        var content = new MemoryContent(new byte[4]);
        var randomSource = RomArtifactSource.FromRandomAccess(content);
        Should.Throw<NotSupportedException>(() => randomSource.OpenSynchronousRaw());
        (await randomSource.OpenRandomAccessAsync()).ShouldBeSameAs(content);
        await Should.ThrowAsync<NotSupportedException>(() => randomSource.OpenRandomAccessAsync().AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodeAsync_ChdFromRandomAccessOrWithoutEvidence_RefusesWithoutFallbackAndReleasesSnapshot(bool randomAccess)
    {
        var fixture = ChdDeliveryFixture.Create();
        var raw = new ChdObservedRaw(fixture.RawBytes);
        var content = new ObservedContent(fixture.RawBytes);
        int verified = 0;
        ReadDecoderOptions limits = new() { MaxConcurrent = 1, MaxIdle = 0, CacheBytes = 8192 };
        await using var chd = new ChdMediaAdapter(new MediaBlockCache(8192), limits, _ => { verified++; return fixture.Native; },
            (source, _, _, _, _, _) => ValueTask.FromResult<IRandomAccessContent>(new ChdObservedDecoded(fixture.LogicalBytes, source)));
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(chd), limits);
        var source = randomAccess ? RomArtifactSource.FromRandomAccess(content) : RomArtifactSource.FromSynchronousRaw(raw);
        var context = new RomMediaOpenContext(fixture.Artifact.ArtifactSha256, randomAccess ? fixture.Evidence : null);

        var refused = randomAccess ? typeof(NotSupportedException) : typeof(InvalidDataException);
        (await Should.ThrowAsync<Exception>(() => decoder.DecodeAsync(RomRepresentationEncoding.Chd, source, context))).ShouldBeOfType(refused);

        verified.ShouldBe(0);
        content.Reads.ShouldBe(0);
        (randomAccess ? content.Disposed : raw.DisposeCalls == 1).ShouldBeTrue();
        await using var next = await decoder.DecodeAsync(RomRepresentationEncoding.Chd,
            RomArtifactSource.FromSynchronousRaw(new ChdObservedRaw(fixture.RawBytes)),
            new RomMediaOpenContext(fixture.Artifact.ArtifactSha256, fixture.Evidence));
        next.Length.ShouldBe(fixture.LogicalBytes.Length);
    }

    private sealed class ObservedContent(byte[] bytes) : IRandomAccessContent
    {
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public long Length => bytes.Length;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        { Reads++; return ValueTask.FromResult(0); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
