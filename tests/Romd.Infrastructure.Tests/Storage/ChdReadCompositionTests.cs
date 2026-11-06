using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Actual host registrations with native-free reads; no native or deployment qualification.</summary>
public sealed class ChdReadCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostReads_ConfiguredBudget_PassesTheSingletonCacheToLegacyDecoder(bool readOnly)
    {
        var adapter = new CapturingAdapter();
        var services = Services(readOnly, 1 << 20);
        services.AddSingleton(new RomMediaAdapterRegistry(adapter));
        await using var provider = services.BuildServiceProvider();
        var decoder = provider.GetRequiredService<IRomRepresentationDecoder>();
        var cache = provider.GetRequiredService<MediaBlockCache>();
        await using var decoded = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new Bytes());

        adapter.Cache.ShouldBeSameAs(cache);
        cache.BudgetBytes.ShouldBe(1 << 20);
        provider.GetRequiredService<IRomRepresentationDecoder>().ShouldBeSameAs(decoder);
        decoded.Length.ShouldBe(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostReads_InvalidConfiguredBudget_RefusesBeforeOpeningContent(bool readOnly)
    {
        var adapter = new CapturingAdapter();
        var services = Services(readOnly, -1);
        services.AddSingleton(new RomMediaAdapterRegistry(adapter));
        await using var provider = services.BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<IRomRepresentationDecoder>())
            .Message.ShouldBe("Invalid Romd:MediaRead decoder limits.");
        adapter.Cache.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostReads_NoNativeDescriptor_PreservesIdentityAndRefusesChd(bool readOnly)
    {
        await using var provider = Services(readOnly, 1 << 20).BuildServiceProvider();
        var runtime = provider.GetRequiredService<ChdDeploymentRuntime>();
        var decoder = provider.GetRequiredService<IRomRepresentationDecoder>();
        await using var plain = await decoder.DecodeAsync(RomRepresentationEncoding.Identity, new Bytes());

        plain.Length.ShouldBe(4);
        runtime.Assets.ShouldBeNull();
        provider.GetRequiredService<RomMediaAdapterRegistry>().Supports(RomRepresentationEncoding.Chd).ShouldBeFalse();
        provider.GetRequiredService<IRomChdDeliveryAdmission>().ShouldBeOfType<ChdDeliveryAdmission>();
        Should.Throw<NotSupportedException>(() => provider.GetRequiredService<ChdMediaAdapter>())
            .Message.ShouldBe("CHD reading requires configured native assets.");
    }

    private static ServiceCollection Services(bool readOnly, long cacheBytes)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Romd:MediaRead:CacheBytes"] = cacheBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        if (readOnly)
            services.AddRomdReadOnlyStorage();
        else
            services.AddRomdStorage();
        return services;
    }

    private sealed class CapturingAdapter : IRomMediaAdapter
    {
        public MediaBlockCache? Cache { get; private set; }
        public RomRepresentationEncoding Encoding => RomRepresentationEncoding.Rvz;
        public bool ReusesIdleImages => true;
        public ValueTask<IRandomAccessContent> OpenAsync(RomArtifactSource source, RomMediaOpenContext context,
            MediaBlockCache cache, CancellationToken ct)
        {
            Cache = cache;
            return source.OpenRandomAccessAsync(ct);
        }
    }

    private sealed class Bytes : IRandomAccessContent
    {
        public long Length => 4;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
            => ValueTask.FromResult(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
