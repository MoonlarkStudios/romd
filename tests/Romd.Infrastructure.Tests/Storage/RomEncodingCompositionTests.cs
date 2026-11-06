using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RomEncodingCompositionTests
{
    [Theory]
    [InlineData(RomRepresentationEncoding.Identity, false)]
    [InlineData(RomRepresentationEncoding.Rvz, false)]
    [InlineData(RomRepresentationEncoding.Chd, true)]
    public void CreateRegistry_ProductionEncodings_OnlyChdRequiresRawStorage(RomRepresentationEncoding encoding, bool raw)
    {
        var descriptor = RomEncodingComposition.CreateRegistry().Describe(encoding);

        descriptor.Encoding.ShouldBe(encoding);
        descriptor.RequiresRawStorage.ShouldBe(raw);
    }

    [Fact]
    public void CreateRegistry_EveryDefinedEncoding_IsDescribed()
    {
        var registry = RomEncodingComposition.CreateRegistry();

        foreach (var encoding in Enum.GetValues<RomRepresentationEncoding>())
            registry.Describe(encoding).Encoding.ShouldBe(encoding);
    }

    [Fact]
    public void AddRomdStorage_SharedComposition_RegistersOneRegistryBuiltDuringComposition()
    {
        var services = new ServiceCollection().AddRomdStorage();

        services.Single(descriptor => descriptor.ServiceType == typeof(RomEncodingRegistry))
            .ImplementationInstance.ShouldNotBeNull("the registry is built during composition so an invalid registration fails startup");
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<RomEncodingRegistry>().Describe(RomRepresentationEncoding.Chd).RequiresRawStorage.ShouldBeTrue();
    }
}
