using Romd.Application.Common.ReferenceCatalog;

namespace Romd.Host.Endpoints;

internal static class SystemIdentityResolution
{
    internal static async Task<int> RequireSystemAsync(this IReferenceCatalogService service, string key, CancellationToken ct) =>
        await service.ResolveSystemIdAsync(key, ct) ?? throw new BadHttpRequestException($"Unknown system key '{key}'.");
}
