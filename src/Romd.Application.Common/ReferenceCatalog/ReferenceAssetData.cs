using Romd.Contracts.Common.ReferenceCatalog;

namespace Romd.Application.Common.ReferenceCatalog;

public sealed record ReferenceAssetData(string Url, string Sha256, string ContentType, bool Monochrome)
{
    public ReferenceAsset ToContract() => new(Url, Sha256, ContentType, Monochrome);
}
