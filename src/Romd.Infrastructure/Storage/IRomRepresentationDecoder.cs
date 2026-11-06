using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

/// <summary>Takes ownership of the artifact, including on failure. RVZ leases use bounded dedicated workers.</summary>
public interface IRomRepresentationDecoder
{
    /// <summary>Whether this host has a reader for the encoding; callers refuse before opening storage when it has none.</summary>
    bool CanDecode(RomRepresentationEncoding format);

    Task<IRandomAccessContent> DecodeAsync(RomRepresentationEncoding format, IRandomAccessContent source,
        CancellationToken ct = default, Sha256? artifact = null, TimeSpan? admissionTimeout = null);

    /// <summary>Admits every decoded format under one read budget; the adapter claims the access it needs.</summary>
    Task<IRandomAccessContent> DecodeAsync(RomRepresentationEncoding format, RomArtifactSource source,
        RomMediaOpenContext context, CancellationToken ct = default);
}
