using ErrorOr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>The one entry list per encoding and the descriptor, reader and preparation registries derived from it.</summary>
public sealed class RomEncodingEntryCompositionTests
{
    private static readonly RomPreparationDependencies NoLaunch = new(Substitute.For<IHashingService>(),
        () => throw new InvalidOperationException("Composition does not launch RVZ."),
        () => throw new InvalidOperationException("Composition does not launch original inspection."),
        () => throw new InvalidOperationException("Composition does not launch DVD CHD."),
        () => throw new InvalidOperationException("Composition does not launch CD CHD."));

    [Fact]
    public void Entries_EveryDefinedEncoding_IsDeclaredExactlyOnce()
    {
        var declared = RomEncodingComposition.Entries.Select(entry => entry.Descriptor.Encoding).ToArray();

        declared.Order().ShouldBe(Enum.GetValues<RomRepresentationEncoding>().Order());
    }

    [Fact]
    public void CreateRegistry_ProductionEntries_DescribesEachEncodingWithItsEntryDescriptor()
    {
        var registry = RomEncodingComposition.CreateRegistry();

        foreach (var entry in RomEncodingComposition.Entries)
            registry.Describe(entry.Descriptor.Encoding).ShouldBeSameAs(entry.Descriptor);
    }

    [Fact]
    public void CreateRegistry_EntryDeclaresDifferentRawFlag_DescribeFollowsTheEntry()
    {
        var entries = Replace(RomRepresentationEncoding.Rvz,
            entry => entry with { Descriptor = entry.Descriptor with { RequiresRawStorage = true } });

        RomEncodingComposition.CreateRegistry(entries).Describe(RomRepresentationEncoding.Rvz).RequiresRawStorage.ShouldBeTrue();
        RomEncodingComposition.CreateRegistry().Describe(RomRepresentationEncoding.Rvz).RequiresRawStorage.ShouldBeFalse();
    }

    [Fact]
    public void Registries_SameEntries_ReaderAndPreparationFacetsReachOnlyTheirRegistry()
    {
        var reader = new MarkerReader();
        var preparation = new MarkerPreparation();
        var entries = Replace(RomRepresentationEncoding.Rvz, entry => entry with { Reader = _ => reader, Preparation = _ => [preparation] });
        entries = Replace(entries, RomRepresentationEncoding.Identity, entry => entry with { Reader = _ => null });

        var readers = new RomMediaAdapterRegistry(entries, new RomReadDependencies(null));
        var preparations = new RomPreparationAdapterRegistry(entries, NoLaunch);

        readers.For(RomRepresentationEncoding.Rvz).ShouldBeSameAs(reader);
        readers.Supports(RomRepresentationEncoding.Identity).ShouldBeFalse();
        preparations.Detect("RVZ\u0001"u8, "game.rvz").ShouldBeSameAs(preparation);
        preparations.Detect("plain"u8, "game.bin").ShouldBeOfType<IdentityPreparationAdapter>();
    }

    [Fact]
    public void Registries_OtherFacetThrows_EachRegistryBuildsOnlyItsOwnFacet()
    {
        var entries = RomEncodingComposition.Entries.Select(entry => entry with
        {
            Preparation = _ => throw new InvalidOperationException("Reader composition built a preparation facet.")
        }).ToArray();
        var readerless = RomEncodingComposition.Entries.Select(entry => entry with
        {
            Reader = _ => throw new InvalidOperationException("Preparation composition built a reader facet.")
        }).ToArray();

        new RomMediaAdapterRegistry(entries, new RomReadDependencies(null)).Supports(RomRepresentationEncoding.Rvz).ShouldBeTrue();
        new RomPreparationAdapterRegistry(readerless, NoLaunch).Detect("RVZ\u0001"u8, "game.iso").Encoding
            .ShouldBe(RomRepresentationEncoding.Rvz);
    }

    [Fact]
    public void PreparationFacets_ProductionEntries_FlattenInClaimOrder()
    {
        var adapters = RomEncodingComposition.Entries.SelectMany(entry => entry.Preparation(NoLaunch)).Select(adapter => adapter.GetType());

        adapters.ShouldBe([typeof(RvzPreparationAdapter), typeof(ChdPreparationAdapter), typeof(IsoPreparationAdapter),
            typeof(IdentityPreparationAdapter)]);
    }

