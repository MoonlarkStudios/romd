using System.Text.Json;
using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Probe;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class InteractiveDecoderObservationsTests
{
    [Fact]
    public async Task DecodeAsync_CapacityFailure_ForwardsExactSourceAndContextAndRetainsCause()
    {
        var inner = Substitute.For<IRomRepresentationDecoder>();
        var raw = Substitute.For<ISynchronousRawContent>();
        await using var source = RomArtifactSource.FromSynchronousRaw(raw);
        var context = new RomMediaOpenContext(Sha256.Parse(new string('a', 64)), admissionTimeout: TimeSpan.FromMilliseconds(123));
        using var cancellation = new CancellationTokenSource();
        var injected = new ResourceCapacityException("Media read capacity is occupied; retry later.");
        inner.DecodeAsync(RomRepresentationEncoding.Chd, source, context, cancellation.Token)
            .Returns(Task.FromException<IRandomAccessContent>(injected));
        var observer = new InteractiveDecoderObservations(inner);

        var actual = await Should.ThrowAsync<ResourceCapacityException>(() => observer.DecodeAsync(
            RomRepresentationEncoding.Chd, source, context, cancellation.Token));

        actual.ShouldBeSameAs(injected);
        await inner.Received(1).DecodeAsync(RomRepresentationEncoding.Chd, source, context, cancellation.Token);
        raw.DidNotReceive().Dispose();
        using var report = JsonDocument.Parse(JsonSerializer.Serialize(observer.Snapshot()));
        report.RootElement.GetProperty("Opens").GetInt64().ShouldBe(1);
        report.RootElement.GetProperty("CapacityRejections").GetProperty("media-reader-slots").GetInt64().ShouldBe(1);
        report.RootElement.GetProperty("MaximumRejectedOpenMilliseconds").GetDouble().ShouldBeGreaterThanOrEqualTo(0);
    }
}
