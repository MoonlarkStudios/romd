using Romd.Domain.Source.Rom;
using Romd.Media;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

internal interface IRomMediaAdapter
{
    RomRepresentationEncoding Encoding { get; }
    /// <summary>False when every open must recheck its admitted facts; the decoder then never parks such images idle.</summary>
    bool ReusesIdleImages { get; }
    /// <summary>Claims the access its container needs and owns what it claims, including on failure.</summary>
    /// <param name="source">Disposed by the caller afterwards, which releases anything left unclaimed.</param>
    ValueTask<IRandomAccessContent> OpenAsync(RomArtifactSource source, RomMediaOpenContext context, MediaBlockCache cache,
        CancellationToken ct);
}