    [Fact]
    public async Task Facets_ProductionEntries_AdaptOnlyTheirEntrysEncoding()
    {
        ReadDecoderOptions limits = new() { MaxConcurrent = 1, MaxIdle = 0, CacheBytes = 8192 };
        await using var chd = new ChdMediaAdapter(new MediaBlockCache(8192), limits,
            _ => throw new InvalidOperationException("Composition does not load native code."),
            (_, _, _, _, _, _) => throw new InvalidOperationException("Composition does not open devices."));
        var reads = new RomReadDependencies(chd);

        foreach (var entry in RomEncodingComposition.Entries)
        {
            entry.Reader(reads).ShouldNotBeNull().Encoding.ShouldBe(entry.Descriptor.Encoding);
            entry.Preparation(NoLaunch).ShouldNotBeEmpty();
            entry.Preparation(NoLaunch).ShouldAllBe(adapter => adapter.Encoding == entry.Descriptor.Encoding);
        }
    }

    [Fact]
    public void PreparationSignatures_ProductionEntries_NoSignaturePrefixesAnother()
    {
        var signatures = Signatures().Select(adapter => adapter.Signature.ToArray()).ToArray();

        signatures.ShouldNotBeEmpty();
        for (int i = 0; i < signatures.Length; i++)
            for (int j = 0; j < signatures.Length; j++)
                if (i != j)
                    signatures[i].AsSpan().StartsWith(signatures[j]).ShouldBeFalse();
    }

    [Theory]
    [InlineData("disc.bin")]
    [InlineData("disc.iso")]
    [InlineData("disc.gcm")]
    [InlineData("disc.rvz")]
    [InlineData("disc.chd")]
    public void SelectPrefixLength_SignatureUnderAnyName_ReadsWholeSignatureAndDetectsItsAdapter(string filename)
    {
        var registry = new RomPreparationAdapterRegistry(RomEncodingComposition.Entries, NoLaunch);

        foreach (var adapter in Signatures())
        {
            byte[] upload = new byte[RomPreparationDetection.MaximumPrefixBytes];
            adapter.Signature.CopyTo(upload);
            // The same two extension rounds RomPreparationService.PrepareAsync runs from its initial four bytes.
            int length = 4;
            for (int round = 0; round < 2; round++)
            {
                int selected = RomPreparationDetection.SelectPrefixLength(upload.AsMemory(0, length), filename);
                if (selected <= length)
                    break;
                length = selected;
            }

            length.ShouldBeGreaterThanOrEqualTo(adapter.Signature.Length);
            registry.Detect(upload.AsSpan(0, length), filename).ShouldBeOfType(adapter.GetType());
        }
    }

    [Theory]
    [InlineData("", "disc.bin", 4)]
    [InlineData("MCo", "disc.iso", 32)]
    [InlineData("MCom", "disc.bin", 124)]
    [InlineData("MCom", "disc.rvz", 124)]
    [InlineData("MComp", "disc.iso", 32)]
    [InlineData("MComXXXX", "disc.iso", RomPreparationDetection.MaximumPrefixBytes)]
    [InlineData("MComXXXX", "disc.gcm", 32)]
    [InlineData("MComXXXX", "disc.chd", 124)]
    [InlineData("MComXXXX", "disc.rvz", 4)]
    [InlineData("RVZ\u0001", "disc.chd", 4)]
    [InlineData("RVZ\u0002", "disc.chd", 124)]
    [InlineData("RVZ\u0002", "disc.RVZ", 4)]
    [InlineData("RVZ\u0002", "disc.bin", 4)]
    [InlineData("data", "disc.iso", 32)]
    public void SelectPrefixLength_EdgePrefixes_KeepsEachRouteLength(string prefix, string filename, int expected) =>
        RomPreparationDetection.SelectPrefixLength(System.Text.Encoding.Latin1.GetBytes(prefix), filename).ShouldBe(expected);

    private static IRomPreparationAdapter[] Signatures() => RomEncodingComposition.Entries
        .SelectMany(entry => entry.Preparation(NoLaunch)).Where(adapter => !adapter.Signature.IsEmpty).ToArray();

