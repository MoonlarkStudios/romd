using Romd.Persistence.Entities;

namespace Romd.Persistence.Catalog;

/// <summary>Catalog coverage from the exact independently measured descriptor retained by a layout.</summary>
internal static class OriginalDiscLayoutCatalogCoverage
{
    internal static bool SatisfiedByDescriptor(CatalogReleaseFileEntity requirement, FileEntityPersistence descriptor) =>
        requirement.Sha1 is { IsEmpty: false } sha1 && descriptor.ArtifactSha1 == sha1 && descriptor.Size == requirement.Size;
}
