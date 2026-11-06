using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Vfs;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ConsumerVfsContentResolverTests
{
    private static readonly Sha256 Logical = Sha256.Parse(new string('a', 64));
    private static readonly Sha256 Artifact = Sha256.Parse(new string('b', 64));

    [Theory]
    [InlineData(RomDeliveryFormat.Original)]
    [InlineData(RomDeliveryFormat.Rvz)]
    public async Task OpenAsync_BoundFormat_OpensExactTargetInsteadOfHashLookup(RomDeliveryFormat format)
    {
        var cas = Substitute.For<IContentAddressableStore>();
        var roms = Substitute.For<IRomContentReader>();
        var target = Target(format);
        var file = new ConsumerVfsFile("Disc/" + target.Filename, target.Sha256, target.SizeBytes, Target: target);
        var source = new MemoryStream(new byte[target.SizeBytes]);
        roms.OpenSeekableAsync(target, Arg.Any<CancellationToken>()).Returns(source);
        await using var opened = (await new ConsumerVfsContentResolver(cas, roms).OpenAsync(file)).ShouldNotBeNull();
        opened.ShouldBeSameAs(source);
        await roms.Received(1).OpenSeekableAsync(target, Arg.Any<CancellationToken>());
        await roms.DidNotReceive().OpenAsync(Arg.Any<Sha256>(), Arg.Any<CancellationToken>());
        cas.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_BoundTargetWithoutRomReader_FailsClosedWithoutCasFallback()
    {
        var cas = Substitute.For<IContentAddressableStore>();
        var target = Target(RomDeliveryFormat.Rvz);
        (await new ConsumerVfsContentResolver(cas).OpenAsync(new("game.rvz", Artifact, 2, Target: target))).ShouldBeNull();
        cas.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_BoundArtifactDisappears_ReturnsMissingWithoutHashOrCasFallback()
    {
        var cas = Substitute.For<IContentAddressableStore>();
        var roms = Substitute.For<IRomContentReader>();
        var target = Target(RomDeliveryFormat.Rvz);
        var file = new ConsumerVfsFile("Disc/game.rvz", Artifact, 2, Target: target);
        (await new ConsumerVfsContentResolver(cas, roms).OpenAsync(file)).ShouldBeNull();
        await roms.DidNotReceive().OpenAsync(Arg.Any<Sha256>(), Arg.Any<CancellationToken>());
        cas.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_MetadataDisagreesWithTarget_FailsBeforeStorage()
    {
        var cas = Substitute.For<IContentAddressableStore>();
        var roms = Substitute.For<IRomContentReader>();
        var file = new ConsumerVfsFile("game.rvz", Logical, 64, Target: Target(RomDeliveryFormat.Rvz));
        await Should.ThrowAsync<InvalidDataException>(() => new ConsumerVfsContentResolver(cas, roms).OpenAsync(file));
        roms.ReceivedCalls().ShouldBeEmpty();
        cas.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_OpenedLengthDisagrees_DisposesOwnedStream()
    {
        var roms = Substitute.For<IRomContentReader>();
        var target = Target(RomDeliveryFormat.Rvz);
        var source = new MemoryStream(new byte[3]);
        roms.OpenSeekableAsync(target, Arg.Any<CancellationToken>()).Returns(source);
        await Should.ThrowAsync<InvalidDataException>(() =>
            new ConsumerVfsContentResolver(Substitute.For<IContentAddressableStore>(), roms)
                .OpenAsync(new("game.rvz", Artifact, 2, Target: target)));
        source.CanRead.ShouldBeFalse();
    }

    private static RomDeliveryTarget Target(RomDeliveryFormat format) => new(1, 2, format,
        format == RomDeliveryFormat.Rvz ? "game.rvz" : "game.iso",
        format == RomDeliveryFormat.Rvz ? Artifact : Logical, format == RomDeliveryFormat.Rvz ? 2 : 64,
        Logical, 64, Artifact, 2, RomRepresentationEncoding.Rvz,
        ReadBinding: new RomManagedReadBinding(11, 101, Guid.Parse("11111111-2222-3333-4444-555555555555"), "vfs-fixture-managed/v1"));
}
