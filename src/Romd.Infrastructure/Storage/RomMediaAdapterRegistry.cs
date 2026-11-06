using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

public sealed class RomMediaAdapterRegistry
{
    private readonly IRomMediaAdapter[] _adapters;
    public RomMediaAdapterRegistry() : this(RomEncodingComposition.Entries, new RomReadDependencies(null)) { }
    public RomMediaAdapterRegistry(ChdMediaAdapter chd)
        : this(RomEncodingComposition.Entries, new RomReadDependencies(chd ?? throw new ArgumentNullException(nameof(chd)))) { }
    /// <summary>Holds the reader facet of every entry this host can read.</summary>
    internal RomMediaAdapterRegistry(IEnumerable<RomEncodingEntry> entries, RomReadDependencies dependencies)
        : this(entries.Select(entry => entry.Reader(dependencies)).OfType<IRomMediaAdapter>().ToArray()) { }
    internal RomMediaAdapterRegistry(params IRomMediaAdapter[] adapters) => _adapters = adapters;
    internal bool Supports(RomRepresentationEncoding encoding) => _adapters.Any(adapter => adapter.Encoding == encoding);
    internal IRomMediaAdapter For(RomRepresentationEncoding encoding) =>
        _adapters.SingleOrDefault(adapter => adapter.Encoding == encoding) ??
        throw new NotSupportedException($"Unsupported ROM representation: {encoding}.");
}
