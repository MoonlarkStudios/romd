using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ConsumerContentArtifactResolverTests
{
    [Fact]
    public async Task ResolveAsync_ExactBoundTarget_UsesItsLogicalReaderWithoutCasFallback()
    {
        var hash = Sha256.Parse(new string('a', 64));
        var cas = Substitute.For<IContentAddressableStore>();
        var roms = Substitute.For<IRomContentReader>();
        using var expected = new MemoryStream([1, 2, 3]);
        var target = new RomDeliveryTarget(7, 9, RomDeliveryFormat.Original, "disc.bin", hash, 3, hash, 3, hash, 3, RomRepresentationEncoding.Identity);
        roms.OpenAsync(target, Arg.Any<CancellationToken>()).Returns(expected);

        var resolved = await new ConsumerContentArtifactResolver(cas, roms).ResolveAsync(target);

        resolved.ShouldBeSameAs(expected);
        await roms.Received(1).OpenAsync(target, default);
        cas.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_BiosArtifactAbsent_DoesNotFallBackToLogicalRomBytes()
    {
        var hash = Sha256.Parse(new string('a', 64));
        var cas = Substitute.For<IContentAddressableStore>();
        var roms = Substitute.For<IRomContentReader>();
        roms.OpenAsync(hash, Arg.Any<CancellationToken>()).Returns(new MemoryStream([1, 2, 3]));

        var resolved = await new ConsumerContentArtifactResolver(cas, roms).ResolveAsync(hash);

        resolved.ShouldBeNull();
        await roms.DidNotReceiveWithAnyArgs().OpenAsync(default(Sha256), default);
    }
}
