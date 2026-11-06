using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Source.Rom;

namespace Romd.Infrastructure.Storage;

public sealed class RomPreparationAdapterRegistry
{
    private readonly IRomPreparationAdapter[] _adapters;
    public RomPreparationAdapterRegistry(IHashingService hashing, Func<RvzVerifier> rvz,
        Func<IRomOriginalMediaIdentifier> original, Func<ChdVerifier> dvd, Func<ChdCdImportInspector> cd)
        : this(RomEncodingComposition.Entries, new(hashing, rvz, original, dvd, cd)) { }
    /// <summary>Holds every entry's preparation facet, in entry declaration order.</summary>
    internal RomPreparationAdapterRegistry(IEnumerable<RomEncodingEntry> entries, RomPreparationDependencies dependencies) =>
        _adapters = entries.SelectMany(entry => entry.Preparation(dependencies)).ToArray();
    /// <summary>
    ///     Two phases: a container signature routes whatever the name (CHD magic in disc.rvz is CHD, RVZ magic in
    ///     disc.chd is RVZ); only then do adapters claim by name or inner layout, in declaration order.
    /// </summary>
    internal IRomPreparationAdapter Detect(ReadOnlySpan<byte> magic, string filename)
    {
        foreach (var adapter in _adapters)
            if (!adapter.Signature.IsEmpty && magic.StartsWith(adapter.Signature))
                return adapter;
        foreach (var adapter in _adapters)
            if (adapter.Claims(magic, filename))
                return adapter;
        throw new NotSupportedException("No adapter claims the uploaded media.");
    }
}
