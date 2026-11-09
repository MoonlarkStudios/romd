using Romd.Contracts.Common.ReferenceCatalog;

namespace Romd.Application.Common.ReferenceCatalog;

public interface IRegionReferenceReader
{
    Task<IReadOnlyList<RegionResource>> ListAsync(CancellationToken ct);
    Task<ReferenceReadResult<RegionResource>?> GetAsync(string key, CancellationToken ct);
}
