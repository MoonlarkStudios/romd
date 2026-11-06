using Romd.Admin.Application.MetadataProviders;
namespace Romd.Admin.Application.Enrichment;

/// <summary>
///     Effective availability after managed configuration and credential validation. Contains no credentials.
///     Disabled lists registered adapters that are not enabled so assessments can name them.
/// </summary>
public sealed record EnrichmentProviderAvailability(IReadOnlyList<string> Enabled, IReadOnlyList<string> Configured,
    IReadOnlyList<string> Disabled)
{
    public static async Task<EnrichmentProviderAvailability> ReadAsync(IEnumerable<IProviderIdentityAdapter> adapters, CancellationToken ct)
    {
        var enabled = new List<string>();
        var configured = new List<string>();
        var disabled = new List<string>();
        foreach (var adapter in adapters)
        {
            var availability = await adapter.GetAvailabilityAsync(ct);
            if (!availability.Enabled)
            {
                disabled.Add(adapter.Id);
                continue;
            }
            enabled.Add(adapter.Id);
            if (availability.Configured)
                configured.Add(adapter.Id);
        }
        return new(enabled, configured, disabled);
    }
}