    [Fact]
    public async Task ProductionEntries_NoNativeAssets_ChdIsUnreadableAndItsPreparationRefuses()
    {
        var readers = new RomMediaAdapterRegistry(RomEncodingComposition.Entries, new RomReadDependencies(null));
        var adapter = new RomPreparationAdapterRegistry(RomEncodingComposition.Entries, NoLaunch with
        {
            Chd = () => throw new NotSupportedException("CHD preparation requires configured native assets."),
            Cd = () => throw new NotSupportedException("CHD preparation requires configured native assets.")
        })
            .Detect("MComprHD"u8, "disc.rvz");

        readers.Supports(RomRepresentationEncoding.Chd).ShouldBeFalse();
        new RomMediaAdapterRegistry().Supports(RomRepresentationEncoding.Chd).ShouldBeFalse();
        adapter.Encoding.ShouldBe(RomRepresentationEncoding.Chd);
        (await Should.ThrowAsync<NotSupportedException>(() => adapter.AdmitAsync(MediaInfrastructureTestServices.ChdV5Prefix(2048), CancellationToken.None)))
            .Message.ShouldBe("CHD preparation requires configured native assets.");
    }

    [Fact]
    public async Task ProductionEntries_HostChdReader_ReadsChdWithThatAdapter()
    {
        ReadDecoderOptions limits = new() { MaxConcurrent = 1, MaxIdle = 0, CacheBytes = 8192 };
        await using var chd = new ChdMediaAdapter(new MediaBlockCache(8192), limits,
            _ => throw new InvalidOperationException("Composition does not load native code."),
            (_, _, _, _, _, _) => throw new InvalidOperationException("Composition does not open devices."));

        new RomMediaAdapterRegistry(RomEncodingComposition.Entries, new RomReadDependencies(chd))
            .For(RomRepresentationEncoding.Chd).ShouldBeSameAs(chd);
    }

    [Fact]
    public async Task ConsumerComposition_ReadOnlyStorage_ReadsWithoutRegisteringPreparationServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddRomdReadOnlyStorage();
        Type[] workerOnly = [typeof(RomPreparationAdapterRegistry), typeof(IRomPreparationService), typeof(RvzVerifier),
            typeof(ChdVerifier), typeof(ChdCdImportInspector), typeof(IRomOriginalMediaIdentifier), typeof(MediaProcessingRuntime), typeof(IMediaWorkerSupervisor)];

        services.Where(descriptor => workerOnly.Contains(descriptor.ServiceType)).ShouldBeEmpty();
        await using var provider = services.BuildServiceProvider();
        var readers = provider.GetRequiredService<RomMediaAdapterRegistry>();
        provider.GetRequiredService<IRomRepresentationDecoder>().ShouldNotBeNull();
        readers.Supports(RomRepresentationEncoding.Rvz).ShouldBeTrue();
        readers.Supports(RomRepresentationEncoding.Identity).ShouldBeTrue();
        readers.Supports(RomRepresentationEncoding.Chd).ShouldBeFalse();
    }

    private static RomEncodingEntry[] Replace(RomRepresentationEncoding encoding, Func<RomEncodingEntry, RomEncodingEntry> change) =>
        Replace(RomEncodingComposition.Entries, encoding, change);

    private static RomEncodingEntry[] Replace(IEnumerable<RomEncodingEntry> entries, RomRepresentationEncoding encoding,
        Func<RomEncodingEntry, RomEncodingEntry> change) =>
        entries.Select(entry => entry.Descriptor.Encoding == encoding ? change(entry) : entry).ToArray();

    private sealed class MarkerReader : IRomMediaAdapter
    {
        public RomRepresentationEncoding Encoding => RomRepresentationEncoding.Rvz;
        public bool ReusesIdleImages => false;
        public ValueTask<IRandomAccessContent> OpenAsync(RomArtifactSource source, RomMediaOpenContext context,
            MediaBlockCache cache, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class MarkerPreparation : IRomPreparationAdapter
    {
        public RomRepresentationEncoding Encoding => RomRepresentationEncoding.Rvz;
        public bool RequiresDatHashes(ReadOnlySpan<byte> prefix) => false;
        public ReadOnlySpan<byte> Signature => RomPreparationDetection.Signature(Encoding).Magic;
        public Task<IRomPreparationAdmission?> AdmitAsync(ReadOnlyMemory<byte> magic, CancellationToken ct) => throw new NotSupportedException();
        public bool Claims(ReadOnlySpan<byte> magic, string filename) => magic.StartsWith("RVZ\u0001"u8);
        public Task<ErrorOr<PreparedRom>> VerifyAsync(ITempFile source, string filename, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
