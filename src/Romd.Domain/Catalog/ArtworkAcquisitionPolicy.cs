namespace Romd.Domain.Catalog;

/// <summary>Shared ordering for acquisition and automatic retained-artwork resolution.</summary>
public static class ArtworkAcquisitionPolicy
{
    public static IReadOnlyList<string> ProviderOrder(ArtworkRole role, IEnumerable<string> configured)
    {
        string[] defaults = role switch
        {
            ArtworkRole.Poster or ArtworkRole.Hero => ["steamgriddb", "igdb"],
            ArtworkRole.Backdrop => ["igdb"],
            ArtworkRole.Logo => ["steamgriddb"],
            _ => []
        };
        return configured.Concat(defaults).Distinct(StringComparer.Ordinal).ToArray();
    }
}
