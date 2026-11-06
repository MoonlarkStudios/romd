using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class RomEncodingRegistryTests
{
    private static readonly RomEncodingDescriptor Identity = new(RomRepresentationEncoding.Identity, false);
    private static readonly RomEncodingDescriptor Rvz = new(RomRepresentationEncoding.Rvz, false);
    private static readonly RomEncodingDescriptor Chd = new(RomRepresentationEncoding.Chd, true);

    [Fact]
    public void Describe_EveryEncodingRegisteredOnce_ReturnsItsOwnDescriptor()
    {
        var registry = new RomEncodingRegistry([Chd, Identity, Rvz]);

        registry.Describe(RomRepresentationEncoding.Identity).ShouldBe(Identity);
        registry.Describe(RomRepresentationEncoding.Rvz).ShouldBe(Rvz);
        registry.Describe(RomRepresentationEncoding.Chd).ShouldBe(Chd);
    }

    [Fact]
    public void Constructor_InvalidRegistration_FailsComposition()
    {
        Should.Throw<ArgumentNullException>(() => new RomEncodingRegistry(null!));
        Should.Throw<ArgumentException>(() => new RomEncodingRegistry([]));
        Should.Throw<ArgumentException>(() => new RomEncodingRegistry([Identity, Rvz]));
        Should.Throw<ArgumentException>(() => new RomEncodingRegistry([Identity, Rvz, Chd, Chd with { RequiresRawStorage = false }]));
        Should.Throw<ArgumentException>(() => new RomEncodingRegistry([Identity, Rvz, Rvz with { RequiresRawStorage = true }]));
        Should.Throw<ArgumentException>(() => new RomEncodingRegistry([Identity, Rvz, Chd, null!]));
        Should.Throw<ArgumentException>(() => new RomEncodingRegistry([Identity, Rvz,
            new RomEncodingDescriptor((RomRepresentationEncoding)99, false)]));
    }

    [Fact]
    public void Describe_UndefinedEncoding_FailsClosed()
    {
        var registry = new RomEncodingRegistry([Identity, Rvz, Chd]);

        Should.Throw<ArgumentOutOfRangeException>(() => registry.Describe((RomRepresentationEncoding)99));
    }
}
