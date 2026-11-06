using Romd.Contracts.Common.ReferenceCatalog;

namespace Romd.Application.Common.ReferenceCatalog;

public interface IRegionReferenceReader
{
    Task<IReadOnlyList<RegionResourceDto>> ListAsync(CancellationToken ct);
    Task<ReferenceReadResult<RegionResourceDto>?> GetAsync(string key, CancellationToken ct);
}
