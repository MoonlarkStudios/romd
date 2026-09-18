using ErrorOr;
using Romd.Contracts.Common.ReferenceCatalog;

namespace Romd.Application.Common.ReferenceCatalog;

public sealed record ReferenceAssetContent(byte[] Bytes, string ContentType);

public interface IReferenceCatalogService
{
    Task<Romd.Application.Common.Systems.SystemKeys> GetSystemKeysAsync(CancellationToken ct);
    Task<int?> ResolveSystemIdAsync(string key, CancellationToken ct);
    Task<ReferenceCatalogDto?> GetCurrentAsync(CancellationToken ct);
    Task<int> ReleaseExpiredAssetsAsync(DateTimeOffset now, CancellationToken ct);
    Task<ReferenceAssetContent?> GetAssetAsync(string hash, CancellationToken ct);
    Task<ErrorOr<string>> AddAssetAsync(byte[] bytes, string contentType, CancellationToken ct);
}
